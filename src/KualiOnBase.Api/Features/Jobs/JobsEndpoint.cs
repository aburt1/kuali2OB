using System.Text.Json;
using System.Text.Json.Nodes;
using KualiOnBase.Api.Features.Import;
using KualiOnBase.Api.Features.Jobs;

namespace KualiOnBase.Api.Features.Jobs;

// Dashboard-facing list of recent jobs. Bearer-gated by ApiKeyMiddleware.
// We deliberately return only basenames for produced files and for the backup
// folder — absolute paths never cross the API boundary. Downloads flow through
// /api/jobs/{id}/files/{i}, which resolves paths server-side inside the stored
// TargetFolderPath / BackupFolderPath.
public static class JobsEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app)
    {
        return app.MapGet("/api/jobs", async (
            RetryQueue queue,
            JobEventLog events,
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
                    backupFolderName = string.IsNullOrWhiteSpace(r.BackupFolderPath)
                        ? null
                        : Path.GetFileName(r.BackupFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    keywords = DecodeKeywords(r.KeywordsJson),
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

    internal static JsonNode? ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return SanitizePayload(JsonNode.Parse(json));
        }
        catch (JsonException) { return null; }
    }

    private static JsonNode? SanitizePayload(JsonNode? node, string? propertyName = null)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            var sanitized = new JsonObject();
            foreach (var entry in obj)
            {
                sanitized[entry.Key] = SanitizePayload(entry.Value, entry.Key);
            }
            return sanitized;
        }

        if (node is JsonArray arr)
        {
            var sanitized = new JsonArray();
            for (var i = 0; i < arr.Count; i++)
            {
                sanitized.Add(SanitizePayload(arr[i], propertyName));
            }
            return sanitized;
        }

        if (node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && (IsSensitiveProperty(propertyName) || LooksLikeHttpUrl(text)))
        {
            return JsonValue.Create("[redacted]");
        }

        return node.DeepClone();
    }

    private static bool IsSensitiveProperty(string? propertyName) =>
        propertyName is not null && propertyName switch
        {
            var name when name.Equals("url", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("signedUrl", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("downloadUrl", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("pdfUrl", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("href", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("temporaryUrl", StringComparison.OrdinalIgnoreCase) => true,
            var name when name.Equals("permaLink", StringComparison.OrdinalIgnoreCase) => true,
            _ => false,
        };

    private static bool LooksLikeHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
