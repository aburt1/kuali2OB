using System.Reflection;
using Dapper;
using KualiOnBase.Api.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Data;

public sealed class Db
{
    private readonly string _connectionString;

    public Db(IOptions<DatabaseOptions> options)
    {
        var path = options.Value.Path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;");
        return conn;
    }

    public void Migrate(ILogger? log = null)
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var full = Path.GetFullPath(path);
        var preExisted = File.Exists(full);
        var preSize = preExisted ? new FileInfo(full).Length : 0;

        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS __Migrations (
                Name       TEXT PRIMARY KEY,
                AppliedAt  TEXT NOT NULL
            );
        """);

        var assembly = Assembly.GetExecutingAssembly();
        var ns = typeof(Db).Namespace + ".Migrations.";
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ns, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var applied = new HashSet<string>(
            conn.Query<string>("SELECT Name FROM __Migrations;"),
            StringComparer.Ordinal);

        foreach (var resourceName in resources)
        {
            var name = resourceName.Substring(ns.Length);
            if (applied.Contains(name)) continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Migration resource {resourceName} not found.");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            // Some migrations (VACUUM, PRAGMA auto_vacuum with mode change)
            // cannot run inside a transaction. Such files opt out with a
            // "-- NO_TRANSACTION" marker on any line of the file header.
            var noTransaction = sql.Contains("-- NO_TRANSACTION", StringComparison.Ordinal);

            // Wrap with the migration name so a failure surfaces "migration X
            // failed: <reason>" at startup instead of a bare SqliteException
            // with no hint about which file broke. Reviewer C3.
            try
            {
                if (noTransaction)
                {
                    // Run the migration body first (no tx), then record it
                    // in __Migrations under its own tx. If the body succeeds
                    // but the INSERT fails, the migration will be re-run on
                    // the next boot — which is safe for idempotent PRAGMA /
                    // VACUUM content by design.
                    conn.Execute(sql);
                    conn.Execute(
                        "INSERT INTO __Migrations (Name, AppliedAt) VALUES (@Name, @AppliedAt);",
                        new { Name = name, AppliedAt = DateTime.UtcNow });
                }
                else
                {
                    using var tx = conn.BeginTransaction();
                    conn.Execute(sql, transaction: tx);
                    conn.Execute(
                        "INSERT INTO __Migrations (Name, AppliedAt) VALUES (@Name, @AppliedAt);",
                        new { Name = name, AppliedAt = DateTime.UtcNow },
                        tx);
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Migration '{name}' failed: {ex.Message}", ex);
            }
        }

        // Surface persistence state on every boot so "why are my jobs gone?"
        // is a one-log-line diagnosis, not a redeploy-and-pray loop.
        var jobCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM ImportJobs;");
        log?.LogInformation(
            "SQLite DB at {Path} — preExisted={PreExisted}, preSizeBytes={PreSize}, jobRowCount={JobCount}. " +
            "If preExisted is False on every redeploy, the /data volume is not persistent (configure Coolify persistent volume or use docker-compose).",
            full, preExisted, preSize, jobCount);
    }

    // Opportunistic page reclaim. Works with PRAGMA auto_vacuum=INCREMENTAL
    // (enabled via migration 006 on the active connection). No exclusive
    // rewrite of the whole file — just returns pages freed by DELETE without
    // blocking concurrent writers. Safe to call on a live DB.
    //
    // For a full page-table rewrite (rarely needed), call Vacuum() instead;
    // it acquires an exclusive lock and can stall every writer for the
    // duration, so BackupCleanupWorker uses IncrementalVacuum on its daily
    // sweep.
    public void IncrementalVacuum()
    {
        using var conn = Open();
        // auto_vacuum mode is per-DB, not per-connection; PRAGMA auto_vacuum
        // read here tells us whether incremental_vacuum is actually supported
        // on this file. If it was NONE when the DB was created (pre-migration-006
        // DBs) incremental_vacuum is a no-op — fall back to VACUUM to reclaim.
        var mode = conn.ExecuteScalar<long?>("PRAGMA auto_vacuum;") ?? 0;
        if (mode == 0)
        {
            conn.Execute("VACUUM;");
            return;
        }
        conn.Execute("PRAGMA incremental_vacuum;");
    }

    // Reclaim pages freed by DELETE via a full rewrite. Acquires an exclusive
    // lock for the duration — do not call from the hot path. Kept for ad-hoc
    // operator use and the pre-migration-006 fallback inside IncrementalVacuum.
    public void Vacuum()
    {
        using var conn = Open();
        conn.Execute("VACUUM;");
    }
}
