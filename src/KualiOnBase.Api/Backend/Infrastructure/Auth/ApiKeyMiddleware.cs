using KualiOnBase.Api.Configuration;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Infrastructure.Auth;

public sealed class ApiKeyMiddleware
{
    public const string BearerPrefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly string _expected;

    public ApiKeyMiddleware(RequestDelegate next, IOptions<AuthOptions> options)
    {
        _next = next;
        _expected = options.Value.ApiKey ?? string.Empty;
    }

    public async Task Invoke(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrEmpty(_expected))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Server API key not configured.");
            return;
        }

        var provided = ExtractBearerToken(context.Request);
        if (provided is null || !CryptographicEquals(provided, _expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(
                "Missing or invalid credentials. Send the API key as 'Authorization: Bearer <key>'.");
            return;
        }

        await _next(context);
    }

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
