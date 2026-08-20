using KualiOnBase.Api.Controllers;
using KualiOnBase.Api.Models;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Services;

public sealed class ApiKeyMiddleware
{
    public const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<AppSettings> _options;

    // IOptionsMonitor rather than IOptions: the middleware is a singleton, so
    // capturing the key in the constructor would pin it for the process lifetime
    // and any future secret provider that refreshes values could not rotate the
    // key without an app restart. Reading CurrentValue per request costs nothing
    // measurable and keeps rotation a configuration concern, not a code change.
    public ApiKeyMiddleware(RequestDelegate next, IOptionsMonitor<AppSettings> options)
    {
        _next = next;
        _options = options;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var auth = _options.CurrentValue.Auth;
        var operatorKey = auth.ApiKey ?? string.Empty;
        if (string.IsNullOrEmpty(operatorKey))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Server API key not configured.");
            return;
        }

        var provided = ExtractBearerToken(context.Request);
        if (provided is null)
        {
            await RejectAsync(context);
            return;
        }

        // The operator credential opens everything. The optional import credential
        // opens only the import endpoint, so the key that lives in Kuali's HTTP
        // Action configuration cannot read job payloads or download documents.
        if (CryptographicEquals(provided, operatorKey))
        {
            await _next(context);
            return;
        }

        var importKey = auth.ImportApiKey ?? string.Empty;
        if (!string.IsNullOrEmpty(importKey)
            && CryptographicEquals(provided, importKey)
            && IsImportRequest(context.Request))
        {
            await _next(context);
            return;
        }

        await RejectAsync(context);
    }

    // Deliberately the same response whether the token was unknown or was a valid
    // import key used on the wrong route: distinguishing them would confirm to a
    // caller that they hold a real credential.
    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync(
            "Missing or invalid credentials. Send the API key as 'Authorization: Bearer <key>'.");
    }

    internal static bool IsImportRequest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && request.Path.Equals(ApiController.ImportRoute, StringComparison.OrdinalIgnoreCase);

    // Bearer-only. We intentionally do not accept X-Api-Key — one auth path
    // means one piece of code to audit, and rate-limit partitioning downstream
    // can't disagree with middleware about which header is authoritative.
    internal static string? ExtractBearerToken(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var auth))
        {
            return null;
        }
        var v = auth.ToString();
        if (!v.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var token = v.Substring(BearerPrefix.Length).Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }
        return diff == 0;
    }
}

/// <summary>
/// Response headers for this application's own responses.
///
/// Scope note: the OnBase AppNet instance sharing this host serves its own
/// responses and is unaffected by these, so this does not mitigate script injected
/// into an AppNet page. What it does do is stop script running on *our* page from
/// shipping data to an external host, and prevent the dashboard being framed.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    // 'unsafe-inline' is required because the dashboard is a single self-contained
    // file with inline style and script and no build step to hash or nonce them.
    // The directives carrying real weight here are connect-src, object-src and base-uri.
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'none'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task Invoke(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        return _next(context);
    }
}
