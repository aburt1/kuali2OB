using System.Text.Json;
using System.Text.Json.Nodes;
using KualiOnBase.Api.Services;

namespace KualiOnBase.Api.Endpoints;

public static class JobsEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app)
    {
        return app.MapGet("/api/jobs", async (
            RetryQueue queue,
            IJobEventLog events,
            int? limit,
            CancellationToken ct) =>
        {
            var rows = await queue.ListRecentAsync(limit ?? 50, ct);
            var eventRows = await events.ListForJobsAsync(rows.Select(r => r.Id).ToList(), ct);
            var eventsByJob = eventRows
                .GroupBy(e => e.JobId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var view = rows.Select(r =>
            {
                eventsByJob.TryGetValue(r.Id, out var evts);
                return new
                {
                    id = r.Id,
                    documentId = r.DocumentId,
                    onbaseDocType = r.OnBaseDocType,
                    targetFolderPath = r.TargetFolderPath,
                    downloadMode = r.DownloadMode,
                    deleteAttachments = r.DeleteAttachments,
                    deleteDocument = r.DeleteDocument,
                    pdfExport = r.PdfExport,
                    status = r.Status,
                    attemptCount = r.AttemptCount,
                    nextAttemptAt = r.NextAttemptAt,
                    lastError = r.LastError,
                    backupFolderPath = r.BackupFolderPath,
                    keywords = DecodeKeywords(r.KeywordsJson),
                    files = DecodeFiles(r.ProducedFiles),
                    createdAt = r.CreatedAt,
                    updatedAt = r.UpdatedAt,
                    events = (evts ?? new List<JobEventRow>()).Select(e => new
                    {
                        id = e.Id,
                        at = e.At,
                        kind = e.Kind,
                        message = e.Message,
                        payload = ParsePayload(e.PayloadJson),
                    }),
                };
            });
            return Results.Ok(view);
        });
    }

    private static IReadOnlyList<KeyValuePair<string, string>> DecodeKeywords(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<KeyValuePair<string, string>>();
        try
        {
            return JsonSerializer.Deserialize<List<KeyValuePair<string, string>>>(json)
                ?? new List<KeyValuePair<string, string>>();
        }
        catch (JsonException) { return Array.Empty<KeyValuePair<string, string>>(); }
    }

    private static IReadOnlyList<string> DecodeFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    private static JsonNode? ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }
}
