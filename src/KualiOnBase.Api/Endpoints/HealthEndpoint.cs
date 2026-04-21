using Dapper;
using KualiOnBase.Api.Data;
using KualiOnBase.Api.Options;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Endpoints;

public static class HealthEndpoint
{
    // /health        → dumb liveness probe (is the process up?)
    // /health/ready  → readiness probe — opens the DB and writes a probe file
    //                  to Backup:RootPath. A missing volume or read-only mount
    //                  shows up as a 503 here so Coolify can stop routing
    //                  traffic to a broken instance.
    //
    // Hardening notes:
    //   - The DB probe uses `SELECT 1` rather than `SELECT COUNT(*) FROM ImportJobs`
    //     (H4). The count is O(N) on SQLite without a cached count; on a growing
    //     table it turns every readiness check into a table scan, and the readiness
    //     probe runs on Coolify's health cadence (seconds). `SELECT 1` only proves
    //     the connection + query path work, which is what readiness actually means.
    //   - We return a generic problem string to the caller instead of echoing
    //     ex.Message (which can contain a filesystem path or SQLite error code
    //     useful for attack tuning). The real error is logged server-side.
    //   - The backup probe file is created with a unique random name and removed
    //     in a finally block so a write-then-throw (disk full after open, etc.)
    //     doesn't orphan .health-probe-* files in the backup root.
    //   - We DO NOT rate-limit /health/* here — Coolify needs to be able to poll
    //     it freely. The endpoints are bearer-UN-gated (path is /health, not
    //     /api/*) which ApiKeyMiddleware honors, but that's expected: readiness
    //     probes can't carry auth headers from an orchestrator.
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/ready", async (
            Db db,
            IOptions<BackupOptions> backupOpts,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("HealthReady");
            var problems = new List<string>();

            try
            {
                using var conn = db.Open();
                // SELECT 1 — proves the connection opens and a query round-trips.
                // No table scan, no schema dependency (a failed migration would
                // show up through the startup failure path, not here).
                await conn.ExecuteScalarAsync<long>(
                    new CommandDefinition("SELECT 1;", cancellationToken: ct));
            }
            catch (Exception ex)
            {
                problems.Add("db: probe failed");
                log.LogWarning(ex, "health/ready: DB probe failed");
            }

            string? probePath = null;
            try
            {
                var root = backupOpts.Value.RootPath;
                if (string.IsNullOrWhiteSpace(root))
                {
                    throw new InvalidOperationException("Backup:RootPath is not configured.");
                }
                Directory.CreateDirectory(root);
                probePath = Path.Combine(root, ".health-probe-" + Guid.NewGuid().ToString("N"));
                await File.WriteAllTextAsync(probePath, "ok", ct);
            }
            catch (Exception ex)
            {
                problems.Add("backup: probe failed");
                log.LogWarning(ex, "health/ready: backup probe failed");
            }
            finally
            {
                // Always try to clean up the probe file — even if WriteAllTextAsync
                // partially succeeded then threw (disk filled between open and close),
                // or the surrounding try hit a non-write exception after creating the
                // path string. Best-effort; a stuck probe file is non-fatal but ugly.
                if (probePath is not null)
                {
                    try { File.Delete(probePath); } catch { /* best effort */ }
                }
            }

            return problems.Count == 0
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not_ready", problems }, statusCode: 503);
        });
    }
}
