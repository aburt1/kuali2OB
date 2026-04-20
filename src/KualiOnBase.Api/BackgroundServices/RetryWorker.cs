using System.Text.Json;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services;
using KualiOnBase.Api.Services.Import;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.BackgroundServices;

public sealed class RetryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RetryOptions _options;
    private readonly ILogger<RetryWorker> _log;

    public RetryWorker(
        IServiceScopeFactory scopes,
        IOptions<RetryOptions> options,
        ILogger<RetryWorker> log)
    {
        _scopes = scopes;
        _options = options.Value;
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
        var queue = scope.ServiceProvider.GetRequiredService<RetryQueue>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IImportOrchestrator>();

        var due = await queue.DueForRetryAsync(DateTime.UtcNow, ct);
        foreach (var job in due)
        {
            if (ct.IsCancellationRequested) break;
            await RunJobAsync(job, queue, orchestrator, ct);
        }
    }

    private async Task RunJobAsync(
        ImportJob job,
        RetryQueue queue,
        IImportOrchestrator orchestrator,
        CancellationToken ct)
    {
        job.AttemptCount += 1;
        job.Status = JobStatus.Running;
        job.UpdatedAt = DateTime.UtcNow;
        await queue.UpdateAsync(job, ct);

        try
        {
            var result = await orchestrator.RunAsync(job, ct);
            job.Status = JobStatus.Succeeded;
            job.BackupFolderPath = result.BackupFolder;
            job.ProducedFiles = JsonSerializer.Serialize(result.ProducedFiles);
            job.LastError = null;
            job.NextAttemptAt = null;
            await queue.UpdateAsync(job, ct);
            _log.LogInformation("Retry job {JobId} succeeded on attempt {Attempt}", job.Id, job.AttemptCount);
        }
        catch (Exception ex)
        {
            if (job.AttemptCount >= _options.MaxAttempts)
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
            await queue.UpdateAsync(job, ct);
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = _options.BaseDelaySeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(seconds, TimeSpan.FromHours(1).TotalSeconds);
        return TimeSpan.FromSeconds(capped);
    }
}
