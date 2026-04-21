using Dapper;
using KualiOnBase.Api.Infrastructure.Data;
using KualiOnBase.Api.Configuration;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Features.Health;

public static class HealthEndpoint
{
    // /health        → liveness (process up)
    // /health/ready  → DB connects + Backup:RootPath exists. Orchestrator does
    //                  a write-probe per job, so we don't re-do it on every poll.
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
                await conn.ExecuteScalarAsync<long>(
                    new CommandDefinition("SELECT 1;", cancellationToken: ct));
            }
            catch (Exception ex)
            {
                problems.Add("db: probe failed");
                log.LogWarning(ex, "health/ready: DB probe failed");
            }

            var root = backupOpts.Value.RootPath;
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
}
