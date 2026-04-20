using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services;
using KualiOnBase.Api.Services.Import;
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
                RunOnce(stoppingToken);
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

    private void RunOnce(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var backup = scope.ServiceProvider.GetRequiredService<IBackupService>();
        var queue = scope.ServiceProvider.GetRequiredService<RetryQueue>();

        var backupCutoff = DateTime.UtcNow.AddDays(-Math.Max(0, _backup.RetentionDays));
        backup.PurgeOlderThan(backupCutoff);

        if (_retry.SucceededJobRetentionDays > 0)
        {
            var jobCutoff = DateTime.UtcNow.AddDays(-_retry.SucceededJobRetentionDays);
            var deleted = queue.DeleteSucceededOlderThanAsync(jobCutoff, ct).GetAwaiter().GetResult();
            if (deleted > 0)
            {
                _log.LogInformation("Pruned {Count} succeeded ImportJobs older than {Cutoff:o}", deleted, jobCutoff);
            }
        }
    }
}
