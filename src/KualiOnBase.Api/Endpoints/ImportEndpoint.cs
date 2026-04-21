using System.Text.Json;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services;
using KualiOnBase.Api.Services.Import;
using KualiOnBase.Api.Services.Kuali;
using KualiOnBase.Api.Services.Notifications;
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
        RetryQueue queue,
        IImportOrchestrator orchestrator,
        INotificationService notifications,
        IOptions<RetryOptions> retry,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("ImportEndpoint");

        // Do NOT log request.QueryString here — it carries every KeywordKey/
        // KeywordValue pair the caller sent, which in practice includes
        // student IDs, full names, phone numbers, and anything else a Kuali
        // workflow author wired in. We log the structured, non-PII subset
        // only. Keyword count is safe (operator diagnostic), keyword values
        // are not.
        var keywordCount = CountKeywordPairs(request.Query);
        log.LogInformation(
            "Import request received: documentId={DocumentId} onbaseDocType={OnBaseDocType} " +
            "downloadMode={DownloadMode} deleteAttachments={DeleteAttachments} " +
            "deleteDocument={DeleteDocument} targetFolderPath={TargetFolderPath} keywordCount={KeywordCount}",
            documentId, onbaseDocType, downloadMode, deleteAttachments,
            deleteDocument, targetFolderPath, keywordCount);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(documentId)) errors.Add("documentId is required.");
        if (string.IsNullOrWhiteSpace(onbaseDocType)) errors.Add("onbaseDocType is required.");
        if (string.IsNullOrWhiteSpace(targetFolderPath)) errors.Add("targetFolderPath is required.");
        if (string.IsNullOrWhiteSpace(downloadMode))
        {
            errors.Add("downloadMode is required.");
        }
        else if (!DownloadModes.TryParse(downloadMode, out _))
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

    // Count non-sentinel keyword pairs without exposing any value — used for
    // observability (operator wants "was the keyword count right?") while
    // staying PII-free in logs.
    private static int CountKeywordPairs(IQueryCollection query)
    {
        var n = 0;
        for (var i = 1; i <= 20; i++)
        {
            var key = query[$"KeywordKey{i}"].ToString();
            var value = query[$"KeywordValue{i}"].ToString();
            if (IsIgnoreSentinel(key) || IsIgnoreSentinel(value)) continue;
            n++;
        }
        return n;
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
