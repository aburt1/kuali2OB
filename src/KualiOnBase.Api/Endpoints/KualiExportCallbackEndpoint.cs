using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services.Kuali;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Endpoints;

// Kuali POSTs here when exportDocument finishes. This route lives OUTSIDE /api
// on purpose so ApiKeyMiddleware does not block Kuali (Kuali has no API key).
// The correlation id is signed with an HMAC so only our own issued URLs are accepted.
public static class KualiExportCallbackEndpoint
{
    public const string Route = "/kuali-export-callback/{correlationId}";

    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app)
    {
        return app.MapPost(Route, HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        string correlationId,
        HttpRequest request,
        IExportCallbackStore store,
        IOptions<KualiOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("KualiExportCallback");
        var opts = options.Value;

        if (string.IsNullOrWhiteSpace(opts.CallbackSecret))
        {
            log.LogError("Callback received but Kuali:CallbackSecret is not configured.");
            return Results.Problem("Callback secret not configured.", statusCode: 500);
        }

        var sig = request.Query["sig"].ToString();
        var expected = KualiCallbackSigner.Sign(correlationId, opts.CallbackSecret);
        if (string.IsNullOrEmpty(sig)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(sig),
                Encoding.ASCII.GetBytes(expected)))
        {
            log.LogWarning("Callback for {CorrelationId} rejected due to invalid signature.", correlationId);
            return Results.Unauthorized();
        }

        var row = await store.GetAsync(correlationId, ct);
        if (row is null)
        {
            log.LogWarning("Callback for unknown correlation id {CorrelationId}.", correlationId);
            return Results.NotFound();
        }

        var body = await ReadBodyAsync(request, ct);
        var url = ExtractUrl(body);
        var error = ExtractError(body);

        if (!string.IsNullOrEmpty(error))
        {
            await store.MarkFailedAsync(correlationId, error!, ct);
            log.LogWarning("Kuali export failed for {DocumentId}: {Error}", row.DocumentId, error);
            return Results.Ok(new { status = "recorded" });
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            await store.MarkFailedAsync(correlationId, "Callback payload did not contain a URL.", ct);
            log.LogWarning("Callback for {DocumentId} had no URL. Body: {Body}", row.DocumentId, body);
            return Results.BadRequest(new { error = "Missing URL in callback payload." });
        }

        await store.MarkCompletedAsync(correlationId, url!, ct);
        log.LogInformation("Recorded Kuali export callback for {DocumentId}.", row.DocumentId);
        return Results.Ok(new { status = "recorded" });
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        request.Body.Position = 0;
        return text;
    }

    // Kuali's callback body shape is not strongly documented, so accept several forms:
    //   - JSON: { "url": "..." } / { "signedUrl": "..." } / { "downloadUrl": "..." } / { "pdfUrl": "..." }
    //   - raw text body that IS the URL
    //   - ?url=... query string fallback
    internal static string? ExtractUrl(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "url", "signedUrl", "downloadUrl", "pdfUrl", "href" })
                {
                    var value = obj[key]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through - maybe the body IS a bare URL
        }

        var trimmed = body.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return null;
    }

    internal static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "error", "errorMessage", "message" })
                {
                    var value = obj[key]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // ignore
        }
        return null;
    }
}

public static class KualiCallbackSigner
{
    public static string Sign(string correlationId, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(correlationId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
