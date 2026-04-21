using System.Text.Json;
using FluentAssertions;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services.Import;
using KualiOnBase.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Tests;

public class ImportOrchestratorTests : IDisposable
{
    private readonly string _sandbox;
    private readonly BackupService _backup;
    private readonly FakeKualiClient _kuali;
    private readonly ImportOrchestrator _sut;

    public ImportOrchestratorTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kuali-onbase-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        Directory.CreateDirectory(TargetFolder);
        Directory.CreateDirectory(BackupRoot);

        _kuali = new FakeKualiClient();
        var backupOptions = Options.Create(new BackupOptions { RootPath = BackupRoot, RetentionDays = 30 });
        _backup = new BackupService(backupOptions, NullLogger<BackupService>.Instance);
        _sut = new ImportOrchestrator(_kuali, _backup, new NoOpJobEventLog(), NullLogger<ImportOrchestrator>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private string TargetFolder => Path.Combine(_sandbox, "target");
    private string BackupRoot => Path.Combine(_sandbox, "backup");

    [Fact]
    public async Task Run_PdfMode_WritesPdfAndIndex()
    {
        var job = BuildJob("pdf");

        var result = await _sut.RunAsync(job, CancellationToken.None);

        result.ProducedFiles.Should().HaveCount(2);
        result.ProducedFiles.Should().Contain(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        result.ProducedFiles.Should().Contain(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

        Directory.EnumerateFiles(TargetFolder).Should().HaveCount(2);
        Directory.EnumerateFiles(result.BackupFolder).Should().HaveCount(2);

        var indexFile = Directory.EnumerateFiles(TargetFolder, "*.txt").Single();
        var text = await File.ReadAllTextAsync(indexFile);
        text.Should().Contain("ONBASE_DOC_TYPE: IT - Access");
        text.Should().Contain("EXTERNAL_SOURCE: KUALI BUILD");
        text.Should().Contain("EXTERNAL_SOURCE_REF: 0014");
    }

    [Fact]
    public async Task Run_PdfMode_SendsCanonicalCombinedOption()
    {
        // Canonical Kuali option per their developer docs. Actual merge behavior
        // is controlled by the tenant setting "Include PDFs uploaded through
        // the form", not by the option array (see README → tenant prerequisite).
        await _sut.RunAsync(BuildJob("pdf"), CancellationToken.None);
        _kuali.LastExportOptions.Should().Equal("Combined");
    }

    [Fact]
    public async Task Run_AttachmentsMode_DownloadsRawFiles_PreservesExtensions()
    {
        // Kuali's 'Attachments' export only works when every attachment is a PDF.
        // Raw download path preserves the user's original formats (.docx, .jpg, …).
        _kuali.Document = _kuali.Document with
        {
            Attachments =
            [
                new KualiAttachment("a1", "data.form.uploads[0]", "resume.docx", "https://fake/a1"),
                new KualiAttachment("a2", "data.form.uploads[1]", "scan.pdf", "https://fake/a2"),
            ],
        };
        _kuali.AttachmentBytes["https://fake/a1"] = [1, 2, 3];
        _kuali.AttachmentBytes["https://fake/a2"] = [4, 5, 6];

        var job = BuildJob("attachments");

        var result = await _sut.RunAsync(job, CancellationToken.None);

        _kuali.LastExportOptions.Should().BeNull("attachments mode must not call ExportPdfAsync");

        var contentFiles = Directory.EnumerateFiles(TargetFolder)
            .Where(p => !p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();
        contentFiles.Should().HaveCount(2);
        contentFiles.Should().Contain(n => n!.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
        contentFiles.Should().Contain(n => n!.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Run_AttachmentsMode_NoAttachments_ThrowsDescriptiveError()
    {
        _kuali.Document = _kuali.Document with { Attachments = [] };
        var job = BuildJob("attachments");

        var act = () => _sut.RunAsync(job, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("0 attachments");
    }

    [Fact]
    public async Task Run_DeleteAttachmentsFlag_CallsClearAttachments()
    {
        _kuali.Document = _kuali.Document with
        {
            Attachments = [new KualiAttachment("a", "data.uploads", "x.pdf", "https://fake/a")],
        };

        var job = BuildJob("pdf", deleteAttachments: true);

        await _sut.RunAsync(job, CancellationToken.None);

        _kuali.AttachmentsCleared.Should().BeTrue();
        _kuali.ClearedFieldPaths.Should().ContainSingle().Which.Should().Be("data.uploads");
        _kuali.DocumentDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task Run_DeleteDocumentFlag_CallsDeleteDocument()
    {
        var job = BuildJob("pdf", deleteDocument: true);
        await _sut.RunAsync(job, CancellationToken.None);
        _kuali.DocumentDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task Run_TargetFolderMissing_Throws()
    {
        var job = BuildJob("pdf");
        job.TargetFolderPath = Path.Combine(_sandbox, "does-not-exist");

        var act = () => _sut.RunAsync(job, CancellationToken.None);
        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Run_TransientKualiFailure_Propagates()
    {
        _kuali.ExportPdfThrows = new HttpRequestException("boom");
        var job = BuildJob("pdf");

        var act = () => _sut.RunAsync(job, CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Run_WritesKeywordsFromJobIntoIndex()
    {
        var job = BuildJob("pdf");
        job.KeywordsJson = JsonSerializer.Serialize(new[]
        {
            new { Key = "Department", Value = "ITS" },
            new { Key = "Term",       Value = "Fall 2026" },
        });

        var result = await _sut.RunAsync(job, CancellationToken.None);
        var index = Directory.EnumerateFiles(TargetFolder, "*.txt").Single();
        var text = await File.ReadAllTextAsync(index);
        text.Should().Contain("Department: ITS");
        text.Should().Contain("Term: Fall 2026");
    }

    private ImportJob BuildJob(string mode, bool deleteAttachments = false, bool deleteDocument = false) =>
        new()
        {
            Id = 1,
            DocumentId = "doc-1",
            OnBaseDocType = "IT - Access",
            TargetFolderPath = TargetFolder,
            DownloadMode = mode,
            DeleteAttachments = deleteAttachments,
            DeleteDocument = deleteDocument,
            KeywordsJson = "[]",
            Status = JobStatus.Running,
            AttemptCount = 1,
        };
}
