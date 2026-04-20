using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using KualiOnBase.Api.Endpoints;
using KualiOnBase.Api.Services.Kuali;
using Microsoft.Extensions.DependencyInjection;

namespace KualiOnBase.Tests;

public class CallbackEndpointTests : IClassFixture<KualiOnBaseFactory>
{
    private const string Secret = "test-secret";
    private readonly KualiOnBaseFactory _factory;
    private readonly HttpClient _client;

    public CallbackEndpointTests(KualiOnBaseFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Valid_Signature_Records_SignedUrl()
    {
        var id = Guid.NewGuid().ToString("N");
        var sig = KualiCallbackSigner.Sign(id, Secret);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IExportCallbackStore>();
        await store.CreatePendingAsync(id, "doc-cb-1", TestContext.Ct);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/kuali-export-callback/{id}?sig={sig}")
        {
            Content = JsonContent.Create(new { url = "https://signed.example/pdf.pdf" }),
        };
        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await store.GetAsync(id, TestContext.Ct);
        row!.Status.Should().Be("Completed");
        row.SignedUrl.Should().Be("https://signed.example/pdf.pdf");
    }

    [Fact]
    public async Task Invalid_Signature_Returns_401()
    {
        var id = Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IExportCallbackStore>();
        await store.CreatePendingAsync(id, "doc-cb-2", TestContext.Ct);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/kuali-export-callback/{id}?sig=deadbeef")
        {
            Content = JsonContent.Create(new { url = "https://signed.example/pdf.pdf" }),
        };
        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_CorrelationId_Returns_404()
    {
        var id = Guid.NewGuid().ToString("N");
        var sig = KualiCallbackSigner.Sign(id, Secret);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/kuali-export-callback/{id}?sig={sig}")
        {
            Content = JsonContent.Create(new { url = "https://signed.example/pdf.pdf" }),
        };
        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Missing_Url_Returns_400()
    {
        var id = Guid.NewGuid().ToString("N");
        var sig = KualiCallbackSigner.Sign(id, Secret);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IExportCallbackStore>();
        await store.CreatePendingAsync(id, "doc-cb-3", TestContext.Ct);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/kuali-export-callback/{id}?sig={sig}")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Error_Payload_Is_Recorded()
    {
        var id = Guid.NewGuid().ToString("N");
        var sig = KualiCallbackSigner.Sign(id, Secret);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IExportCallbackStore>();
        await store.CreatePendingAsync(id, "doc-cb-4", TestContext.Ct);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/kuali-export-callback/{id}?sig={sig}")
        {
            Content = JsonContent.Create(new { error = "render failed" }),
        };
        var response = await _client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await store.GetAsync(id, TestContext.Ct);
        row!.Status.Should().Be("Failed");
        row.ErrorMessage.Should().Be("render failed");
    }
}

internal static class TestContext
{
    public static CancellationToken Ct => CancellationToken.None;
}
