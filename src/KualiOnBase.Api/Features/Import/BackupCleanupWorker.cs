using KualiOnBase.Api.Infrastructure.Data;
using KualiOnBase.Api.Configuration;
using KualiOnBase.Api.Features.Import;
using KualiOnBase.Api.Features.Jobs;
using KualiOnBase.Api.Features.Kuali;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Features.Import;

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
