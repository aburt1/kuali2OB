using System.Reflection;
using Dapper;
using KualiOnBase.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Services;

public sealed class Db
{
    private readonly string _connectionString;

    public Db(IOptions<AppSettings> options)
    {
        var path = options.Value.Database.Path;
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
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.", StringComparison.Ordinal)
                && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var applied = new HashSet<string>(
            conn.Query<string>("SELECT Name FROM __Migrations;"),
            StringComparer.Ordinal);

        foreach (var resourceName in resources)
        {
            var name = resourceName[(resourceName.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            if (applied.Contains(name)) continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Migration resource {resourceName} not found.");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            try
            {
                using var tx = conn.BeginTransaction();
                conn.Execute(sql, transaction: tx);
                conn.Execute(
                    "INSERT INTO __Migrations (Name, AppliedAt) VALUES (@Name, @AppliedAt);",
                    new { Name = name, AppliedAt = DateTime.UtcNow },
                    tx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                // Name the migration in the message so boot failures are diagnosable.
                throw new InvalidOperationException(
                    $"Migration '{name}' failed: {ex.Message}", ex);
            }
        }

        // Surface persistence state on every boot so "why are my jobs gone?"
        // is a one-log-line diagnosis, not a redeploy-and-pray loop.
        var jobCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM ImportJobs;");
        log?.LogInformation(
            "SQLite DB at {Path} — preExisted={PreExisted}, preSizeBytes={PreSize}, jobRowCount={JobCount}. " +
            "If preExisted is False on every redeploy, Database:Path is pointing inside the deployment " +
            "folder and is being overwritten — point it at a directory outside the site root.",
            full, preExisted, preSize, jobCount);
    }

    // Full page-table rewrite. Acquires an exclusive lock for the duration —
    // BackupCleanupWorker runs this daily after deletes. DB is tiny (handful
    // of MB at most), so the lock is negligible.
    public void Vacuum()
    {
        using var conn = Open();
        conn.Execute("VACUUM;");
    }
}
