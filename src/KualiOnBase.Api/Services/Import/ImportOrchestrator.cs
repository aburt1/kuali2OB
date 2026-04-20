using System.Text.Json;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using KualiOnBase.Api.Services.Kuali;

namespace KualiOnBase.Api.Services.Import;

public sealed class ImportOrchestrator : IImportOrchestrator
{
    private readonly IKualiClient _kuali;
    private readonly IBackupService _backup;
    private readonly IJobEventLog _events;
    private readonly ILogger<ImportOrchestrator> _log;

    public ImportOrchestrator(
        IKualiClient kuali,
        IBackupService backup,
        IJobEventLog events,
        ILogger<ImportOrchestrator> log)
    {
        _kuali = kuali;
        _backup = backup;
        _events = events;
        _log = log;
    }

    public async Task<ImportOrchestratorResult> RunAsync(ImportJob job, CancellationToken ct)
    {
        if (!DownloadModes.TryParse(job.DownloadMode, out var mode))
        {
            throw new ArgumentException(
                $"Invalid downloadMode '{job.DownloadMode}'. Expected pdf|attachments|all.");
        }

        if (!Directory.Exists(job.TargetFolderPath))
        {
            throw new DirectoryNotFoundException(
                $"targetFolderPath '{job.TargetFolderPath}' does not exist or is not reachable.");
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
                    a.Url,
                }),
            },
            ct);

        var tempRoot = Path.Combine(Path.GetTempPath(), $"kuali-onbase-{job.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var stagedFiles = await DownloadAsync(job, document, mode, tempRoot, ct);
            if (stagedFiles.Count == 0)
            {
                throw new InvalidOperationException("No files were produced for this import.");
            }

            var externalSourceRefBase = !string.IsNullOrWhiteSpace(document.SerialNumber)
                ? document.SerialNumber
                : job.DocumentId;

            var finalNames = BuildFinalNames(stagedFiles, document, job.OnBaseDocType);
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

            if (job.DeleteAttachments && document.Attachments.Count > 0)
            {
                var fieldPaths = document.Attachments.Select(a => a.FieldPath).ToList();
                await _kuali.ClearAttachmentsAsync(document.Id, fieldPaths, ct);
                await _events.LogAsync(job.Id, JobEventKind.AttachmentsCleared,
                    $"{fieldPaths.Count} field(s)",
                    new { FieldPaths = fieldPaths },
                    ct);
            }

            if (job.DeleteDocument)
            {
                await _kuali.DeleteDocumentAsync(document.Id, ct);
                await _events.LogAsync(job.Id, JobEventKind.DocumentDeleted,
                    $"document {document.Id} deleted",
                    new { document.Id },
                    ct);
            }

            await _events.LogAsync(job.Id, JobEventKind.ImportSucceeded,
                $"{producedFiles.Count} file(s) delivered",
                new { Files = producedFiles, BackupFolder = backupFolder },
                ct);

            _log.LogInformation(
                "Import job {JobId} completed: {FileCount} file(s) written to {Target}",
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
        DownloadMode mode,
        string tempRoot,
        CancellationToken ct)
    {
        var files = new List<string>();

        if (mode is DownloadMode.Pdf or DownloadMode.All)
        {
            await _events.LogAsync(job.Id, JobEventKind.ExportRequested,
                $"requesting PDF export for {document.Id}",
                new { document.Id },
                ct);

            var url = await _kuali.ExportPdfAsync(document.Id, ct);

            await _events.LogAsync(job.Id, JobEventKind.ExportCallbackReceived,
                "signed URL received",
                new { SignedUrl = url },
                ct);

            var pdfPath = Path.Combine(tempRoot, $"form-{document.Id}.pdf");
            await _kuali.DownloadToFileAsync(url, pdfPath, ct);
            var size = new FileInfo(pdfPath).Length;

            await _events.LogAsync(job.Id, JobEventKind.PdfDownloaded,
                $"{size:N0} bytes",
                new { Path = pdfPath, Bytes = size },
                ct);

            files.Add(pdfPath);
        }

        if (mode is DownloadMode.Attachments or DownloadMode.All)
        {
            for (var i = 0; i < document.Attachments.Count; i++)
            {
                var att = document.Attachments[i];
                var ext = Path.GetExtension(att.FileName);
                var local = Path.Combine(tempRoot, $"attach-{i}{ext}");
                await _kuali.DownloadToFileAsync(att.Url, local, ct);
                var size = new FileInfo(local).Length;

                await _events.LogAsync(job.Id, JobEventKind.AttachmentDownloaded,
                    $"{att.FileName} ({size:N0} bytes)",
                    new { att.FieldPath, att.FileName, att.Url, Bytes = size },
                    ct);

                files.Add(local);
            }
        }

        return files;
    }

    private static List<string> BuildFinalNames(
        IReadOnlyList<string> stagedFiles,
        KualiDocument document,
        string onbaseDocType)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(stagedFiles.Count);

        foreach (var staged in stagedFiles)
        {
            var ext = Path.GetExtension(staged).TrimStart('.');
            var baseName = FileNameSanitizer.BuildContentFileName(
                serialNumber: document.SerialNumber,
                schoolId: document.SchoolId,
                lastName: document.LastName,
                firstName: document.FirstName,
                onbaseDocType: onbaseDocType,
                extension: ext);

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

    private sealed record KeywordDto(string Key, string Value);
}
