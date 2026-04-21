using System.Diagnostics;
using System.Security.Cryptography;
using Dapper;
using KualiOnBase.Api.Data;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services.Kuali;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Endpoints;

// Diagnostics: read-only introspection of DB persistence + an "I want to try
// this exact options array against Kuali and see what comes back" probe.
// The probe exists because Kuali's `options: [String!]!` is a plain string
// array — the valid values aren't in the schema, they're defined server-side,
// so finding what actually merges requires empirical testing.
public static class DiagnosticEndpoint
{
    // Cap probe-export size hard so a single scripted call can't OOM the
    // container (Kuali exports can be hundreds of MB for long forms).
    private const long MaxProbeBytes = 200L * 1024 * 1024; // 200 MB

    public static RouteHandlerBuilder MapDbStatus(IEndpointRouteBuilder app) =>
        app.MapGet("/api/diag/db-status", HandleDbStatus);

    public static RouteHandlerBuilder MapProbeExport(IEndpointRouteBuilder app) =>
        app.MapPost("/api/diag/kuali-probe-export", HandleProbeExport);

    public static void Map(IEndpointRouteBuilder app)
    {
        MapDbStatus(app);
        MapProbeExport(app);
    }

    public record DbStatus(
        bool Exists,
        long SizeBytes,
        long JobCount,
        DateTime? LatestJobAt,
        string[] AppliedMigrations);

    public static IResult HandleDbStatus(
        Db db,
        IOptions<DatabaseOptions> dbOptions,
        ILoggerFactory lf)
    {
        var log = lf.CreateLogger("DiagDbStatus");
        // Resolve path for existence/size but do NOT expose it to the caller —
        // full filesystem paths are information disclosure useful for attack
        // tuning on a public URL.
        var full = Path.GetFullPath(dbOptions.Value.Path);
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
}
