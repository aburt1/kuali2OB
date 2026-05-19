using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KualiOnBase.Api.Tests.Kuali;

public sealed class KualiClientTests
{
    [Fact]
    public void ResolveDownloadUrl_RootRelativePath_UsesSameOriginWithAuth()
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri("https://kuali.example.edu/")
        };

        var client = new KualiClient(
            http,
            new StubHttpClientFactory(),
            Options.Create(new KualiOptions()),
            new StubExportCallbackStore(),
            NullLogger<KualiClient>.Instance);

        var resolved = client.ResolveDownloadUrl("/files/123");

        Assert.True(resolved.UseAuth);
        Assert.Equal(new Uri("https://kuali.example.edu/files/123"), resolved.Uri);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubExportCallbackStore : IExportCallbackStore
    {
        public Task CreatePendingAsync(string correlationId, string documentId, CancellationToken ct) => Task.CompletedTask;
        public Task<ExportCallbackRow?> GetAsync(string correlationId, CancellationToken ct) => Task.FromResult<ExportCallbackRow?>(null);
        public Task<bool> MarkCompletedAsync(string correlationId, string signedUrl, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> MarkFailedAsync(string correlationId, string error, CancellationToken ct) => Task.FromResult(false);
        public Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct) => Task.FromResult(0);
    }
}
