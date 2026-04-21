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
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diag/db-status", HandleDbStatus);
        app.MapPost("/api/diag/kuali-probe-export", HandleProbeExport);
    }

    public record DbStatus(
        string Path,
        bool Exists,
        long SizeBytes,
        long JobCount,
        DateTime? LatestJobAt,
        string[] AppliedMigrations);

    public static IResult HandleDbStatus(
        Db db,
        IOptions<DatabaseOptions> dbOptions)
    {
        var path = dbOptions.Value.Path;
        var full = Path.GetFullPath(path);
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
            latest = DateTime.TryParse(latestIso, out var dt) ? dt : null;
            migrations = conn.Query<string>("SELECT Name FROM __Migrations ORDER BY Name;").ToArray();
        }
        catch { /* db file missing or not yet migrated */ }

        return Results.Ok(new DbStatus(full, exists, size, jobCount, latest, migrations));
    }

    public record ProbeExportRequest(string DocumentId, string[] Options);
    public record ProbeExportResult(
        string DocumentId,
        string[] SentOptions,
        string SignedUrl,
        long SizeBytes,
        string Sha256,
        long DurationMs);

    // Runs exportDocument(options: <your array>), waits for callback, downloads
    // the resulting PDF to memory, and reports size + hash. Different `options`
    // arrays that produce different sizes tell you which strings are actually
    // doing something. Larger size = more content merged in.
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
            var bytes = await File.ReadAllBytesAsync(tempPath, ct);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            sw.Stop();

            return Results.Ok(new ProbeExportResult(
                DocumentId: req.DocumentId,
                SentOptions: req.Options ?? Array.Empty<string>(),
                SignedUrl: url,
                SizeBytes: bytes.LongLength,
                Sha256: sha,
                DurationMs: sw.ElapsedMilliseconds));
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }
}
