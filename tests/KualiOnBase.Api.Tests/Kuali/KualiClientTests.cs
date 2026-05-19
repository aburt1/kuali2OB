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
        var root = Path.Combine(Path.GetTempPath(), $"kuali2ob-kuali-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        using var http = new HttpClient
        {
            BaseAddress = new Uri("https://kuali.example.edu/")
        };

        try
        {
            var settings = Options.Create(new AppSettings
            {
                Database = new AppSettings.DatabaseSettings
                {
                    Path = Path.Combine(root, "test.db")
                }
            });
            var db = new Db(settings);
            db.Migrate();

            var client = new KualiClient(
                http,
                new StubHttpClientFactory(),
                settings,
                new ExportCallbackStore(db),
                NullLogger<KualiClient>.Instance);

            var resolved = client.ResolveDownloadUrl("/files/123");

            Assert.True(resolved.UseAuth);
            Assert.Equal(new Uri("https://kuali.example.edu/files/123"), resolved.Uri);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

}
