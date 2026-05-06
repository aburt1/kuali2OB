using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using KualiOnBase.Api.Configuration;
using KualiOnBase.Api.Features.Jobs;
using KualiOnBase.Api.Features.Kuali;
using KualiOnBase.Api.Features.Notifications;
using KualiOnBase.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Features.Import;

public static class ImportEndpoint
{
    public const string Route = "/api/kuali-onbase-import";

    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app)
    {
        return app.MapPost(Route, Handle);
    }

    public static async Task<IResult> Handle(
        HttpRequest request,
        [FromQuery] string? documentId,
        [FromQuery] string? onbaseDocType,
        [FromQuery] string? targetFolderPath,
        [FromQuery] string? downloadMode,
        [FromQuery] bool? deleteAttachments,
        [FromQuery] bool? deleteDocument,
        RetryQueue queue,
        ImportOrchestrator orchestrator,
        INotificationService notifications,
        IOptions<RetryOptions> retry,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("ImportEndpoint");

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(documentId)) errors.Add("documentId is required.");
        if (string.IsNullOrWhiteSpace(onbaseDocType)) errors.Add("onbaseDocType is required.");
        if (string.IsNullOrWhiteSpace(targetFolderPath)) errors.Add("targetFolderPath is required.");
        if (string.IsNullOrWhiteSpace(downloadMode))
        {
            errors.Add("downloadMode is required.");
        }
        else if (downloadMode != "pdf" && downloadMode != "attachments")
        {
            errors.Add("downloadMode must be one of: pdf, attachments.");
        }
        if (deleteAttachments is null) errors.Add("deleteAttachments is required.");

        if (errors.Count > 0)
        {
            log.LogWarning("Import request rejected (validation): {Errors}",
                string.Join(" | ", errors));
            return TextError(400, string.Join("\n", errors));
        }

        var keywords = ExtractKeywords(request.Query);

        // Never log request.QueryString — keyword values carry PII.
        log.LogInformation(
            "Import request received: documentId={DocumentId} onbaseDocType={OnBaseDocType} " +
            "downloadMode={DownloadMode} deleteAttachments={DeleteAttachments} " +
            "deleteDocument={DeleteDocument} targetFolderPath={TargetFolderPath} keywordCount={KeywordCount}",
            documentId, onbaseDocType, downloadMode, deleteAttachments,
            deleteDocument, targetFolderPath, keywords.Count);

        var job = new ImportJob
        {
            DocumentId = documentId!,
            OnBaseDocType = onbaseDocType!,
            TargetFolderPath = targetFolderPath!,
            DownloadMode = downloadMode!,
            DeleteAttachments = deleteAttachments ?? false,
            DeleteDocument = deleteDocument ?? false,
            KeywordsJson = JsonSerializer.Serialize(keywords),
            Status = JobStatus.Running,
            AttemptCount = 1,
        };
        await queue.InsertAsync(job, ct);

        try
        {
            var result = await orchestrator.RunAsync(job, ct);
            job.BackupFolderPath = result.BackupFolder;
            job.ProducedFiles = JsonSerializer.Serialize(result.ProducedFiles);
            job.LastError = result.CleanupMessage;

            if (result.CleanupDeferred)
            {
                job.Status = JobStatus.Retrying;
                job.NextAttemptAt = result.ResumeAt;
                await queue.UpdateAsync(job, ct);
                return Results.Accepted(
                    $"{Route}/{job.Id}",
                    new ImportResponse(
                        job.Id, job.Status, result.ProducedFiles, result.BackupFolder,
                        job.AttemptCount, job.NextAttemptAt, result.CleanupMessage));
            }

            job.Status = JobStatus.Succeeded;
            job.NextAttemptAt = null;
            await queue.UpdateAsync(job, ct);

            return Results.Ok(new ImportResponse(
                job.Id, job.Status, result.ProducedFiles, result.BackupFolder,
                job.AttemptCount, null, null));
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            var delay = TimeSpan.FromSeconds(retry.Value.BaseDelaySeconds);
            job.Status = JobStatus.Retrying;
            job.NextAttemptAt = DateTime.UtcNow.Add(delay);
            job.LastError = ex.Message;
            await queue.UpdateAsync(job, ct);

            log.LogWarning(ex,
                "Import job {JobId} hit a transient error; scheduled retry at {NextAttemptAt}",
                job.Id, job.NextAttemptAt);

            return Results.Accepted(
                $"{Route}/{job.Id}",
                new ImportResponse(
                    job.Id, job.Status, [], job.BackupFolderPath,
                    job.AttemptCount, job.NextAttemptAt, ex.Message));
        }
        catch (ArgumentException ex)
        {
            // Caller-error (bad params) — no alert; operator already sees 400.
            await MarkFailedAsync(queue, job, ex, ct);
            log.LogWarning(ex, "Import job {JobId} rejected (bad argument)", job.Id);
            return TextError(400, ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            // Also caller-configurable (wrong path) — no alert.
            await MarkFailedAsync(queue, job, ex, ct);
            log.LogWarning(ex, "Import job {JobId} rejected (target folder)", job.Id);
            return TextError(400, ex.Message);
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(queue, job, ex, ct);
            log.LogError(ex, "Import job {JobId} failed permanently", job.Id);
            await notifications.NotifyJobFailedAsync(job, ct);
            return TextError(500, $"Import failed: {ex.Message}");
        }
    }

    // Kuali's HTTP Action stringifies our response body as "[object Object]" when it's
    // JSON, so operators can't see the actual error. Plain-text bodies render as-is,
    // which makes them readable in Kuali's "response data" dialog.
    private static IResult TextError(int status, string message) =>
        Results.Text(message, contentType: "text/plain; charset=utf-8", statusCode: status);

    private static bool IsTransient(Exception ex) => ex switch
    {
        DirectoryNotFoundException => false,
        FileNotFoundException => false,
        PathTooLongException => false,
        UnauthorizedAccessException => false,
        HttpRequestException => true,
        IOException => true,
        TaskCanceledException => true,
        KualiApiException kex when LooksTransient(kex.Message) => true,
        _ => false,
    };

    private static bool LooksTransient(string message) =>
        message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || message.Contains("503", StringComparison.Ordinal)
        || message.Contains("502", StringComparison.Ordinal)
        || message.Contains("504", StringComparison.Ordinal);

    private static async Task MarkFailedAsync(RetryQueue queue, ImportJob job, Exception ex, CancellationToken ct)
    {
        job.Status = JobStatus.Failed;
        job.LastError = ex.Message;
        await queue.UpdateAsync(job, ct);
    }

    internal static List<KeyValuePair<string, string>> ExtractKeywords(IQueryCollection query)
    {
        var result = new List<KeyValuePair<string, string>>();
        for (var i = 1; i <= 20; i++)
        {
            var key = query[$"KeywordKey{i}"].ToString();
            var value = query[$"KeywordValue{i}"].ToString();
            if (IsIgnoreSentinel(key) || IsIgnoreSentinel(value))
            {
                continue;
            }
            result.Add(new KeyValuePair<string, string>(key, value));
        }
        return result;
    }

    // Kuali Build's HTTP-Action URL editor forces a value into every token, so
    // "unused" keyword slots get filled with a literal "|" by convention — treat
    // it (and whitespace / empty) as the skip-this-pair sentinel.
    private static bool IsIgnoreSentinel(string s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim() == "|";
}

public sealed record ImportOrchestratorResult(
    IReadOnlyList<string> ProducedFiles,
    string BackupFolder,
    bool CleanupDeferred = false,
    DateTime? ResumeAt = null,
    string? CleanupMessage = null);

public sealed class ImportOrchestrator
{
    private static readonly TimeSpan CleanupGracePeriod = TimeSpan.FromMinutes(2);

    private readonly IKualiClient _kuali;
    private readonly BackupService _backup;
    private readonly JobEventLog _events;
    private readonly ImportOptions _importOptions;
    private readonly RetryQueue _queue;
    private readonly ILogger<ImportOrchestrator> _log;

    public ImportOrchestrator(
        IKualiClient kuali,
        BackupService backup,
        JobEventLog events,
        RetryQueue queue,
        IOptions<ImportOptions> importOptions,
        ILogger<ImportOrchestrator> log)
    {
        _kuali = kuali;
        _backup = backup;
        _events = events;
        _queue = queue;
        _importOptions = importOptions.Value;
        _log = log;
    }

    public async Task<ImportOrchestratorResult> RunAsync(ImportJob job, CancellationToken ct)
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

        if (await _queue.CleanupAlreadySucceededInWindowAsync(
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

        if (await _queue.IsSiblingStillImportingAsync(
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

        var cleanup = await _queue.GetCleanupRequestInWindowAsync(
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

    private static ImportOrchestratorResult DeferCleanup(
        ImportOrchestratorResult delivery,
        DateTime? resumeAt,
        string message) =>
        delivery with
        {
            CleanupDeferred = true,
            ResumeAt = resumeAt,
            CleanupMessage = message,
        };

    private async Task<ImportOrchestratorResult> DeliverAsync(ImportJob job, CancellationToken ct)
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

            return new ImportOrchestratorResult(producedFiles, backupFolder);
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
        ImportOrchestratorResult delivery,
        CancellationToken ct)
    {
        await _events.LogAsync(job.Id, JobEventKind.ImportSucceeded,
            $"{delivery.ProducedFiles.Count} file(s) delivered",
            new { Files = delivery.ProducedFiles, BackupFolder = delivery.BackupFolder },
            ct);
    }

    private static bool TryGetCompletedDelivery(ImportJob job, out ImportOrchestratorResult? delivery)
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

            delivery = new ImportOrchestratorResult(files, job.BackupFolderPath);
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

public sealed class RetryQueue
{
    private readonly Db _db;

    public RetryQueue(Db db)
    {
        _db = db;
    }

    public async Task<long> InsertAsync(ImportJob job, CancellationToken ct)
    {
        using var conn = _db.Open();
        job.CreatedAt = DateTime.UtcNow;
        job.UpdatedAt = job.CreatedAt;
        const string sql = """
            INSERT INTO ImportJobs
                (DocumentId, OnBaseDocType, TargetFolderPath, DownloadMode,
                 DeleteAttachments, DeleteDocument, KeywordsJson, Status,
                 AttemptCount, NextAttemptAt, LastError, BackupFolderPath,
                 ProducedFiles, CreatedAt, UpdatedAt)
            VALUES (@DocumentId, @OnBaseDocType, @TargetFolderPath, @DownloadMode,
                    @DeleteAttachments, @DeleteDocument, @KeywordsJson, @Status,
                    @AttemptCount, @NextAttemptAt, @LastError, @BackupFolderPath,
                    @ProducedFiles, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();
        """;
        job.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, job, cancellationToken: ct));
        return job.Id;
    }

    public async Task UpdateAsync(ImportJob job, CancellationToken ct)
    {
        using var conn = _db.Open();
        job.UpdatedAt = DateTime.UtcNow;
        const string sql = """
            UPDATE ImportJobs SET
                Status            = @Status,
                AttemptCount      = @AttemptCount,
                NextAttemptAt     = @NextAttemptAt,
                LastError         = @LastError,
                BackupFolderPath  = @BackupFolderPath,
                ProducedFiles     = @ProducedFiles,
                UpdatedAt         = @UpdatedAt
            WHERE Id = @Id;
        """;
        await conn.ExecuteAsync(new CommandDefinition(sql, job, cancellationToken: ct));
    }

    public async Task<ImportJob?> GetAsync(long id, CancellationToken ct)
    {
        using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<ImportJob>(
            new CommandDefinition("SELECT * FROM ImportJobs WHERE Id = @Id;", new { Id = id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportJob>> DueForRetryAsync(DateTime nowUtc, CancellationToken ct)
    {
        using var conn = _db.Open();
        var rows = await conn.QueryAsync<ImportJob>(new CommandDefinition(
            """
            SELECT * FROM ImportJobs
            WHERE Status = 'Retrying' AND NextAttemptAt <= @Now
            ORDER BY NextAttemptAt
            LIMIT 25;
            """,
            new { Now = nowUtc },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> DeleteSucceededOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        using var conn = _db.Open();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ImportJobs WHERE Status = 'Succeeded' AND UpdatedAt < @Cutoff;",
            new { Cutoff = cutoffUtc },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportJob>> ListRecentAsync(int limit, CancellationToken ct)
    {
        using var conn = _db.Open();
        var rows = await conn.QueryAsync<ImportJob>(new CommandDefinition(
            "SELECT * FROM ImportJobs ORDER BY Id DESC LIMIT @Limit;",
            new { Limit = Math.Clamp(limit, 1, 500) },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> IsSiblingStillImportingAsync(
        string documentId,
        string downloadMode,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        var siblingMode = GetSiblingMode(downloadMode);
        if (siblingMode is null || !await HasJobModeInWindowAsync(documentId, siblingMode, anchorUtc, window, ct))
        {
            return false;
        }

        return !await HasDeliveredModeInWindowAsync(documentId, "pdf", anchorUtc, window, ct)
            || !await HasDeliveredModeInWindowAsync(documentId, "attachments", anchorUtc, window, ct);
    }

    public async Task<bool> CleanupAlreadySucceededInWindowAsync(
        string documentId,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        using var conn = _db.Open();
        var (startUtc, endUtc) = GetWindow(anchorUtc, window);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
              FROM ImportJobs
             WHERE DocumentId = @DocumentId
               AND CreatedAt BETWEEN @StartUtc AND @EndUtc
               AND Status = 'Succeeded'
               AND (DeleteAttachments = 1 OR DeleteDocument = 1);
            """,
            new
            {
                DocumentId = documentId,
                StartUtc = startUtc,
                EndUtc = endUtc,
            },
            cancellationToken: ct));
        return count > 0;
    }

    public async Task<(bool DeleteAttachments, bool DeleteDocument)> GetCleanupRequestInWindowAsync(
        string documentId,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        using var conn = _db.Open();
        var (startUtc, endUtc) = GetWindow(anchorUtc, window);
        var row = await conn.QuerySingleAsync(new CommandDefinition(
            """
            SELECT
                MAX(CASE WHEN DeleteAttachments = 1 THEN 1 ELSE 0 END) AS DeleteAttachments,
                MAX(CASE WHEN DeleteDocument = 1 THEN 1 ELSE 0 END) AS DeleteDocument
              FROM ImportJobs
             WHERE DocumentId = @DocumentId
               AND CreatedAt BETWEEN @StartUtc AND @EndUtc
               AND BackupFolderPath IS NOT NULL
               AND ProducedFiles IS NOT NULL;
            """,
            new
            {
                DocumentId = documentId,
                StartUtc = startUtc,
                EndUtc = endUtc,
            },
            cancellationToken: ct));

        return (Convert.ToInt32(row.DeleteAttachments) == 1, Convert.ToInt32(row.DeleteDocument) == 1);
    }

    private async Task<bool> HasJobModeInWindowAsync(
        string documentId,
        string downloadMode,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        using var conn = _db.Open();
        var (startUtc, endUtc) = GetWindow(anchorUtc, window);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
              FROM ImportJobs
             WHERE DocumentId = @DocumentId
               AND DownloadMode = @DownloadMode
               AND CreatedAt BETWEEN @StartUtc AND @EndUtc;
            """,
            new
            {
                DocumentId = documentId,
                DownloadMode = downloadMode,
                StartUtc = startUtc,
                EndUtc = endUtc,
            },
            cancellationToken: ct));
        return count > 0;
    }

    private async Task<bool> HasDeliveredModeInWindowAsync(
        string documentId,
        string downloadMode,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        using var conn = _db.Open();
        var (startUtc, endUtc) = GetWindow(anchorUtc, window);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
              FROM ImportJobs
             WHERE DocumentId = @DocumentId
               AND DownloadMode = @DownloadMode
               AND CreatedAt BETWEEN @StartUtc AND @EndUtc
               AND BackupFolderPath IS NOT NULL
               AND ProducedFiles IS NOT NULL;
            """,
            new
            {
                DocumentId = documentId,
                DownloadMode = downloadMode,
                StartUtc = startUtc,
                EndUtc = endUtc,
            },
            cancellationToken: ct));
        return count > 0;
    }

    private static (DateTime StartUtc, DateTime EndUtc) GetWindow(DateTime anchorUtc, TimeSpan window) =>
        (anchorUtc - window, anchorUtc + window);

    private static string? GetSiblingMode(string mode) => mode switch
    {
        "pdf" => "attachments",
        "attachments" => "pdf",
        _ => null,
    };
}

public sealed class RetryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RetryOptions _options;
    private readonly ILogger<RetryWorker> _log;

    public RetryWorker(
        IServiceScopeFactory scopes,
        IOptions<RetryOptions> options,
        ILogger<RetryWorker> log)
    {
        _scopes = scopes;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "RetryWorker loop failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessDueJobsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<RetryQueue>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ImportOrchestrator>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var due = await queue.DueForRetryAsync(DateTime.UtcNow, ct);
        foreach (var job in due)
        {
            if (ct.IsCancellationRequested) break;
            await RunJobAsync(job, queue, orchestrator, notifications, ct);
        }
    }

    private async Task RunJobAsync(
        ImportJob job,
        RetryQueue queue,
        ImportOrchestrator orchestrator,
        INotificationService notifications,
        CancellationToken ct)
    {
        job.AttemptCount += 1;
        job.Status = JobStatus.Running;
        job.UpdatedAt = DateTime.UtcNow;
        await queue.UpdateAsync(job, ct);

        try
        {
            var result = await orchestrator.RunAsync(job, ct);
            job.BackupFolderPath = result.BackupFolder;
            job.ProducedFiles = JsonSerializer.Serialize(result.ProducedFiles);

            if (result.CleanupDeferred)
            {
                job.Status = JobStatus.Retrying;
                job.LastError = result.CleanupMessage;
                job.NextAttemptAt = result.ResumeAt ?? DateTime.UtcNow.Add(Backoff(job.AttemptCount));
                await queue.UpdateAsync(job, ct);
                _log.LogInformation(
                    "Retry job {JobId} deferred cleanup; next check at {NextAttemptAt}",
                    job.Id, job.NextAttemptAt);
                return;
            }

            job.Status = JobStatus.Succeeded;
            job.LastError = null;
            job.NextAttemptAt = null;
            await queue.UpdateAsync(job, ct);
            _log.LogInformation("Retry job {JobId} succeeded on attempt {Attempt}", job.Id, job.AttemptCount);
        }
        catch (Exception ex)
        {
            var exhausted = job.AttemptCount >= _options.MaxAttempts;
            if (exhausted)
            {
                job.Status = JobStatus.Failed;
                job.LastError = ex.Message;
                job.NextAttemptAt = null;
                _log.LogError(ex,
                    "Retry job {JobId} exhausted {Max} attempts; marked Failed",
                    job.Id, _options.MaxAttempts);
            }
            else
            {
                job.Status = JobStatus.Retrying;
                job.LastError = ex.Message;
                job.NextAttemptAt = DateTime.UtcNow.Add(Backoff(job.AttemptCount));
                _log.LogWarning(ex,
                    "Retry job {JobId} failed (attempt {Attempt}); next at {NextAttemptAt}",
                    job.Id, job.AttemptCount, job.NextAttemptAt);
            }
            await queue.UpdateAsync(job, ct);

            if (exhausted)
            {
                await notifications.NotifyJobFailedAsync(job, ct);
            }
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = _options.BaseDelaySeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(seconds, TimeSpan.FromHours(1).TotalSeconds);
        return TimeSpan.FromSeconds(capped);
    }
}

public sealed class BackupService
{
    // PurgeOlderThan parses the leading 15 chars back out — we can't trust
    // Directory.GetCreationTimeUtc because SMB/CIFS and some bind-mount setups
    // reset creation time on every touch, which would keep backups alive forever.
    private const string BackupNameTimestampFormat = "yyyyMMdd_HHmmss";

    private readonly BackupOptions _options;
    private readonly ILogger<BackupService> _log;

    public BackupService(IOptions<BackupOptions> options, ILogger<BackupService> log)
    {
        _options = options.Value;
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

public sealed class BackupCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BackupOptions _backup;
    private readonly RetryOptions _retry;
    private readonly ILogger<BackupCleanupWorker> _log;

    public BackupCleanupWorker(
        IServiceScopeFactory scopes,
        IOptions<BackupOptions> backup,
        IOptions<RetryOptions> retry,
        ILogger<BackupCleanupWorker> log)
    {
        _scopes = scopes;
        _backup = backup.Value;
        _retry = retry.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _backup.CleanupIntervalHours));
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Backup cleanup iteration failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
        var queue = scope.ServiceProvider.GetRequiredService<RetryQueue>();
        var callbacks = scope.ServiceProvider.GetRequiredService<IExportCallbackStore>();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        var backupCutoff = DateTime.UtcNow.AddDays(-Math.Max(0, _backup.RetentionDays));
        backup.PurgeOlderThan(backupCutoff);

        var prunedRows = 0;
        if (_retry.SucceededJobRetentionDays > 0)
        {
            var jobCutoff = DateTime.UtcNow.AddDays(-_retry.SucceededJobRetentionDays);
            prunedRows = await queue.DeleteSucceededOlderThanAsync(jobCutoff, ct);
            if (prunedRows > 0)
            {
                _log.LogInformation("Pruned {Count} succeeded ImportJobs older than {Cutoff:o}", prunedRows, jobCutoff);
            }
        }

        // ExportCallbacks is grow-only — Pending rows are orphaned when Kuali
        // never calls back. 7 days is enough to chase a weird export after the weekend.
        try
        {
            var callbackCutoff = DateTime.UtcNow.AddDays(-7);
            var prunedCallbacks = await callbacks.DeleteOlderThanAsync(callbackCutoff, ct);
            if (prunedCallbacks > 0)
            {
                _log.LogInformation("Pruned {Count} ExportCallbacks older than {Cutoff:o}", prunedCallbacks, callbackCutoff);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ExportCallbacks prune failed");
        }

        if (prunedRows > 0)
        {
            try
            {
                db.Vacuum();
                _log.LogInformation("Vacuumed DB after pruning {Count} row(s).", prunedRows);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "VACUUM failed; reclaim deferred to next run.");
            }
        }
    }
}

public sealed class ImportJob
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string OnBaseDocType { get; set; } = string.Empty;
    public string TargetFolderPath { get; set; } = string.Empty;
    public string DownloadMode { get; set; } = string.Empty;
    public bool DeleteAttachments { get; set; }
    public bool DeleteDocument { get; set; }
    public string? KeywordsJson { get; set; }
    public string Status { get; set; } = JobStatus.Running;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? BackupFolderPath { get; set; }
    public string? ProducedFiles { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class JobStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Retrying = "Retrying";
}

public sealed record ImportResponse(
    long JobId,
    string Status,
    IReadOnlyList<string> Files,
    string? BackupFolder,
    int Attempt,
    DateTime? NextAttemptAt,
    string? Error);

public sealed record IndexFileEntry(string FileName, string ExternalSourceRef);

public static class IndexFileBuilder
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

public static class FileNameSanitizer
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
