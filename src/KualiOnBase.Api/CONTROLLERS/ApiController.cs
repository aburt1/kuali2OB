using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Controllers;

// One HTTP file on purpose. The sections below keep the route surface easy to
// scan while the real work stays in SERVICES.
public static class ApiController
{
    // ---------------------------------------------------------------------
    // Import endpoint
    // ---------------------------------------------------------------------
    public const string ImportRoute = "/api/kuali-onbase-import";

    public static RouteHandlerBuilder MapImport(IEndpointRouteBuilder app)
    {
        return app.MapPost(ImportRoute, HandleImport);
    }

    public static async Task<IResult> HandleImport(
        HttpRequest request,
        [FromQuery] string? documentId,
        [FromQuery] string? onbaseDocType,
        [FromQuery] string? targetFolderPath,
        [FromQuery] string? downloadMode,
        [FromQuery] bool? deleteAttachments,
        [FromQuery] bool? deleteDocument,
        JobsService jobs,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("ApiController.Import");

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

        // Enqueue only — RetryWorker picks the job up on its next tick and
        // runs ImportService inside its own scope. Returning 202 here means
        // Kuali's HTTP Action sees an immediate ACK and never client-side-times-out
        // waiting on the 180s Kuali export callback round-trip.
        //
        // Kuali Build's long-running-integration contract: when the workflow step
        // calls us it sets X-Response-URL to a one-time callback URL. We POST to
        // that URL when the job hits a terminal state; the workflow advances on
        // a 2xx response and reads our X-Status-Code header for the final status.
        var responseUrl = request.Headers["X-Response-URL"].ToString();
        if (string.IsNullOrWhiteSpace(responseUrl)) responseUrl = null;

        var now = DateTime.UtcNow;
        var job = new ImportJob
        {
            DocumentId = documentId!,
            OnBaseDocType = onbaseDocType!,
            TargetFolderPath = targetFolderPath!,
            DownloadMode = downloadMode!,
            DeleteAttachments = deleteAttachments ?? false,
            DeleteDocument = deleteDocument ?? false,
            KeywordsJson = JsonSerializer.Serialize(keywords),
            Status = JobStatus.Queued,
            AttemptCount = 0,
            NextAttemptAt = now,
            ResponseUrl = responseUrl,
        };
        await jobs.InsertAsync(job, ct);

        return Results.Accepted(
            $"{ImportRoute}/{job.Id}",
            new ImportResponse(
                job.Id, job.Status, [], null,
                job.AttemptCount, job.NextAttemptAt, null));
    }

    // Kuali's HTTP Action stringifies our response body as "[object Object]" when it's
    // JSON, so operators can't see the actual error. Plain-text bodies render as-is,
    // which makes them readable in Kuali's "response data" dialog.
    private static IResult TextError(int status, string message) =>
        Results.Text(message, contentType: "text/plain; charset=utf-8", statusCode: status);

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

    // ---------------------------------------------------------------------
    // Dashboard job endpoints
    // ---------------------------------------------------------------------
    public static RouteHandlerBuilder MapJobs(IEndpointRouteBuilder app)
    {
        MapFileDownload(app);
        return app.MapGet("/api/jobs", async (
            JobsService jobs,
            JobEventLog events,
            int? limit,
            CancellationToken ct) =>
        {
            var rows = await jobs.ListRecentAsync(limit ?? 50, ct);
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
    private static RouteHandlerBuilder MapFileDownload(IEndpointRouteBuilder app)
    {
        return app.MapGet("/api/jobs/{id:long}/files/{index:int}", HandleJobFile);
    }

    public static async Task<IResult> HandleJobFile(
        long id,
        int index,
        JobsService jobs,
        CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        var files = DecodeFiles(job.ProducedFiles);
        if (index < 0 || index >= files.Count)
        {
            return Results.NotFound();
        }

        var candidate = files[index];
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch
        {
            return Results.BadRequest();
        }

        // Containment check: the resolved file must sit inside the job's target
        // folder OR the backup folder we recorded — reject anything else so this
        // endpoint can't be used to read arbitrary files off disk.
        if (!IsUnder(fullPath, job.TargetFolderPath)
            && !(job.BackupFolderPath is { Length: > 0 } backup && IsUnder(fullPath, backup)))
        {
            return Results.NotFound();
        }

        if (!File.Exists(fullPath))
        {
            return Results.NotFound();
        }

        var contentType = InferContentType(fullPath);
        // No fileDownloadName → no Content-Disposition: attachment header, so
        // the browser renders PDFs inline and shows .txt as plain text.
        return Results.File(fullPath, contentType, enableRangeProcessing: true);
    }

    private static bool IsUnder(string fullPath, string folder)
    {
        string fullFolder;
        try { fullFolder = Path.GetFullPath(folder); }
        catch { return false; }

        var folderWithSep = fullFolder.EndsWith(Path.DirectorySeparatorChar)
            ? fullFolder
            : fullFolder + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(folderWithSep, StringComparison.Ordinal);
    }

    private static string InferContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".txt" => "text/plain; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    // ---------------------------------------------------------------------
    // Health and diagnostics
    // ---------------------------------------------------------------------
    // /health        → liveness (process up)
    // /health/ready  → DB connects + Backup:RootPath exists. ImportService does
    //                  a write-probe per job, so we don't re-do it on every poll.
    public static void MapHealth(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/ready", async (
            Db db,
            IOptions<AppSettings> settings,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("HealthReady");
            var problems = new List<string>();

            try
            {
                using var conn = db.Open();
                await conn.ExecuteScalarAsync<long>(
                    new CommandDefinition("SELECT 1;", cancellationToken: ct));
            }
            catch (Exception ex)
            {
                problems.Add("db: probe failed");
                log.LogWarning(ex, "health/ready: DB probe failed");
            }

            var root = settings.Value.Backup.RootPath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                problems.Add("backup: root missing");
                log.LogWarning("health/ready: Backup:RootPath missing or not configured ({Root})", root);
            }

            return problems.Count == 0
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not_ready", problems }, statusCode: 503);
        });
    }

    // Cap probe-export size hard so a single scripted call can't OOM the
    // container (Kuali exports can be hundreds of MB for long forms).
    private const long MaxProbeBytes = 200L * 1024 * 1024; // 200 MB

    public static RouteHandlerBuilder MapDbStatus(IEndpointRouteBuilder app) =>
        app.MapGet("/api/diag/db-status", HandleDbStatus);

    public static RouteHandlerBuilder MapProbeExport(IEndpointRouteBuilder app) =>
        app.MapPost("/api/diag/kuali-probe-export", HandleProbeExport);

    public record DbStatus(
        bool Exists,
        long SizeBytes,
        long JobCount,
        DateTime? LatestJobAt,
        string[] AppliedMigrations);

    public static IResult HandleDbStatus(
        Db db,
        IOptions<AppSettings> settings,
        ILoggerFactory lf)
    {
        var log = lf.CreateLogger("DiagDbStatus");
        // Resolve path for existence/size but do NOT expose it to the caller —
        // full filesystem paths are information disclosure useful for attack
        // tuning on a public URL.
        var full = Path.GetFullPath(settings.Value.Database.Path);
        var exists = File.Exists(full);
        var size = exists ? new FileInfo(full).Length : 0;

        long jobCount = 0;
        DateTime? latest = null;
        string[] migrations = Array.Empty<string>();
        try
        {
            using var conn = db.Open();
            jobCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM ImportJobs;");
            var latestIso = conn.ExecuteScalar<string?>("SELECT MAX(CreatedAt) FROM ImportJobs;");
            latest = DateTime.TryParse(latestIso,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt)
                ? dt : null;
            migrations = conn.Query<string>("SELECT Name FROM __Migrations ORDER BY Name;").ToArray();
        }
        catch (Exception ex)
        {
            // Don't swallow silently — operators need to know when the probe
            // itself failed vs. "DB is empty". Log the real error; return a
            // generic signal to the caller.
            log.LogWarning(ex, "diag/db-status probe failed");
        }

        return Results.Ok(new DbStatus(exists, size, jobCount, latest, migrations));
    }

    public record ProbeExportRequest(string DocumentId, string[] Options);
    public record ProbeExportResult(
        string DocumentId,
        string[] SentOptions,
        long SizeBytes,
        string Sha256,
        long DurationMs);

    // Runs exportDocument(options: <your array>), waits for callback, downloads
    // the resulting PDF, and reports size + hash — WITHOUT buffering the whole
    // thing in memory (used to be File.ReadAllBytes → OOM vector). Hash streams
    // through IncrementalHash as bytes are written. Size-capped so a script
    // can't use this endpoint to exhaust container memory or disk.
    //
    // Also rate-limited (wired in Program.cs via .RequireRateLimiting) so an
    // authenticated operator can't script the endpoint to hammer Kuali tenant
    // export quotas. The SignedUrl is NOT returned to the caller — it was in
    // the old response and was itself a post-auth information leak.
    public static async Task<IResult> HandleProbeExport(
        [FromBody] ProbeExportRequest req,
        IKualiClient kuali,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DocumentId))
        {
            return Results.BadRequest(new { error = "documentId is required" });
        }

        var sw = Stopwatch.StartNew();
        var url = await kuali.ExportPdfAsync(req.DocumentId, req.Options ?? Array.Empty<string>(), ct);

        var tempPath = Path.Combine(Path.GetTempPath(), $"kuali-probe-{Guid.NewGuid():N}.pdf");
        try
        {
            await kuali.DownloadToFileAsync(url, tempPath, ct);

            var info = new FileInfo(tempPath);
            if (info.Length > MaxProbeBytes)
            {
                return Results.Problem(
                    $"Probe result exceeded {MaxProbeBytes:N0} bytes ({info.Length:N0}); refusing to hash.",
                    statusCode: 413);
            }

            // Stream the hash: open the file once, feed blocks through
            // IncrementalHash without loading anything else. Constant memory.
            string sha;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var fs = File.OpenRead(tempPath);
                var buffer = new byte[81_920];
                int read;
                while ((read = await fs.ReadAsync(buffer, ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                }
                sha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            sw.Stop();

            return Results.Ok(new ProbeExportResult(
                DocumentId: req.DocumentId,
                SentOptions: req.Options ?? Array.Empty<string>(),
                SizeBytes: info.Length,
                Sha256: sha,
                DurationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------------
    // Kuali PDF export callback
    // ---------------------------------------------------------------------
    public const string KualiCallbackRoute = "/kuali-export-callback/{correlationId}";

    // Kuali's real callback body is a tiny JSON payload; cap hostile bodies cheap.
    private const int MaxBodyBytes = 64 * 1024;

    public static RouteHandlerBuilder MapKualiCallback(IEndpointRouteBuilder app)
    {
        return app.MapPost(KualiCallbackRoute, HandleKualiCallback);
    }

    public static async Task<IResult> HandleKualiCallback(
        string correlationId,
        HttpRequest request,
        ExportCallbackStore store,
        IOptions<AppSettings> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("KualiExportCallback");
        var opts = options.Value.Kuali;

        if (string.IsNullOrWhiteSpace(opts.CallbackSecret))
        {
            log.LogError("Callback received but Kuali:CallbackSecret is not configured.");
            return Results.Problem("Callback secret not configured.", statusCode: 500);
        }

        var sig = request.Query["sig"].ToString();
        var expected = KualiClient.SignCallback(correlationId, opts.CallbackSecret);
        // Length gate before FixedTimeEquals — that API throws on length mismatch,
        // which would bubble to a 500 and leak short-sig timing distinguishability.
        if (string.IsNullOrEmpty(sig)
            || sig.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(sig),
                Encoding.ASCII.GetBytes(expected)))
        {
            log.LogWarning("Callback for {CorrelationId} rejected due to invalid signature.", correlationId);
            return Results.Unauthorized();
        }

        // Content-Length short-circuit so we don't buffer hostile bodies into Kestrel.
        if (request.ContentLength is long cl && cl > MaxBodyBytes)
        {
            log.LogWarning("Callback for {CorrelationId} rejected — body size {Size} exceeds cap.",
                correlationId, cl);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var row = await store.GetAsync(correlationId, ct);
        if (row is null)
        {
            log.LogWarning("Callback for unknown correlation id {CorrelationId}.", correlationId);
            return Results.NotFound();
        }

        string body;
        try
        {
            body = await ReadBodyAsync(request, ct);
        }
        catch (InvalidOperationException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var url = ExtractUrl(body);
        var error = ExtractError(body);

        if (!string.IsNullOrEmpty(error))
        {
            var transitioned = await store.MarkFailedAsync(correlationId, error!, ct);
            if (!transitioned)
            {
                log.LogWarning("Duplicate/late failure callback for {CorrelationId}; row already finalized.",
                    correlationId);
                return Results.Conflict(new { status = "already-finalized" });
            }
            log.LogWarning("Kuali export failed for {DocumentId}: {Error}", row.DocumentId, error);
            return Results.Ok(new { status = "recorded" });
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            await store.MarkFailedAsync(correlationId, "Callback payload did not contain a URL.", ct);
            log.LogWarning("Callback for {DocumentId} had no URL.", row.DocumentId);
            return Results.BadRequest(new { error = "Missing URL in callback payload." });
        }

        // Require HTTPS here. The resolved URL can eventually be fetched by a
        // client that knows the Kuali bearer token, so allowing plaintext HTTP
        // would create a credential-leak path on same-host callback URLs.
        if (!IsHttpsUrl(url!))
        {
            await store.MarkFailedAsync(correlationId,
                "Callback payload URL must use https scheme.", ct);
            log.LogWarning("Callback for {DocumentId} had non-https URL; rejected.", row.DocumentId);
            return Results.BadRequest(new { error = "Callback URL must be https." });
        }

        var completed = await store.MarkCompletedAsync(correlationId, url!, ct);
        if (!completed)
        {
            // Either Kuali retried and won the race, or an attacker who got our
            // HMAC is trying to overwrite a finalized row. Either way we refuse
            // without echoing which it was.
            log.LogWarning("Duplicate/late callback for {CorrelationId}; row already finalized.",
                correlationId);
            return Results.Conflict(new { status = "already-finalized" });
        }

        log.LogInformation("Recorded Kuali export callback for {DocumentId}.", row.DocumentId);
        return Results.Ok(new { status = "recorded" });
    }

    // Enforce MaxBodyBytes even when Content-Length is missing or lies.
    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxBodyBytes) throw new InvalidOperationException("Callback body exceeds cap.");
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u)
        && u!.Scheme == Uri.UriSchemeHttps;

    // Kuali's callback body shape is not strongly documented, so accept several forms:
    //   - JSON: { "url": "..." } / { "signedUrl": "..." } / { "downloadUrl": "..." } / { "pdfUrl": "..." }
    //   - raw text body that IS the URL
    //   - ?url=... query string fallback
    internal static string? ExtractUrl(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "url", "signedUrl", "downloadUrl", "pdfUrl", "href" })
                {
                    var value = obj[key]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through - maybe the body IS a bare URL
        }

        var trimmed = body.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return null;
    }

    internal static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "error", "errorMessage", "message" })
                {
                    var value = obj[key]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // ignore
        }
        return null;
    }
}
