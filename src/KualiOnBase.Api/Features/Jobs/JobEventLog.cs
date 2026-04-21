using System.Text.Json;
using Dapper;
using KualiOnBase.Api.Infrastructure.Data;

namespace KualiOnBase.Api.Features.Jobs;

public interface IJobEventLog
{
    Task LogAsync(long jobId, string kind, string? message, object? payload, CancellationToken ct);
    Task<IReadOnlyList<JobEventRow>> ListForJobAsync(long jobId, CancellationToken ct);
    Task<IReadOnlyList<JobEventRow>> ListForJobsAsync(IReadOnlyCollection<long> jobIds, CancellationToken ct);
}

public sealed class JobEventRow
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public DateTime At { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? PayloadJson { get; set; }
}

public sealed class JobEventLog : IJobEventLog
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly Db _db;
    private readonly ILogger<JobEventLog> _log;

    public JobEventLog(Db db, ILogger<JobEventLog> log)
    {
        _db = db;
        _log = log;
    }

    public async Task LogAsync(long jobId, string kind, string? message, object? payload, CancellationToken ct)
    {
        string? json = null;
        if (payload is not null)
        {
            try { json = JsonSerializer.Serialize(payload, PayloadJson); }
            catch (NotSupportedException ex)
            {
                _log.LogWarning(ex, "Failed to serialize payload for event {Kind} on job {JobId}", kind, jobId);
            }
        }

        try
        {
            using var conn = _db.Open();
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO JobEvents (JobId, At, Kind, Message, PayloadJson)
                VALUES (@JobId, @At, @Kind, @Message, @PayloadJson);
                """,
                new
                {
                    JobId = jobId,
                    At = DateTime.UtcNow,
                    Kind = kind,
                    Message = message,
                    PayloadJson = json,
                },
                cancellationToken: ct));
        }
        catch (Exception ex)
        {
            // Event logging must never break a job.
            _log.LogWarning(ex, "Failed to record event {Kind} for job {JobId}", kind, jobId);
        }
    }

    public async Task<IReadOnlyList<JobEventRow>> ListForJobAsync(long jobId, CancellationToken ct)
    {
        using var conn = _db.Open();
        var rows = await conn.QueryAsync<JobEventRow>(new CommandDefinition(
            "SELECT * FROM JobEvents WHERE JobId = @JobId ORDER BY Id;",
            new { JobId = jobId },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<JobEventRow>> ListForJobsAsync(IReadOnlyCollection<long> jobIds, CancellationToken ct)
    {
        if (jobIds.Count == 0) return Array.Empty<JobEventRow>();
        using var conn = _db.Open();
        var rows = await conn.QueryAsync<JobEventRow>(new CommandDefinition(
            "SELECT * FROM JobEvents WHERE JobId IN @Ids ORDER BY JobId, Id;",
            new { Ids = jobIds },
            cancellationToken: ct));
        return rows.AsList();
    }
}

public static class JobEventKind
{
    public const string ImportStarted = "ImportStarted";
    public const string DocumentFetched = "DocumentFetched";
    public const string ExportRequested = "ExportRequested";
    public const string ExportCallbackReceived = "ExportCallbackReceived";
    public const string PdfDownloaded = "PdfDownloaded";
    public const string AttachmentDownloaded = "AttachmentDownloaded";
    public const string FilesRenamed = "FilesRenamed";
    public const string BackupCreated = "BackupCreated";
    public const string FilesCopiedToTarget = "FilesCopiedToTarget";
    public const string IndexFileWritten = "IndexFileWritten";
    public const string AttachmentsCleared = "AttachmentsCleared";
    public const string DocumentDeleted = "DocumentDeleted";
    public const string ImportSucceeded = "ImportSucceeded";
    public const string ImportFailed = "ImportFailed";
}
