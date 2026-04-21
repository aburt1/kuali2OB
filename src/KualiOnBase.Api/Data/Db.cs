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

            using var tx = conn.BeginTransaction();
            conn.Execute(sql, transaction: tx);
            conn.Execute(
                "INSERT INTO __Migrations (Name, AppliedAt) VALUES (@Name, @AppliedAt);",
                new { Name = name, AppliedAt = DateTime.UtcNow },
                tx);
            tx.Commit();
        }

        // Surface persistence state on every boot so "why are my jobs gone?"
        // is a one-log-line diagnosis, not a redeploy-and-pray loop.
        var jobCount = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM ImportJobs;");
        log?.LogInformation(
            "SQLite DB at {Path} — preExisted={PreExisted}, preSizeBytes={PreSize}, jobRowCount={JobCount}. " +
            "If preExisted is False on every redeploy, the /data volume is not persistent (configure Coolify persistent volume or use docker-compose).",
            full, preExisted, preSize, jobCount);
    }
}
