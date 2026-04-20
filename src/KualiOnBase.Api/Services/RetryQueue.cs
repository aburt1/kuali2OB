using Dapper;
using KualiOnBase.Api.Data;
using KualiOnBase.Api.Models;

namespace KualiOnBase.Api.Services;

public sealed class RetryQueue
{
    private readonly Db _db;

    public RetryQueue(Db db)
    {
        _db = db;
    }

    public async Task<long> InsertAsync(ImportJob job, CancellationToken ct)
    {
        using var conn = _db.Open();
        job.CreatedAt = DateTime.UtcNow;
        job.UpdatedAt = job.CreatedAt;
        const string sql = """
            INSERT INTO ImportJobs
                (DocumentId, OnBaseDocType, TargetFolderPath, DownloadMode,
                 DeleteAttachments, DeleteDocument, KeywordsJson, PdfExport, Status,
                 AttemptCount, NextAttemptAt, LastError, BackupFolderPath,
                 ProducedFiles, CreatedAt, UpdatedAt)
            VALUES (@DocumentId, @OnBaseDocType, @TargetFolderPath, @DownloadMode,
                    @DeleteAttachments, @DeleteDocument, @KeywordsJson, @PdfExport, @Status,
                    @AttemptCount, @NextAttemptAt, @LastError, @BackupFolderPath,
                    @ProducedFiles, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();
        """;
        job.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, job, cancellationToken: ct));
        return job.Id;
    }

    public async Task UpdateAsync(ImportJob job, CancellationToken ct)
    {
        using var conn = _db.Open();
        job.UpdatedAt = DateTime.UtcNow;
        const string sql = """
            UPDATE ImportJobs SET
                Status            = @Status,
                AttemptCount      = @AttemptCount,
                NextAttemptAt     = @NextAttemptAt,
                LastError         = @LastError,
                BackupFolderPath  = @BackupFolderPath,
                ProducedFiles     = @ProducedFiles,
                UpdatedAt         = @UpdatedAt
            WHERE Id = @Id;
        """;
        await conn.ExecuteAsync(new CommandDefinition(sql, job, cancellationToken: ct));
    }

    public async Task<ImportJob?> GetAsync(long id, CancellationToken ct)
    {
        using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<ImportJob>(
            new CommandDefinition("SELECT * FROM ImportJobs WHERE Id = @Id;", new { Id = id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportJob>> DueForRetryAsync(DateTime nowUtc, CancellationToken ct)
    {
        using var conn = _db.Open();
        var rows = await conn.QueryAsync<ImportJob>(new CommandDefinition(
            """
            SELECT * FROM ImportJobs
            WHERE Status = 'Retrying' AND NextAttemptAt <= @Now
            ORDER BY NextAttemptAt
            LIMIT 25;
            """,
            new { Now = nowUtc },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> DeleteSucceededOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        using var conn = _db.Open();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ImportJobs WHERE Status = 'Succeeded' AND UpdatedAt < @Cutoff;",
            new { Cutoff = cutoffUtc },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportJob>> ListRecentAsync(int limit, CancellationToken ct)
    {
        using var conn = _db.Open();
        var rows = await conn.QueryAsync<ImportJob>(new CommandDefinition(
            "SELECT * FROM ImportJobs ORDER BY Id DESC LIMIT @Limit;",
            new { Limit = Math.Clamp(limit, 1, 500) },
            cancellationToken: ct));
        return rows.AsList();
    }
}
