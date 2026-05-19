using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Services;

// All job persistence and background job work is here: insert/update jobs, find retry work, list
// dashboard rows, and answer the small cleanup-coordination questions. This is
// intentionally one readable service instead of one repository class per query.
public sealed class JobsService
{
    private readonly Db _db;

    public JobsService(Db db)
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
                 DeleteAttachments, DeleteDocument, KeywordsJson, Status,
                 AttemptCount, NextAttemptAt, LastError, BackupFolderPath,
                 ProducedFiles, ResponseUrl, KualiNotifiedAt, CreatedAt, UpdatedAt)
            VALUES (@DocumentId, @OnBaseDocType, @TargetFolderPath, @DownloadMode,
                    @DeleteAttachments, @DeleteDocument, @KeywordsJson, @Status,
                    @AttemptCount, @NextAttemptAt, @LastError, @BackupFolderPath,
                    @ProducedFiles, @ResponseUrl, @KualiNotifiedAt, @CreatedAt, @UpdatedAt);
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
                KualiNotifiedAt   = @KualiNotifiedAt,
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
            WHERE Status IN ('Queued', 'Retrying') AND NextAttemptAt <= @Now
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

    public async Task<bool> IsSiblingStillImportingAsync(
        string documentId,
        string downloadMode,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        var siblingMode = GetSiblingMode(downloadMode);
        if (siblingMode is null || !await HasJobModeInWindowAsync(documentId, siblingMode, anchorUtc, window, ct))
        {
            return false;
        }

        return !await HasDeliveredModeInWindowAsync(documentId, "pdf", anchorUtc, window, ct)
            || !await HasDeliveredModeInWindowAsync(documentId, "attachments", anchorUtc, window, ct);
    }

    public async Task<bool> CleanupAlreadySucceededInWindowAsync(
        string documentId,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        using var conn = _db.Open();
        var (startUtc, endUtc) = GetWindow(anchorUtc, window);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
              FROM ImportJobs
             WHERE DocumentId = @DocumentId
               AND CreatedAt BETWEEN @StartUtc AND @EndUtc
               AND Status = 'Succeeded'
               AND (DeleteAttachments = 1 OR DeleteDocument = 1);
            """,
            new
            {
                DocumentId = documentId,
                StartUtc = startUtc,
                EndUtc = endUtc,
            },
            cancellationToken: ct));
        return count > 0;
    }

    public async Task<(bool DeleteAttachments, bool DeleteDocument)> GetCleanupRequestInWindowAsync(
        string documentId,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        using var conn = _db.Open();
        var (startUtc, endUtc) = GetWindow(anchorUtc, window);
        var row = await conn.QuerySingleAsync(new CommandDefinition(
            """
            SELECT
                MAX(CASE WHEN DeleteAttachments = 1 THEN 1 ELSE 0 END) AS DeleteAttachments,
                MAX(CASE WHEN DeleteDocument = 1 THEN 1 ELSE 0 END) AS DeleteDocument
              FROM ImportJobs
             WHERE DocumentId = @DocumentId
               AND CreatedAt BETWEEN @StartUtc AND @EndUtc
               AND BackupFolderPath IS NOT NULL
               AND ProducedFiles IS NOT NULL;
            """,
            new
            {
                DocumentId = documentId,
                StartUtc = startUtc,
                EndUtc = endUtc,
            },
            cancellationToken: ct));

        return (Convert.ToInt32(row.DeleteAttachments) == 1, Convert.ToInt32(row.DeleteDocument) == 1);
    }

    private async Task<bool> HasJobModeInWindowAsync(
        string documentId,
        string downloadMode,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        using var conn = _db.Open();
        var (startUtc, endUtc) = GetWindow(anchorUtc, window);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
              FROM ImportJobs
             WHERE DocumentId = @DocumentId
               AND DownloadMode = @DownloadMode
               AND CreatedAt BETWEEN @StartUtc AND @EndUtc;
            """,
            new
            {
                DocumentId = documentId,
                DownloadMode = downloadMode,
                StartUtc = startUtc,
                EndUtc = endUtc,
            },
            cancellationToken: ct));
        return count > 0;
    }

    private async Task<bool> HasDeliveredModeInWindowAsync(
        string documentId,
        string downloadMode,
        DateTime anchorUtc,
        TimeSpan window,
        CancellationToken ct)
    {
        using var conn = _db.Open();
        var (startUtc, endUtc) = GetWindow(anchorUtc, window);
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*)
              FROM ImportJobs
             WHERE DocumentId = @DocumentId
               AND DownloadMode = @DownloadMode
               AND CreatedAt BETWEEN @StartUtc AND @EndUtc
               AND BackupFolderPath IS NOT NULL
               AND ProducedFiles IS NOT NULL;
            """,
            new
            {
                DocumentId = documentId,
                DownloadMode = downloadMode,
                StartUtc = startUtc,
                EndUtc = endUtc,
            },
            cancellationToken: ct));
        return count > 0;
    }

    private static (DateTime StartUtc, DateTime EndUtc) GetWindow(DateTime anchorUtc, TimeSpan window) =>
        (anchorUtc - window, anchorUtc + window);

    private static string? GetSiblingMode(string mode) => mode switch
    {
        "pdf" => "attachments",
        "attachments" => "pdf",
        _ => null,
    };
}

public sealed class RetryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AppSettings.RetrySettings _options;
    private readonly ILogger<RetryWorker> _log;

    public RetryWorker(
        IServiceScopeFactory scopes,
        IOptions<AppSettings> settings,
        ILogger<RetryWorker> log)
    {
        _scopes = scopes;
        _options = settings.Value.Retry;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "RetryWorker loop failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessDueJobsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobsService>();
        var importService = scope.ServiceProvider.GetRequiredService<ImportService>();
        var notifications = scope.ServiceProvider.GetRequiredService<EmailNotificationService>();
        var kuali = scope.ServiceProvider.GetRequiredService<KualiResponseUrlNotifier>();

        var due = await jobs.DueForRetryAsync(DateTime.UtcNow, ct);
        foreach (var job in due)
        {
            if (ct.IsCancellationRequested) break;
            await RunJobAsync(job, jobs, importService, notifications, kuali, ct);
        }
    }

    private async Task RunJobAsync(
        ImportJob job,
        JobsService jobs,
        ImportService importService,
        EmailNotificationService notifications,
        KualiResponseUrlNotifier kuali,
        CancellationToken ct)
    {
        job.AttemptCount += 1;
        job.Status = JobStatus.Running;
        job.UpdatedAt = DateTime.UtcNow;
        await jobs.UpdateAsync(job, ct);

        try
        {
            var result = await importService.RunAsync(job, ct);
            job.BackupFolderPath = result.BackupFolder;
            job.ProducedFiles = JsonSerializer.Serialize(result.ProducedFiles);

            // Delivery is "done" the moment files land in /target — whether or
            // not the deleteDocument cleanup grace period has elapsed yet. Fire
            // the workflow callback here so downstream Kuali steps don't wait
            // an extra ~2 minutes for our internal cleanup. KualiNotifiedAt
            // gates against double-firing on the cleanup retry.
            await NotifyKualiOnceAsync(job, succeeded: true, error: null, jobs, kuali, ct);

            if (result.CleanupDeferred)
            {
                job.Status = JobStatus.Retrying;
                job.LastError = result.CleanupMessage;
                job.NextAttemptAt = result.ResumeAt ?? DateTime.UtcNow.Add(Backoff(job.AttemptCount));
                await jobs.UpdateAsync(job, ct);
                _log.LogInformation(
                    "Retry job {JobId} deferred cleanup; next check at {NextAttemptAt}",
                    job.Id, job.NextAttemptAt);
                return;
            }

            job.Status = JobStatus.Succeeded;
            job.LastError = null;
            job.NextAttemptAt = null;
            await jobs.UpdateAsync(job, ct);
            _log.LogInformation("Retry job {JobId} succeeded on attempt {Attempt}", job.Id, job.AttemptCount);
        }
        catch (Exception ex)
        {
            var exhausted = job.AttemptCount >= _options.MaxAttempts;
            if (exhausted)
            {
                job.Status = JobStatus.Failed;
                job.LastError = ex.Message;
                job.NextAttemptAt = null;
                _log.LogError(ex,
                    "Retry job {JobId} exhausted {Max} attempts; marked Failed",
                    job.Id, _options.MaxAttempts);
            }
            else
            {
                job.Status = JobStatus.Retrying;
                job.LastError = ex.Message;
                job.NextAttemptAt = DateTime.UtcNow.Add(Backoff(job.AttemptCount));
                _log.LogWarning(ex,
                    "Retry job {JobId} failed (attempt {Attempt}); next at {NextAttemptAt}",
                    job.Id, job.AttemptCount, job.NextAttemptAt);
            }
            await jobs.UpdateAsync(job, ct);

            if (exhausted)
            {
                // When ResponseUrl is set, Kuali handles the failure email via
                // the workflow integration step. Skip our SMTP path to avoid
                // duplicate alerts. Fall back to email only when there's no
                // workflow callback to honor (e.g. CLI/manual API callers).
                var notified = await NotifyKualiOnceAsync(job, succeeded: false, ex.Message, jobs, kuali, ct);
                if (!notified)
                {
                    await notifications.NotifyJobFailedAsync(job, ct);
                }
            }
        }
    }

    // Returns true if the workflow callback POST succeeded (or was already sent
    // earlier in this job's lifecycle). False means either no ResponseUrl was
    // captured or the POST failed — in which case the caller decides whether to
    // fall back to email and the next retry tick will try the callback again.
    private static async Task<bool> NotifyKualiOnceAsync(
        ImportJob job,
        bool succeeded,
        string? error,
        JobsService jobs,
        KualiResponseUrlNotifier kuali,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ResponseUrl)) return false;
        if (job.KualiNotifiedAt is not null) return true;

        var ok = await kuali.NotifyAsync(job, succeeded, error, ct);
        if (!ok) return false;

        job.KualiNotifiedAt = DateTime.UtcNow;
        await jobs.UpdateAsync(job, ct);
        return true;
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = _options.BaseDelaySeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(seconds, TimeSpan.FromHours(1).TotalSeconds);
        return TimeSpan.FromSeconds(capped);
    }
}
public sealed class BackupCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AppSettings.BackupSettings _backup;
    private readonly AppSettings.RetrySettings _retry;
    private readonly ILogger<BackupCleanupWorker> _log;

    public BackupCleanupWorker(
        IServiceScopeFactory scopes,
        IOptions<AppSettings> settings,
        ILogger<BackupCleanupWorker> log)
    {
        _scopes = scopes;
        _backup = settings.Value.Backup;
        _retry = settings.Value.Retry;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _backup.CleanupIntervalHours));
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Backup cleanup iteration failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobsService>();
        var callbacks = scope.ServiceProvider.GetRequiredService<ExportCallbackStore>();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        var backupCutoff = DateTime.UtcNow.AddDays(-Math.Max(0, _backup.RetentionDays));
        backup.PurgeOlderThan(backupCutoff);

        var prunedRows = 0;
        if (_retry.SucceededJobRetentionDays > 0)
        {
            var jobCutoff = DateTime.UtcNow.AddDays(-_retry.SucceededJobRetentionDays);
            prunedRows = await jobs.DeleteSucceededOlderThanAsync(jobCutoff, ct);
            if (prunedRows > 0)
            {
                _log.LogInformation("Pruned {Count} succeeded ImportJobs older than {Cutoff:o}", prunedRows, jobCutoff);
            }
        }

        // ExportCallbacks is grow-only — Pending rows are orphaned when Kuali
        // never calls back. 7 days is enough to chase a weird export after the weekend.
        try
        {
            var callbackCutoff = DateTime.UtcNow.AddDays(-7);
            var prunedCallbacks = await callbacks.DeleteOlderThanAsync(callbackCutoff, ct);
            if (prunedCallbacks > 0)
            {
                _log.LogInformation("Pruned {Count} ExportCallbacks older than {Cutoff:o}", prunedCallbacks, callbackCutoff);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ExportCallbacks prune failed");
        }

        if (prunedRows > 0)
        {
            try
            {
                db.Vacuum();
                _log.LogInformation("Vacuumed DB after pruning {Count} row(s).", prunedRows);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "VACUUM failed; reclaim deferred to next run.");
            }
        }
    }
}

public sealed class JobEventLog
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
    public const string CleanupDeferred = "CleanupDeferred";
    public const string ImportSucceeded = "ImportSucceeded";
    public const string ImportFailed = "ImportFailed";
}
