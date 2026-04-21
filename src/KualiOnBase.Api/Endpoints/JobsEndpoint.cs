using System.Text.Json;
using System.Text.Json.Nodes;
using KualiOnBase.Api.Services;

namespace KualiOnBase.Api.Endpoints;

// Dashboard-facing list of recent jobs. Bearer-gated by ApiKeyMiddleware.
//
// Path-hygiene note (CR-5): the orchestrator stores on-disk absolute paths in
// `ProducedFiles` and `BackupFolderPath`. We intentionally do NOT round-trip
// those paths to the dashboard — the client only needs file *names* (to render
// links and titles) and an opaque backup folder *name* (to locate the dated
// folder inside Backup:RootPath). Downloads go through /api/jobs/{id}/files/{i}
// which resolves the path server-side inside the stored TargetFolderPath /
// BackupFolderPath sandbox, so the client never sees or submits a filesystem
// path at all. This caps blast radius if a bearer token leaks.
//
// `targetFolderPath` is left as-is because it was supplied by the Kuali workflow
// author — we're reflecting their own input back to them so they can verify the
// request landed unmangled.
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
                    status = r.Status,
                    attemptCount = r.AttemptCount,
                    nextAttemptAt = r.NextAttemptAt,
                    lastError = r.LastError,
                    // Return ONLY the leaf folder name (yyyyMMdd_HHmmss_{docid}),
                    // not the full Backup:RootPath. The operator can locate it
                    // inside the configured backup share themselves.
                    backupFolderName = BasenameOrNull(r.BackupFolderPath),
                    hasBackup = !string.IsNullOrEmpty(r.BackupFolderPath),
                    keywords = DecodeKeywords(r.KeywordsJson),
                    // Basenames only — no absolute paths in the API surface.
                    files = DecodeFiles(r.ProducedFiles).Select(Path.GetFileName).ToList(),
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

    private static string? BasenameOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Trim trailing separators so GetFileName returns the last segment, not "".
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? null : name;
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
