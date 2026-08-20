using KualiOnBase.Api.Controllers;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace KualiOnBase.Api.Tests.Configuration;

/// <summary>
/// The operator credential opens every route. The optional import credential — the
/// one pasted into Kuali's HTTP Action, visible to any Kuali app administrator —
/// opens only the import endpoint.
/// </summary>
public sealed class ApiKeyScopeTests
{
    private const string OperatorKey = "operator-key-long-enough";
    private const string ImportKey = "import-key-long-enough-x";

    private sealed class StubMonitor : IOptionsMonitor<AppSettings>
    {
        private readonly AppSettings _value;
        public StubMonitor(AppSettings value) => _value = value;
        public AppSettings CurrentValue => _value;
        public AppSettings Get(string? name) => _value;
        public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
    }

    private static async Task<int> InvokeAsync(string path, string method, string? token, bool splitEnabled)
    {
        var settings = new AppSettings();
        settings.Auth.ApiKey = OperatorKey;
        if (splitEnabled) settings.Auth.ImportApiKey = ImportKey;

        var reached = false;
        var middleware = new ApiKeyMiddleware(_ => { reached = true; return Task.CompletedTask; },
            new StubMonitor(settings));

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        if (token is not null) context.Request.Headers["Authorization"] = "Bearer " + token;

        await middleware.Invoke(context);
        return reached ? 200 : context.Response.StatusCode;
    }

    [Fact]
    public async Task OperatorKeyOpensTheImportEndpoint()
        => Assert.Equal(200, await InvokeAsync(ApiController.ImportRoute, "POST", OperatorKey, splitEnabled: true));

    [Fact]
    public async Task OperatorKeyOpensTheJobsEndpoint()
        => Assert.Equal(200, await InvokeAsync("/api/jobs", "GET", OperatorKey, splitEnabled: true));

    [Fact]
    public async Task ImportKeyOpensTheImportEndpoint()
        => Assert.Equal(200, await InvokeAsync(ApiController.ImportRoute, "POST", ImportKey, splitEnabled: true));

    // The point of the split: a leaked Kuali-side key cannot read stored job
    // payloads or download the confidential documents themselves.
    [Theory]
    [InlineData("/api/jobs", "GET")]
    [InlineData("/api/jobs/1/files/0", "GET")]
    [InlineData("/api/diag/db-status", "GET")]
    [InlineData("/api/diag/kuali-probe-export", "POST")]
    public async Task ImportKeyIsRejectedEverywhereElse(string path, string method)
        => Assert.Equal(401, await InvokeAsync(path, method, ImportKey, splitEnabled: true));

    [Fact]
    public async Task ImportKeyIsRejectedOnAGetToTheImportRoute()
        => Assert.Equal(401, await InvokeAsync(ApiController.ImportRoute, "GET", ImportKey, splitEnabled: true));

    // Existing deployments set only Auth:ApiKey; behaviour there must not change.
    [Fact]
    public async Task WithoutTheSplitConfiguredTheImportKeyIsJustAnUnknownToken()
        => Assert.Equal(401, await InvokeAsync(ApiController.ImportRoute, "POST", ImportKey, splitEnabled: false));

    [Fact]
    public async Task WithoutTheSplitConfiguredTheOperatorKeyStillOpensEverything()
    {
        Assert.Equal(200, await InvokeAsync(ApiController.ImportRoute, "POST", OperatorKey, splitEnabled: false));
        Assert.Equal(200, await InvokeAsync("/api/jobs", "GET", OperatorKey, splitEnabled: false));
    }

    [Fact]
    public async Task UnknownTokenIsRejected()
        => Assert.Equal(401, await InvokeAsync("/api/jobs", "GET", "not-a-real-key-at-all", splitEnabled: true));

    [Fact]
    public async Task MissingTokenIsRejected()
        => Assert.Equal(401, await InvokeAsync("/api/jobs", "GET", null, splitEnabled: true));

    [Fact]
    public async Task NonApiPathsAreNotGuarded()
        => Assert.Equal(200, await InvokeAsync("/", "GET", null, splitEnabled: true));

    [Fact]
    public async Task HealthEndpointIsNotUnderTheApiPrefix()
        => Assert.Equal(200, await InvokeAsync("/health", "GET", null, splitEnabled: true));
}

public sealed class SecurityHeadersTests
{
    [Fact]
    public async Task HeadersArePresentOnEveryResponse()
    {
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();

        await middleware.Invoke(context);

        var headers = context.Response.Headers;
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);

        var csp = headers["Content-Security-Policy"].ToString();
        // Blocks script on our page from shipping data to an external host.
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        // The dashboard is a single inline file with no build step, so inline
        // script and style must remain permitted or the page stops working.
        Assert.Contains("'unsafe-inline'", csp);
    }
}
