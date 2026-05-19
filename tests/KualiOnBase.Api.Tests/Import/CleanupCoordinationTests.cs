using System.Text;
using System.Text.Json;
using Dapper;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KualiOnBase.Api.Tests.Import;

public sealed class CleanupCoordinationTests
{
    [Fact]
    public async Task SingleCallCleanup_DeferredThenExecutesAfterGracePeriod()
    {
        await using var harness = new CleanupHarness();
        var job = await harness.CreateJobAsync(
            downloadMode: "pdf",
            deleteAttachments: true,
            deleteDocument: true);

        var initial = await harness.Import.RunAsync(job, CancellationToken.None);
        await harness.PersistDeferredAsync(job, initial);

        Assert.True(initial.CleanupDeferred);
        Assert.NotNull(initial.ResumeAt);
        Assert.Equal(0, harness.Kuali.ClearAttachmentsCalls);
        Assert.Equal(0, harness.Kuali.DeleteDocumentCalls);

        harness.SetJobWindow(job, DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow.AddSeconds(-1));

        var finalized = await harness.Import.RunAsync(job, CancellationToken.None);
        await harness.PersistSucceededAsync(job, finalized);

        Assert.False(finalized.CleanupDeferred);
        Assert.Equal(1, harness.Kuali.ClearAttachmentsCalls);
        Assert.Equal(1, harness.Kuali.DeleteDocumentCalls);
        Assert.True(await harness.Queue.CleanupAlreadySucceededInWindowAsync(
            job.DocumentId,
            job.CreatedAt,
            TimeSpan.FromMinutes(2),
            CancellationToken.None));
    }

    [Fact]
    public async Task PairedCalls_AggregateCleanupFlags_AndCleanupRunsOnce()
    {
        await using var harness = new CleanupHarness();

        var pdfJob = await harness.CreateJobAsync(
            downloadMode: "pdf",
            deleteAttachments: true,
            deleteDocument: false);
        var attachmentsJob = await harness.CreateJobAsync(
            downloadMode: "attachments",
            deleteAttachments: false,
            deleteDocument: true);

        var pdfInitial = await harness.Import.RunAsync(pdfJob, CancellationToken.None);
        await harness.PersistDeferredAsync(pdfJob, pdfInitial);

        var attachmentsInitial = await harness.Import.RunAsync(attachmentsJob, CancellationToken.None);
        await harness.PersistDeferredAsync(attachmentsJob, attachmentsInitial);

        var requestedAt = DateTime.UtcNow.AddMinutes(-3);
        var readyAt = DateTime.UtcNow.AddSeconds(-1);
        harness.SetJobWindow(pdfJob, requestedAt, readyAt);
        harness.SetJobWindow(attachmentsJob, requestedAt.AddSeconds(10), readyAt);

        var finalized = await harness.Import.RunAsync(attachmentsJob, CancellationToken.None);
        await harness.PersistSucceededAsync(attachmentsJob, finalized);

        Assert.False(finalized.CleanupDeferred);
        Assert.Equal(1, harness.Kuali.ClearAttachmentsCalls);
        Assert.Equal(1, harness.Kuali.DeleteDocumentCalls);

        var secondFinalizer = await harness.Import.RunAsync(pdfJob, CancellationToken.None);

        Assert.False(secondFinalizer.CleanupDeferred);
        Assert.Equal(1, harness.Kuali.ClearAttachmentsCalls);
        Assert.Equal(1, harness.Kuali.DeleteDocumentCalls);
    }

    [Fact]
    public async Task SiblingOutsideGraceWindow_DoesNotBlockSingleCallCleanup()
    {
        await using var harness = new CleanupHarness();

        var pdfJob = await harness.CreateJobAsync(
            downloadMode: "pdf",
            deleteAttachments: true,
            deleteDocument: false);
        var attachmentsJob = await harness.CreateJobAsync(
            downloadMode: "attachments",
            deleteAttachments: false,
            deleteDocument: false);

        var pdfInitial = await harness.Import.RunAsync(pdfJob, CancellationToken.None);
        await harness.PersistDeferredAsync(pdfJob, pdfInitial);

        var attachmentsInitial = await harness.Import.RunAsync(attachmentsJob, CancellationToken.None);
        await harness.PersistDeferredAsync(attachmentsJob, attachmentsInitial);

        var requestedAt = DateTime.UtcNow.AddMinutes(-4);
        harness.SetJobWindow(pdfJob, requestedAt, DateTime.UtcNow.AddSeconds(-1));
        harness.SetJobWindow(attachmentsJob, requestedAt.AddMinutes(3), DateTime.UtcNow.AddSeconds(-1));

        var finalized = await harness.Import.RunAsync(pdfJob, CancellationToken.None);

        Assert.False(finalized.CleanupDeferred);
        Assert.Equal(1, harness.Kuali.ClearAttachmentsCalls);
        Assert.Equal(0, harness.Kuali.DeleteDocumentCalls);
    }

    private sealed class CleanupHarness : IAsyncDisposable
    {
        private readonly string _root;

        public CleanupHarness()
        {
            _root = Path.Combine(Path.GetTempPath(), $"kuali2ob-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);

            TargetRoot = Path.Combine(_root, "target");
            BackupRoot = Path.Combine(_root, "backup");
            Directory.CreateDirectory(TargetRoot);
            Directory.CreateDirectory(BackupRoot);

            var settings = Options.Create(new AppSettings
            {
                Database = new AppSettings.DatabaseSettings
                {
                    Path = Path.Combine(_root, "test.db")
                },
                Backup = new AppSettings.BackupSettings
                {
                    RootPath = BackupRoot
                },
                Import = new AppSettings.ImportSettings
                {
                    AllowedTargetRoots = TargetRoot
                }
            });

            Db = new Db(settings);
            Db.Migrate();

            Queue = new JobsService(Db);
            Kuali = new FakeKualiClient();
            Import = new ImportService(
                Kuali,
                new BackupService(settings, NullLogger<BackupService>.Instance),
                new JobEventLog(Db, NullLogger<JobEventLog>.Instance),
                Queue,
                settings,
                NullLogger<ImportService>.Instance);
        }

        public string TargetRoot { get; }
        public string BackupRoot { get; }
        public Db Db { get; }
        public JobsService Queue { get; }
        public FakeKualiClient Kuali { get; }
        public ImportService Import { get; }

        public async Task<ImportJob> CreateJobAsync(
            string downloadMode,
            bool deleteAttachments,
            bool deleteDocument)
        {
            var job = new ImportJob
            {
                DocumentId = "doc-123",
                OnBaseDocType = "StudentForm",
                TargetFolderPath = TargetRoot,
                DownloadMode = downloadMode,
                DeleteAttachments = deleteAttachments,
                DeleteDocument = deleteDocument,
                KeywordsJson = JsonSerializer.Serialize(new[]
                {
                    new KeyValuePair<string, string>("StudentId", "900000001")
                }),
                Status = JobStatus.Running,
                AttemptCount = 1,
            };

            await Queue.InsertAsync(job, CancellationToken.None);
            return job;
        }

        public async Task PersistDeferredAsync(ImportJob job, ImportResult result)
        {
            job.BackupFolderPath = result.BackupFolder;
            job.ProducedFiles = JsonSerializer.Serialize(result.ProducedFiles);
            job.Status = JobStatus.Retrying;
            job.LastError = result.CleanupMessage;
            job.NextAttemptAt = result.ResumeAt;
            await Queue.UpdateAsync(job, CancellationToken.None);
        }

        public async Task PersistSucceededAsync(ImportJob job, ImportResult result)
        {
            job.BackupFolderPath = result.BackupFolder;
            job.ProducedFiles = JsonSerializer.Serialize(result.ProducedFiles);
            job.Status = JobStatus.Succeeded;
            job.LastError = null;
            job.NextAttemptAt = null;
            await Queue.UpdateAsync(job, CancellationToken.None);
        }

        public void SetJobWindow(ImportJob job, DateTime createdAtUtc, DateTime readyAtUtc)
        {
            job.CreatedAt = createdAtUtc;
            job.UpdatedAt = createdAtUtc;
            job.NextAttemptAt = readyAtUtc;

            using var conn = Db.Open();
            conn.Execute(
                """
                UPDATE ImportJobs
                   SET CreatedAt = @CreatedAt,
                       UpdatedAt = @CreatedAt,
                       NextAttemptAt = @ReadyAt
                 WHERE Id = @JobId;
                """,
                new
                {
                    JobId = job.Id,
                    CreatedAt = createdAtUtc,
                    ReadyAt = readyAtUtc,
                });
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test assets.
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeKualiClient : IKualiClient
    {
        private bool _attachmentsCleared;

        public int ClearAttachmentsCalls { get; private set; }
        public int DeleteDocumentCalls { get; private set; }

        public Task<KualiDocument> GetDocumentAsync(string documentId, CancellationToken ct)
        {
            IReadOnlyList<KualiAttachment> attachments = _attachmentsCleared
                ? Array.Empty<KualiAttachment>()
                : new[]
                {
                    new KualiAttachment(
                        "att-1",
                        "supportingDocuments.primary",
                        "supporting-document.docx",
                        "https://files.example.edu/supporting-document.docx")
                };

            return Task.FromResult(new KualiDocument(
                documentId,
                "SN-12345",
                "Casey",
                "Student",
                "900000001",
                attachments,
                "{}"));
        }

        public Task<string> ExportPdfAsync(
            string documentId,
            IReadOnlyList<string> exportOptions,
            CancellationToken ct) =>
            Task.FromResult("https://files.example.edu/export.pdf");

        public async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? "pdf-bytes"
                : "attachment-bytes");
            await File.WriteAllBytesAsync(destinationPath, bytes, ct);
        }

        public Task ClearAttachmentsAsync(string documentId, IReadOnlyList<string> fieldPaths, CancellationToken ct)
        {
            ClearAttachmentsCalls += 1;
            _attachmentsCleared = true;
            return Task.CompletedTask;
        }

        public Task DeleteDocumentAsync(string documentId, CancellationToken ct)
        {
            DeleteDocumentCalls += 1;
            return Task.CompletedTask;
        }
    }

}
