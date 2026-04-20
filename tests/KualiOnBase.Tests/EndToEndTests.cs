using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services.Kuali;
using KualiOnBase.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KualiOnBase.Tests;

public sealed class KualiOnBaseFactory : WebApplicationFactory<Program>
{
    public FakeKualiClient FakeKuali { get; } = new();
    public string Sandbox { get; }
    public string TargetFolder { get; }
    public string BackupRoot { get; }
    public string DbPath { get; }

    public KualiOnBaseFactory()
    {
        Sandbox = Path.Combine(Path.GetTempPath(), "kuali-e2e-" + Guid.NewGuid().ToString("N"));
        TargetFolder = Path.Combine(Sandbox, "target");
        BackupRoot = Path.Combine(Sandbox, "backup");
        DbPath = Path.Combine(Sandbox, "test.db");
        Directory.CreateDirectory(TargetFolder);
        Directory.CreateDirectory(BackupRoot);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:ApiKey"] = "test-key",
                ["Kuali:BaseUrl"] = "http://fake.invalid",
                ["Kuali:ApiToken"] = "fake",
                ["Kuali:PublicBaseUrl"] = "http://fake.invalid",
                ["Kuali:CallbackSecret"] = "test-secret",
                ["Backup:RootPath"] = BackupRoot,
                ["Backup:RetentionDays"] = "30",
                ["Backup:CleanupIntervalHours"] = "99999",
                ["Retry:MaxAttempts"] = "2",
                ["Retry:BaseDelaySeconds"] = "1",
                ["Retry:PollIntervalSeconds"] = "99999",
                ["Database:Path"] = DbPath,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Remove the real Kuali HttpClient-bound registration and
            // replace with the singleton fake.
            var descriptor = services.Single(d => d.ServiceType == typeof(IKualiClient));
            services.Remove(descriptor);
            services.AddSingleton<IKualiClient>(FakeKuali);

            // Drop the hosted services so the tests don't race with background work.
            foreach (var hosted in services.Where(s => s.ServiceType == typeof(IHostedService)).ToList())
            {
                services.Remove(hosted);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Sandbox, recursive: true); } catch { /* best effort */ }
    }
}

public class EndToEndTests : IClassFixture<KualiOnBaseFactory>
{
    private readonly KualiOnBaseFactory _factory;
    private readonly HttpClient _client;

    public EndToEndTests(KualiOnBaseFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_Returns_Ok()
    {
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_ApiKey_Returns_401()
    {
        var response = await _client.PostAsync("/api/kuali-onbase-import", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_ApiKey_Returns_401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/kuali-onbase-import");
        req.Headers.Add("X-Api-Key", "nope");
        var response = await _client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bearer_Token_Is_Accepted()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/kuali-onbase-import");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-key");
        var response = await _client.SendAsync(req);
        // Correct credential → middleware passes → missing-param 400, not 401.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bearer_Token_Wrong_Returns_401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/kuali-onbase-import");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "nope");
        var response = await _client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_Required_Parameters_Returns_400()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/kuali-onbase-import");
        req.Headers.Add("X-Api-Key", "test-key");
        var response = await _client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HappyPath_WritesFiles_ReturnsSucceeded()
    {
        _factory.FakeKuali.Document = new KualiDocument(
            Id: "doc-happy",
            SerialNumber: "0099",
            FirstName: "Drew",
            LastName: "Burt",
            SchoolId: "123456789",
            Attachments: []);
        _factory.FakeKuali.PdfBytes = System.Text.Encoding.UTF8.GetBytes("%PDF-fake");

        var url = BuildUrl(
            documentId: "doc-happy",
            onbaseDocType: "IT - Access",
            targetFolderPath: _factory.TargetFolder,
            downloadMode: "pdf",
            deleteAttachments: false,
            extra: new Dictionary<string, string>
            {
                ["KeywordKey1"] = "Department",
                ["KeywordValue1"] = "ITS",
            });

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("X-Api-Key", "test-key");
        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ImportResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be(JobStatus.Succeeded);
        body.Files.Should().HaveCount(2);

        Directory.EnumerateFiles(_factory.TargetFolder, "*.pdf").Should().NotBeEmpty();
        var indexFile = Directory.EnumerateFiles(_factory.TargetFolder, "*.txt").Single();
        var text = await File.ReadAllTextAsync(indexFile);
        text.Should().Contain("ONBASE_DOC_TYPE: IT - Access");
        text.Should().Contain("Department: ITS");
        text.Should().Contain("EXTERNAL_SOURCE: KUALI BUILD");
    }

    [Fact]
    public async Task Transient_Kuali_Failure_Returns_202_And_Retrying()
    {
        _factory.FakeKuali.ExportPdfThrows = new HttpRequestException("kuali down");

        var url = BuildUrl(
            documentId: "doc-transient",
            onbaseDocType: "DocType",
            targetFolderPath: _factory.TargetFolder,
            downloadMode: "pdf",
            deleteAttachments: false);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("X-Api-Key", "test-key");
        var response = await _client.SendAsync(req);

        try
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var body = await response.Content.ReadFromJsonAsync<ImportResponse>();
            body!.Status.Should().Be(JobStatus.Retrying);
            body.NextAttemptAt.Should().NotBeNull();
            body.Error.Should().Be("kuali down");
        }
        finally
        {
            _factory.FakeKuali.ExportPdfThrows = null;
        }
    }

    [Fact]
    public async Task Missing_TargetFolder_Returns_400()
    {
        var bogus = Path.Combine(_factory.Sandbox, "does-not-exist");
        var url = BuildUrl(
            documentId: "doc-x",
            onbaseDocType: "DT",
            targetFolderPath: bogus,
            downloadMode: "pdf",
            deleteAttachments: false);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("X-Api-Key", "test-key");
        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static string BuildUrl(
        string documentId,
        string onbaseDocType,
        string targetFolderPath,
        string downloadMode,
        bool deleteAttachments,
        IDictionary<string, string>? extra = null)
    {
        var q = new List<KeyValuePair<string, string>>
        {
            new("documentId", documentId),
            new("onbaseDocType", onbaseDocType),
            new("targetFolderPath", targetFolderPath),
            new("downloadMode", downloadMode),
            new("deleteAttachments", deleteAttachments.ToString().ToLowerInvariant()),
        };
        if (extra is not null)
        {
            foreach (var kv in extra)
            {
                q.Add(new(kv.Key, kv.Value));
            }
        }
        var qs = string.Join('&', q.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"/api/kuali-onbase-import?{qs}";
    }
}
