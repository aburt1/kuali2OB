using KualiOnBase.Api.Features.Import;
using KualiOnBase.Api.Features.Kuali;

namespace KualiOnBase.Api.Features.Notifications;

public interface INotificationService
{
    // Fires when a job reaches a terminal Failed state (either synchronously in
    // the endpoint or after the RetryWorker exhausts attempts). Implementations
    // must never throw — notification failures must not obscure the job failure.
    Task NotifyJobFailedAsync(ImportJob job, CancellationToken ct);
}
