using System.Text.Json;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services;
using KualiOnBase.Api.Services.Import;
using KualiOnBase.Api.Services.Kuali;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Endpoints;

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
        [FromQuery] string? pdfExport,
        RetryQueue queue,
        IImportOrchestrator orchestrator,
        IOptions<RetryOptions> retry,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("ImportEndpoint");

        log.LogInformation(
            "Import request received: documentId={DocumentId} onbaseDocType={OnBaseDocType} " +
            "downloadMode={DownloadMode} pdfExport={PdfExport} deleteAttachments={DeleteAttachments} " +
            "deleteDocument={DeleteDocument} targetFolderPath={TargetFolderPath} query={Query}",
            documentId, onbaseDocType, downloadMode, pdfExport, deleteAttachments,
            deleteDocument, targetFolderPath, request.QueryString.Value);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(documentId)) errors.Add("documentId is required.");
        if (string.IsNullOrWhiteSpace(onbaseDocType)) errors.Add("onbaseDocType is required.");
        if (string.IsNullOrWhiteSpace(targetFolderPath)) errors.Add("targetFolderPath is required.");
        if (string.IsNullOrWhiteSpace(downloadMode)) errors.Add("downloadMode is required.");
        else if (!DownloadModes.TryParse(downloadMode, out _))
            errors.Add("downloadMode must be one of: pdf, attachments, all.");
        if (deleteAttachments is null) errors.Add("deleteAttachments is required.");

        string? canonicalPdfExport = null;
        if (!PdfExportOptions.TryNormalize(pdfExport, out canonicalPdfExport))
        {
            errors.Add("pdfExport must be one of: Form, Combined, Attachments (or omitted).");
        }

        if (errors.Count > 0)
        {
            log.LogWarning("Import request rejected (validation): {Errors} | query={Query}",
                string.Join(" | ", errors), request.QueryString.Value);
            return Results.BadRequest(new { errors });
        }

        var keywords = ExtractKeywords(request.Query);

        var job = new ImportJob
        {
            DocumentId = documentId!,
            OnBaseDocType = onbaseDocType!,
            TargetFolderPath = targetFolderPath!,
            DownloadMode = downloadMode!,
            DeleteAttachments = deleteAttachments ?? false,
            DeleteDocument = deleteDocument ?? false,
            KeywordsJson = JsonSerializer.Serialize(keywords),
            PdfExport = canonicalPdfExport,
            Status = JobStatus.Running,
            AttemptCount = 1,
        };
        await queue.InsertAsync(job, ct);

        try
        {
            var result = await orchestrator.RunAsync(job, ct);
            job.Status = JobStatus.Succeeded;
            job.BackupFolderPath = result.BackupFolder;
            job.ProducedFiles = JsonSerializer.Serialize(result.ProducedFiles);
            job.LastError = null;
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
            await MarkFailedAsync(queue, job, ex, ct);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (DirectoryNotFoundException ex)
        {
            await MarkFailedAsync(queue, job, ex, ct);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(queue, job, ex, ct);
            log.LogError(ex, "Import job {JobId} failed permanently", job.Id);
            return Results.Problem(title: "Import failed", detail: ex.Message, statusCode: 500);
        }
    }

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
