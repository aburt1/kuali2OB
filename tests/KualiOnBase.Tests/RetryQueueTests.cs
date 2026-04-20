using FluentAssertions;
using KualiOnBase.Api.Data;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Tests;

public class RetryQueueTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Db _db;
    private readonly RetryQueue _queue;

    public RetryQueueTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kuali-onbase-{Guid.NewGuid():N}.db");
        _db = new Db(Options.Create(new DatabaseOptions { Path = _dbPath }));
        _db.Migrate();
        _queue = new RetryQueue(_db);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Insert_AssignsIdAndRoundTripsFields()
    {
        var job = NewJob();
        var id = await _queue.InsertAsync(job, CancellationToken.None);

        id.Should().BeGreaterThan(0);
        job.Id.Should().Be(id);

        var reloaded = await _queue.GetAsync(id, CancellationToken.None);
        reloaded.Should().NotBeNull();
        reloaded!.DocumentId.Should().Be(job.DocumentId);
        reloaded.Status.Should().Be(JobStatus.Running);
        reloaded.DeleteAttachments.Should().Be(job.DeleteAttachments);
    }

    [Fact]
    public async Task Update_PersistsRetryState()
    {
        var job = NewJob();
        await _queue.InsertAsync(job, CancellationToken.None);

        job.Status = JobStatus.Retrying;
        job.AttemptCount = 2;
        job.NextAttemptAt = DateTime.UtcNow.AddMinutes(5);
        job.LastError = "transient";

        await _queue.UpdateAsync(job, CancellationToken.None);

        var reloaded = await _queue.GetAsync(job.Id, CancellationToken.None);
        reloaded!.Status.Should().Be(JobStatus.Retrying);
        reloaded.AttemptCount.Should().Be(2);
        reloaded.NextAttemptAt.Should().NotBeNull();
        reloaded.LastError.Should().Be("transient");
    }

    [Fact]
    public async Task DueForRetry_ReturnsOnlyRetryingJobsPastDue()
    {
        var past = NewJob();
        past.Status = JobStatus.Retrying;
        past.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
        await _queue.InsertAsync(past, CancellationToken.None);

        var future = NewJob();
        future.Status = JobStatus.Retrying;
        future.NextAttemptAt = DateTime.UtcNow.AddHours(1);
        await _queue.InsertAsync(future, CancellationToken.None);

        var running = NewJob();
        running.Status = JobStatus.Running;
        running.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
        await _queue.InsertAsync(running, CancellationToken.None);

        var due = await _queue.DueForRetryAsync(DateTime.UtcNow, CancellationToken.None);

        due.Should().ContainSingle().Which.Id.Should().Be(past.Id);
    }

    [Fact]
    public async Task DeleteSucceededOlderThan_RemovesOldSucceededRowsOnly()
    {
        var oldSucceeded = NewJob();
        oldSucceeded.Status = JobStatus.Succeeded;
        await _queue.InsertAsync(oldSucceeded, CancellationToken.None);
        oldSucceeded.Status = JobStatus.Succeeded;
        await _queue.UpdateAsync(oldSucceeded, CancellationToken.None);

        // Force UpdatedAt into the past via direct SQL
        using (var c = _db.Open())
        {
            Dapper.SqlMapper.Execute(c,
                "UPDATE ImportJobs SET UpdatedAt = @When WHERE Id = @Id",
                new { When = DateTime.UtcNow.AddDays(-60), Id = oldSucceeded.Id });
        }

        var fresh = NewJob();
        fresh.Status = JobStatus.Succeeded;
        await _queue.InsertAsync(fresh, CancellationToken.None);

        var deleted = await _queue.DeleteSucceededOlderThanAsync(DateTime.UtcNow.AddDays(-30), CancellationToken.None);

        deleted.Should().Be(1);
        (await _queue.GetAsync(oldSucceeded.Id, CancellationToken.None)).Should().BeNull();
        (await _queue.GetAsync(fresh.Id, CancellationToken.None)).Should().NotBeNull();
    }

    private static ImportJob NewJob() => new()
    {
        DocumentId = "doc-1",
        OnBaseDocType = "IT",
        TargetFolderPath = "/tmp",
        DownloadMode = "pdf",
        DeleteAttachments = true,
        DeleteDocument = false,
        KeywordsJson = "[]",
        Status = JobStatus.Running,
        AttemptCount = 1,
    };
}
