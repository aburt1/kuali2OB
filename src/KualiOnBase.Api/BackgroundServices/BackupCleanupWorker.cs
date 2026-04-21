using KualiOnBase.Api.Data;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services;
using KualiOnBase.Api.Services.Import;
using KualiOnBase.Api.Services.Kuali;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.BackgroundServices;

public sealed class BackupCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BackupOptions _backup;
    private readonly RetryOptions _retry;
    private readonly ILogger<BackupCleanupWorker> _log;

    public BackupCleanupWorker(
        IServiceScopeFactory scopes,
        IOptions<BackupOptions> backup,
        IOptions<RetryOptions> retry,
        ILogger<BackupCleanupWorker> log)
    {
        _scopes = scopes;
        _backup = backup.Value;
        _retry = retry.Value;
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
                // H11: previously this called a synchronous RunOnce that in turn
                // used .GetAwaiter().GetResult() to call async DB code. That's a
                // sync-over-async on a BackgroundService thread — under load it
                // can deadlock (SqliteConnection captures the sync context in
                // some hosting configurations) and it blocks the thread the
                // entire time. Now fully async end-to-end.
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
        var backup = scope.ServiceProvider.GetRequiredService<IBackupService>();
        var queue = scope.ServiceProvider.GetRequiredService<RetryQueue>();
        var callbacks = scope.ServiceProvider.GetRequiredService<IExportCallbackStore>();
        var db = scope.ServiceProvider.GetRequiredService<Db>();

        var backupCutoff = DateTime.UtcNow.AddDays(-Math.Max(0, _backup.RetentionDays));
        backup.PurgeOlderThan(backupCutoff);

        var prunedRows = 0;
        if (_retry.SucceededJobRetentionDays > 0)
        {
            var jobCutoff = DateTime.UtcNow.AddDays(-_retry.SucceededJobRetentionDays);
            prunedRows = await queue.DeleteSucceededOlderThanAsync(jobCutoff, ct);
            if (prunedRows > 0)
            {
                _log.LogInformation("Pruned {Count} succeeded ImportJobs older than {Cutoff:o}", prunedRows, jobCutoff);
            }
        }

        // H13: ExportCallbacks is a grow-only table if nothing prunes it. Every
        // PDF export creates a row; only 'Completed'/'Failed' rows get written
        // back, 'Pending' rows are orphaned when Kuali never calls back.
        // Use a fixed 7-day retention — callback rows are diagnostic-only after
        // the orchestrator has resolved the signed URL, they're not needed for
        // replay. 7 days is enough for operators to chase a weird export on
        // Monday that happened the previous Tuesday.
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

        // H6: plain VACUUM acquires an exclusive lock and rewrites the whole
        // database file — on a busy SQLite DB that blocks every writer for the
        // duration. We switched to incremental auto-vacuum (PRAGMA in migration
        // 006) which reclaims pages opportunistically; `PRAGMA incremental_vacuum`
        // returns pages without an exclusive rewrite. Only run it when we
        // actually freed pages by deleting rows.
        if (prunedRows > 0)
        {
            try
            {
                db.IncrementalVacuum();
                _log.LogInformation(
                    "Ran PRAGMA incremental_vacuum after pruning {Count} row(s).",
                    prunedRows);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "incremental_vacuum failed; disk usage will be reclaimed on the next successful run.");
            }
        }
    }
}
