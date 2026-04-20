using KualiOnBase.Api.Services;

namespace KualiOnBase.Tests.Fakes;

public sealed class NoOpJobEventLog : IJobEventLog
{
    public Task LogAsync(long jobId, string kind, string? message, object? payload, CancellationToken ct)
        => Task.CompletedTask;

    public Task<IReadOnlyList<JobEventRow>> ListForJobAsync(long jobId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JobEventRow>>(Array.Empty<JobEventRow>());

    public Task<IReadOnlyList<JobEventRow>> ListForJobsAsync(IReadOnlyCollection<long> jobIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<JobEventRow>>(Array.Empty<JobEventRow>());
}
