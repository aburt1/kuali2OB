using KualiOnBase.Api.Options;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Auth;

public sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-Api-Key";
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

        var provided = ExtractToken(context.Request);
        if (provided is null || !CryptographicEquals(provided, _expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(
                "Missing or invalid credentials. Send the API key as either 'X-Api-Key: <key>' or 'Authorization: Bearer <key>'.");
            return;
        }

        await _next(context);
    }

    private static string? ExtractToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out var apiKeyHeader))
        {
            var v = apiKeyHeader.ToString();
            if (!string.IsNullOrEmpty(v)) return v;
        }

        if (request.Headers.TryGetValue("Authorization", out var auth))
        {
            var v = auth.ToString();
            if (v.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return v.Substring(BearerPrefix.Length).Trim();
            }
        }

        return null;
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
