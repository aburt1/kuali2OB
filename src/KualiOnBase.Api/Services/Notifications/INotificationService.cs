using KualiOnBase.Api.Models;

namespace KualiOnBase.Api.Services.Notifications;

public interface INotificationService
{
    // Fires when a job reaches a terminal Failed state (either synchronously in
    // the endpoint or after the RetryWorker exhausts attempts). Implementations
    // must never throw — notification failures must not obscure the job failure.
    Task NotifyJobFailedAsync(ImportJob job, CancellationToken ct);
}
