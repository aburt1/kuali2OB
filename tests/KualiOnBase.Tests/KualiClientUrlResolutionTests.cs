using FluentAssertions;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services.Kuali;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Tests;

public class KualiClientUrlResolutionTests
{
    private static KualiClient BuildClient(string baseUrl) =>
        new(
            new HttpClient(),
            Options.Create(new KualiOptions { BaseUrl = baseUrl, ApiToken = "tok" }),
            new NoopCallbackStore(),
            NullLogger<KualiClient>.Instance);

    [Fact]
    public void ResolveDownloadUrl_LeadingSlashPath_IsTreatedAsRelativeAndAuthenticated()
    {
        // Regression: on Unix, Uri.TryCreate("/app/forms/...", Absolute) succeeds as file:///
        // and we'd hand that to HttpClient → "The 'file' scheme is not supported."
        var client = BuildClient("https://csub.kualibuild.com");

        var (uri, useAuth) = client.ResolveDownloadUrl("/app/forms/api/v2/files/perma/abc");

        uri.Scheme.Should().Be("https");
        uri.Host.Should().Be("csub.kualibuild.com");
        uri.AbsolutePath.Should().Be("/app/forms/api/v2/files/perma/abc");
        useAuth.Should().BeTrue();
    }

    [Fact]
    public void ResolveDownloadUrl_RelativeWithoutLeadingSlash_Authenticated()
    {
        var client = BuildClient("https://csub.kualibuild.com/");
        var (uri, useAuth) = client.ResolveDownloadUrl("app/forms/api/v2/files/x");
        uri.AbsoluteUri.Should().Be("https://csub.kualibuild.com/app/forms/api/v2/files/x");
        useAuth.Should().BeTrue();
    }

    [Fact]
    public void ResolveDownloadUrl_SameHostAbsolute_Authenticated()
    {
        var client = BuildClient("https://csub.kualibuild.com");
        var (_, useAuth) = client.ResolveDownloadUrl("https://csub.kualibuild.com/app/forms/api/v2/files/x");
        useAuth.Should().BeTrue();
    }

    [Fact]
    public void ResolveDownloadUrl_ExternalHttpsUrl_NoAuth()
    {
        var client = BuildClient("https://csub.kualibuild.com");
        var (_, useAuth) = client.ResolveDownloadUrl("https://s3.amazonaws.com/bucket/signed?x=1");
        useAuth.Should().BeFalse();
    }

    private sealed class NoopCallbackStore : IExportCallbackStore
    {
        public Task CreatePendingAsync(string correlationId, string documentId, CancellationToken ct) => Task.CompletedTask;
        public Task<ExportCallbackRow?> GetAsync(string correlationId, CancellationToken ct) => Task.FromResult<ExportCallbackRow?>(null);
        public Task MarkCompletedAsync(string correlationId, string signedUrl, CancellationToken ct) => Task.CompletedTask;
        public Task MarkFailedAsync(string correlationId, string error, CancellationToken ct) => Task.CompletedTask;
        public Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct) => Task.FromResult(0);
    }
}
