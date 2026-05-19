using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Services;

public sealed record ImportResult(
    IReadOnlyList<string> ProducedFiles,
    string BackupFolder,
    bool CleanupDeferred = false,
    DateTime? ResumeAt = null,
    string? CleanupMessage = null);

// Owns the import workflow end to end: fetch from Kuali, write the OnBase DIP
// files, copy backups, and coordinate the optional cleanup step. The supporting
// helper classes stay in this file so developers can read the full path without
// bouncing through a dozen tiny files.
public sealed class ImportService
{
    private static readonly TimeSpan CleanupGracePeriod = TimeSpan.FromMinutes(2);

    private readonly IKualiClient _kuali;
    private readonly BackupService _backup;
    private readonly JobEventLog _events;
    private readonly AppSettings.ImportSettings _importOptions;
    private readonly JobsService _jobs;
    private readonly ILogger<ImportService> _log;

    public ImportService(
        IKualiClient kuali,
        BackupService backup,
        JobEventLog events,
        JobsService jobs,
        IOptions<AppSettings> settings,
        ILogger<ImportService> log)
    {
        _kuali = kuali;
        _backup = backup;
        _events = events;
        _jobs = jobs;
        _importOptions = settings.Value.Import;
        _log = log;
    }

    public async Task<ImportResult> RunAsync(ImportJob job, CancellationToken ct)
    {
        ValidateDownloadMode(job.DownloadMode);

        var alreadyDelivered = TryGetCompletedDelivery(job, out var savedDelivery);
        var delivery = alreadyDelivered
            ? savedDelivery!
            : await DeliverAsync(job, ct);

        if (!IsCleanupRequested(job))
        {
            await MarkImportSucceededAsync(job, delivery, ct);
            return delivery;
        }

        if (!alreadyDelivered)
        {
            var readyAt = DateTime.UtcNow.Add(CleanupGracePeriod);
            await _events.LogAsync(job.Id, JobEventKind.CleanupDeferred,
                $"Cleanup deferred until {readyAt:o}",
                new { job.DeleteAttachments, job.DeleteDocument, CleanupReadyAt = readyAt },
                ct);

            return DeferCleanup(delivery, readyAt, "Import succeeded; cleanup deferred during grace period.");
        }

        if (await _jobs.CleanupAlreadySucceededInWindowAsync(
            job.DocumentId,
            job.CreatedAt,
            CleanupGracePeriod,
            ct))
        {
            await MarkImportSucceededAsync(job, delivery, ct);
            return delivery;
        }

        if (job.NextAttemptAt is not null && DateTime.UtcNow < job.NextAttemptAt.Value)
        {
            return DeferCleanup(delivery, job.NextAttemptAt, "Import succeeded; cleanup still in grace period.");
        }

        if (await _jobs.IsSiblingStillImportingAsync(
            job.DocumentId,
            job.DownloadMode,
            job.CreatedAt,
            CleanupGracePeriod,
            ct))
        {
            return DeferCleanup(
                delivery,
                resumeAt: null,
                "Cleanup is waiting for both pdf and attachments imports to complete.");
        }

        var cleanup = await _jobs.GetCleanupRequestInWindowAsync(
            job.DocumentId,
            job.CreatedAt,
            CleanupGracePeriod,
            ct);

        await CompleteCleanupAsync(job, cleanup.DeleteAttachments, cleanup.DeleteDocument, ct);
        await MarkImportSucceededAsync(job, delivery, ct);
        return delivery;
    }

    private static void ValidateDownloadMode(string mode)
    {
        if (mode != "pdf" && mode != "attachments")
        {
            throw new ArgumentException($"Invalid downloadMode '{mode}'. Expected pdf|attachments.");
        }
    }

    private static ImportResult DeferCleanup(
        ImportResult delivery,
        DateTime? resumeAt,
        string message) =>
        delivery with
        {
            CleanupDeferred = true,
            ResumeAt = resumeAt,
            CleanupMessage = message,
        };

    private async Task<ImportResult> DeliverAsync(ImportJob job, CancellationToken ct)
    {
        var attachmentsMode = job.DownloadMode == "attachments";

        // Allow-list check before touching the filesystem — otherwise a bad
        // targetFolderPath could point at /etc, /, or a sibling share.
        ValidateTargetPath(job.TargetFolderPath);

        if (!Directory.Exists(job.TargetFolderPath))
        {
            throw new DirectoryNotFoundException(
                $"targetFolderPath '{job.TargetFolderPath}' does not exist or is not reachable.");
        }

        // Write-probe catches read-only mounts and perms issues before we burn
        // a Kuali export call and download 100 MB of PDFs.
        var probePath = Path.Combine(job.TargetFolderPath, $".kuali2ob-probe-{Guid.NewGuid():N}");
        var probeWrote = false;
        try
        {
            await File.WriteAllTextAsync(probePath, "probe", ct);
            probeWrote = true;
            File.Delete(probePath);
            probeWrote = false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"targetFolderPath '{job.TargetFolderPath}' is not writable by this process: {ex.Message}", ex);
        }
        finally
        {
            if (probeWrote)
            {
                try { File.Delete(probePath); } catch { /* best effort */ }
            }
        }

        await _events.LogAsync(job.Id, JobEventKind.ImportStarted,
            $"mode={job.DownloadMode}, target={job.TargetFolderPath}",
            new { job.DocumentId, job.OnBaseDocType, job.DownloadMode, job.TargetFolderPath, job.DeleteAttachments, job.DeleteDocument },
            ct);

        var document = await _kuali.GetDocumentAsync(job.DocumentId, ct);
        await _events.LogAsync(job.Id, JobEventKind.DocumentFetched,
            $"serial={document.SerialNumber}, attachments={document.Attachments.Count}",
            new
            {
                document.Id,
                document.SerialNumber,
                document.FirstName,
                document.LastName,
                document.SchoolId,
                Attachments = document.Attachments.Select(a => new
                {
                    a.Id,
                    a.FieldPath,
                    a.FileName,
                    HasUrl = !string.IsNullOrWhiteSpace(a.Url),
                }),
                RawData = ParseRawData(document.RawDataJson),
            },
            ct);

        var tempRoot = Path.Combine(Path.GetTempPath(), $"kuali-onbase-{job.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var stagedFiles = await DownloadAsync(job, document, attachmentsMode, tempRoot, ct);
            if (stagedFiles.Count == 0)
            {
                var detail = attachmentsMode
                    ? "Download mode was 'attachments' but the Kuali document returned 0 attachments. Either the form has no uploaded files or the attachment fields aren't in a shape the walker recognizes (see DocumentFetched → RawData payload above)."
                    : "No files were produced for this import.";
                throw new InvalidOperationException(detail);
            }

            var externalSourceRefBase = job.DocumentId;

            var finalNames = BuildFinalNames(stagedFiles, job.DocumentId);
            await _events.LogAsync(job.Id, JobEventKind.FilesRenamed,
                $"{finalNames.Count} file(s)",
                finalNames.Select((n, i) => new { Staged = Path.GetFileName(stagedFiles[i]), Final = n }),
                ct);

            var backupFolder = _backup.CreateBackupFolder(job.DocumentId, DateTime.UtcNow);
            await _events.LogAsync(job.Id, JobEventKind.BackupCreated,
                backupFolder,
                new { BackupFolder = backupFolder },
                ct);

            var producedFiles = new List<string>(finalNames.Count + 1);
            var indexEntries = new List<IndexFileEntry>(finalNames.Count);

            for (var i = 0; i < stagedFiles.Count; i++)
            {
                var src = stagedFiles[i];
                var destName = finalNames[i];
                var destPath = Path.Combine(job.TargetFolderPath, destName);

                await _backup.CopyAsync(src, backupFolder, ct);

                File.Copy(src, destPath, overwrite: true);
                producedFiles.Add(destPath);

                var refSuffix = finalNames.Count > 1 ? $"_{i + 1}" : string.Empty;
                indexEntries.Add(new IndexFileEntry(destName, $"{externalSourceRefBase}{refSuffix}"));
            }

            await _events.LogAsync(job.Id, JobEventKind.FilesCopiedToTarget,
                $"{producedFiles.Count} file(s) → {job.TargetFolderPath}",
                new { Target = job.TargetFolderPath, Files = producedFiles },
                ct);

            var keywords = DeserializeKeywords(job.KeywordsJson);
            var indexContent = IndexFileBuilder.Build(job.OnBaseDocType, indexEntries, keywords);
            var indexFileName = FileNameSanitizer.Sanitize(externalSourceRefBase) + ".txt";
            var indexPath = Path.Combine(job.TargetFolderPath, indexFileName);
            await File.WriteAllTextAsync(indexPath, indexContent, ct);
            await _backup.CopyAsync(indexPath, backupFolder, ct);
            producedFiles.Add(indexPath);

            await _events.LogAsync(job.Id, JobEventKind.IndexFileWritten,
                indexPath,
                new { Path = indexPath, Content = indexContent },
                ct);

            _log.LogInformation(
                "Import job {JobId} delivered {FileCount} file(s) to {Target}",
                job.Id, producedFiles.Count, job.TargetFolderPath);

            return new ImportResult(producedFiles, backupFolder);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            await _events.LogAsync(job.Id, JobEventKind.ImportFailed,
                ex.Message,
                new { Error = ex.Message, Type = ex.GetType().Name },
                ct);
            throw;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<List<string>> DownloadAsync(
        ImportJob job,
        KualiDocument document,
        bool attachmentsMode,
        string tempRoot,
        CancellationToken ct)
    {
        // `attachments` downloads raw files (preserves .docx/.jpg/…).
        // `pdf` uses Kuali's exportDocument; merge behavior is controlled by the
        // Kuali tenant setting "Include PDFs uploaded through the form" (see README).
        if (attachmentsMode)
        {
            return await DownloadRawAttachmentsAsync(job, document, tempRoot, ct);
        }

        var kualiOptions = new[] { "Combined" };

        await _events.LogAsync(job.Id, JobEventKind.ExportRequested,
            $"requesting Kuali export for {document.Id} (options=[{string.Join(",", kualiOptions)}])",
            new { document.Id, Options = kualiOptions },
            ct);

        var url = await _kuali.ExportPdfAsync(document.Id, kualiOptions, ct);

        await _events.LogAsync(job.Id, JobEventKind.ExportCallbackReceived,
            "signed URL received",
            new { HasSignedUrl = !string.IsNullOrWhiteSpace(url) },
            ct);

        var pdfPath = Path.Combine(tempRoot, $"export-{document.Id}.pdf");
        await _kuali.DownloadToFileAsync(url, pdfPath, ct);
        var size = new FileInfo(pdfPath).Length;

        await _events.LogAsync(job.Id, JobEventKind.PdfDownloaded,
            $"{size:N0} bytes",
            new { Path = pdfPath, Bytes = size },
            ct);

        return new List<string> { pdfPath };
    }

    private async Task<List<string>> DownloadRawAttachmentsAsync(
        ImportJob job,
        KualiDocument document,
        string tempRoot,
        CancellationToken ct)
    {
        var files = new List<string>(document.Attachments.Count);
        for (var i = 0; i < document.Attachments.Count; i++)
        {
            var att = document.Attachments[i];
            var ext = Path.GetExtension(att.FileName);
            var local = Path.Combine(tempRoot, $"attach-{i}{ext}");
            await _kuali.DownloadToFileAsync(att.Url, local, ct);
            var size = new FileInfo(local).Length;

            await _events.LogAsync(job.Id, JobEventKind.AttachmentDownloaded,
                $"{att.FileName} ({size:N0} bytes)",
                new
                {
                    att.FieldPath,
                    att.FileName,
                    HasUrl = !string.IsNullOrWhiteSpace(att.Url),
                    Bytes = size,
                },
                ct);

            files.Add(local);
        }
        return files;
    }

    private static List<string> BuildFinalNames(
        IReadOnlyList<string> stagedFiles,
        string documentId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(stagedFiles.Count);

        foreach (var staged in stagedFiles)
        {
            var ext = Path.GetExtension(staged).TrimStart('.');
            var baseName = FileNameSanitizer.BuildContentFileName(documentId, ext);
            result.Add(FileNameSanitizer.MakeUnique(baseName, seen));
        }

        return result;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> DeserializeKeywords(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var pairs = JsonSerializer.Deserialize<List<KeywordDto>>(json);
            return pairs is null
                ? []
                : pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }

    private async Task CompleteCleanupAsync(
        ImportJob job,
        bool deleteAttachments,
        bool deleteDocument,
        CancellationToken ct)
    {
        var document = await _kuali.GetDocumentAsync(job.DocumentId, ct);

        if (deleteAttachments && document.Attachments.Count > 0)
        {
            var fieldPaths = document.Attachments.Select(a => a.FieldPath).ToList();
            await _kuali.ClearAttachmentsAsync(document.Id, fieldPaths, ct);
            await _events.LogAsync(job.Id, JobEventKind.AttachmentsCleared,
                $"{fieldPaths.Count} field(s)",
                new { FieldPaths = fieldPaths },
                ct);
        }

        if (deleteDocument)
        {
            await _kuali.DeleteDocumentAsync(document.Id, ct);
            await _events.LogAsync(job.Id, JobEventKind.DocumentDeleted,
                $"document {document.Id} deleted",
                new { document.Id },
                ct);
        }
    }

    private async Task MarkImportSucceededAsync(
        ImportJob job,
        ImportResult delivery,
        CancellationToken ct)
    {
        await _events.LogAsync(job.Id, JobEventKind.ImportSucceeded,
            $"{delivery.ProducedFiles.Count} file(s) delivered",
            new { Files = delivery.ProducedFiles, BackupFolder = delivery.BackupFolder },
            ct);
    }

    private static bool TryGetCompletedDelivery(ImportJob job, out ImportResult? delivery)
    {
        delivery = null;
        if (string.IsNullOrWhiteSpace(job.BackupFolderPath) || string.IsNullOrWhiteSpace(job.ProducedFiles))
        {
            return false;
        }

        try
        {
            var files = JsonSerializer.Deserialize<List<string>>(job.ProducedFiles);
            if (files is null || files.Count == 0)
            {
                return false;
            }

            delivery = new ImportResult(files, job.BackupFolderPath);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsCleanupRequested(ImportJob job) =>
        job.DeleteAttachments || job.DeleteDocument;

    private void ValidateTargetPath(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new ArgumentException("targetFolderPath is required.", nameof(requested));
        }

        var roots = _importOptions.ParseAllowedRoots();
        if (roots.Count == 0)
        {
            throw new InvalidOperationException(
                "Import:AllowedTargetRoots is not configured; refusing to write to any targetFolderPath.");
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(requested);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"targetFolderPath '{requested}' is not a valid path: {ex.Message}", nameof(requested), ex);
        }

        // OnBase SMB shares are case-insensitive on Windows hosts.
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var root in roots)
        {
            // Trailing separator on both sides so `/allowed` doesn't accept `/allowed-sibling`.
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedPath = canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (normalizedPath.StartsWith(normalizedRoot, cmp)) return;
        }

        throw new ArgumentException(
            $"targetFolderPath '{requested}' is not under any configured Import:AllowedTargetRoots.",
            nameof(requested));
    }

    private sealed record KeywordDto(string Key, string Value);

    // The raw-data JSON lives inside another JSON payload (the event payload),
    // so we parse it once to a JsonNode to keep it as a real object — otherwise
    // the viewer sees an escaped string-of-JSON and can't drill into it.
    private static System.Text.Json.Nodes.JsonNode? ParseRawData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.Nodes.JsonNode.Parse(json); }
        catch { return null; }
    }
}

public sealed class BackupService
{
    // PurgeOlderThan parses the leading 15 chars back out — we can't trust
    // Directory.GetCreationTimeUtc because SMB/CIFS and some bind-mount setups
    // reset creation time on every touch, which would keep backups alive forever.
    private const string BackupNameTimestampFormat = "yyyyMMdd_HHmmss";

    private readonly AppSettings.BackupSettings _options;
    private readonly ILogger<BackupService> _log;

    public BackupService(IOptions<AppSettings> options, ILogger<BackupService> log)
    {
        _options = options.Value.Backup;
        _log = log;
    }

    public string CreateBackupFolder(string documentId, DateTime timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(_options.RootPath))
        {
            throw new InvalidOperationException("Backup:RootPath is not configured.");
        }

        var safeId = FileNameSanitizer.Sanitize(documentId);
        var name = $"{timestampUtc.ToString(BackupNameTimestampFormat, CultureInfo.InvariantCulture)}_{safeId}";
        var path = Path.Combine(_options.RootPath, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task CopyAsync(string sourcePath, string backupFolder, CancellationToken ct)
    {
        Directory.CreateDirectory(backupFolder);
        var dest = Path.Combine(backupFolder, Path.GetFileName(sourcePath));
        await using var src = File.OpenRead(sourcePath);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct);
    }

    public void PurgeOlderThan(DateTime cutoffUtc)
    {
        if (string.IsNullOrWhiteSpace(_options.RootPath) || !Directory.Exists(_options.RootPath))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(_options.RootPath))
        {
            try
            {
                var leaf = Path.GetFileName(dir);
                // Skip folders we didn't create (operator-dropped diagnostics etc.).
                if (!TryParseFolderTimestamp(leaf, out var stamp)) continue;

                if (stamp < cutoffUtc)
                {
                    Directory.Delete(dir, recursive: true);
                    _log.LogInformation("Purged expired backup {Folder}", leaf);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to purge backup {Folder}", Path.GetFileName(dir));
            }
        }
    }

    internal static bool TryParseFolderTimestamp(string? folderName, out DateTime timestampUtc)
    {
        timestampUtc = default;
        if (string.IsNullOrEmpty(folderName) || folderName.Length < BackupNameTimestampFormat.Length)
        {
            return false;
        }
        var stampSegment = folderName.Substring(0, BackupNameTimestampFormat.Length);
        if (!DateTime.TryParseExact(
                stampSegment,
                BackupNameTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }
        timestampUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
}
internal static class IndexFileBuilder
{
    public const string ExternalSourceLiteral = "KUALI BUILD";

    public static string Build(
        string onbaseDocType,
        IReadOnlyList<IndexFileEntry> files,
        IReadOnlyList<KeyValuePair<string, string>> keywords)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("At least one file entry is required.", nameof(files));
        }

        // DIP parses line-oriented "KEY: VALUE". Strip control chars from every
        // user-controlled value so a smuggled \r\n can't inject DIP directives
        // (CWE-93 — would otherwise let a caller rewrite ONBASE_DOC_TYPE).
        var safeDocType = StripLineBreaks(onbaseDocType);

        var sb = new StringBuilder();
        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }
            sb.Append("ONBASE_DOC_TYPE: ").AppendLine(safeDocType);
            sb.Append("FILENAME: ").AppendLine(StripLineBreaks(files[i].FileName));
            sb.Append("EXTERNAL_SOURCE: ").AppendLine(ExternalSourceLiteral);
            sb.Append("EXTERNAL_SOURCE_REF: ").AppendLine(StripLineBreaks(files[i].ExternalSourceRef));
            foreach (var pair in keywords)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }
                sb.Append(StripLineBreaks(pair.Key)).Append(": ").AppendLine(StripLineBreaks(pair.Value));
            }
        }
        return sb.ToString();
    }

    internal static string StripLineBreaks(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
}

internal static class FileNameSanitizer
{
    private static readonly char[] Invalid =
        ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0', '\r', '\n', '\t'];

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(Array.IndexOf(Invalid, ch) >= 0 || char.IsControl(ch) ? '-' : ch);
        }

        var cleaned = sb.ToString().Trim(' ', '.', '-', '_');
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    // Filenames are the Kuali documentId. Sanitization is defensive; the id is
    // already URL-safe. Multi-file jobs get `_2`, `_3`, … via MakeUnique.
    public static string BuildContentFileName(string documentId, string extension)
    {
        var stem = Sanitize(documentId);
        var ext = NormalizeExtension(extension);
        return ext.Length == 0 ? stem : $"{stem}.{ext}";
    }

    public static string MakeUnique(string fileName, ISet<string> existing)
    {
        if (existing.Add(fileName))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var counter = 2;
        while (true)
        {
            var candidate = string.IsNullOrEmpty(ext)
                ? $"{stem}_{counter}"
                : $"{stem}_{counter}{ext}";
            if (existing.Add(candidate))
            {
                return candidate;
            }
            counter++;
        }
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }
        var trimmed = extension.Trim().TrimStart('.');
        return Sanitize(trimmed).ToLowerInvariant();
    }
}
