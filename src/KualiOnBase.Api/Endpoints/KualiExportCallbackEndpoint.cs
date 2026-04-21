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
//
// Threat model: anyone on the public internet can hit this endpoint. The HMAC
// gates auth. Additional layers below:
//   - body size cap     (attacker can't buffer-bomb this unauthenticated path)
//   - URL scheme check  (blocks relative/file:// payloads that would become SSRF
//                        through Kuali with our Bearer token forwarded)
//   - one-shot status   (store's UPDATE ... WHERE Status='Pending' prevents a
//                        late attacker from overwriting an already-finalized row)
public static class KualiExportCallbackEndpoint
{
    public const string Route = "/kuali-export-callback/{correlationId}";

    // Kuali's real callback body is a tiny JSON payload (URL or error). Cap at 64 KB
    // to keep hostile bodies cheap to reject.
    private const int MaxBodyBytes = 64 * 1024;

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
        // Length gate before FixedTimeEquals: that API throws on length mismatch,
        // which (pre-fix) bubbled through the global exception handler as a 500 and
        // also leaked "short sig vs wrong sig" timing distinguishability. Fail
        // uniformly with 401 either way.
        if (string.IsNullOrEmpty(sig)
            || sig.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(sig),
                Encoding.ASCII.GetBytes(expected)))
        {
            log.LogWarning("Callback for {CorrelationId} rejected due to invalid signature.", correlationId);
            return Results.Unauthorized();
        }

        // Body-size check BEFORE reading so an attacker can't stream gigabytes
        // of garbage into Kestrel's buffer. Content-Length is a strong hint;
        // if absent or wrong we enforce the cap during the read as well.
        if (request.ContentLength is long cl && cl > MaxBodyBytes)
        {
            log.LogWarning("Callback for {CorrelationId} rejected — body size {Size} exceeds cap.",
                correlationId, cl);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var row = await store.GetAsync(correlationId, ct);
        if (row is null)
        {
            log.LogWarning("Callback for unknown correlation id {CorrelationId}.", correlationId);
            return Results.NotFound();
        }

        string body;
        try
        {
            body = await ReadBodyAsync(request, ct);
        }
        catch (InvalidOperationException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var url = ExtractUrl(body);
        var error = ExtractError(body);

        if (!string.IsNullOrEmpty(error))
        {
            var transitioned = await store.MarkFailedAsync(correlationId, error!, ct);
            if (!transitioned)
            {
                log.LogWarning("Duplicate/late failure callback for {CorrelationId}; row already finalized.",
                    correlationId);
                return Results.Conflict(new { status = "already-finalized" });
            }
            log.LogWarning("Kuali export failed for {DocumentId}: {Error}", row.DocumentId, error);
            return Results.Ok(new { status = "recorded" });
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            await store.MarkFailedAsync(correlationId, "Callback payload did not contain a URL.", ct);
            log.LogWarning("Callback for {DocumentId} had no URL.", row.DocumentId);
            return Results.BadRequest(new { error = "Missing URL in callback payload." });
        }

        // http/https only. A relative path like "/etc/passwd" would otherwise be
        // accepted here, stored as the "signed URL", and later combined with
        // Kuali:BaseUrl by ResolveDownloadUrl — turning this into an authenticated
        // SSRF against the Kuali tenant with our Bearer token attached.
        if (!IsHttpUrl(url!))
        {
            await store.MarkFailedAsync(correlationId,
                "Callback payload URL must use http or https scheme.", ct);
            log.LogWarning("Callback for {DocumentId} had non-http URL; rejected.", row.DocumentId);
            return Results.BadRequest(new { error = "Callback URL must be http(s)." });
        }

        var completed = await store.MarkCompletedAsync(correlationId, url!, ct);
        if (!completed)
        {
            // Either Kuali retried and won the race, or an attacker who got our
            // HMAC is trying to overwrite a finalized row. Either way we refuse
            // without echoing which it was.
            log.LogWarning("Duplicate/late callback for {CorrelationId}; row already finalized.",
                correlationId);
            return Results.Conflict(new { status = "already-finalized" });
        }

        log.LogInformation("Recorded Kuali export callback for {DocumentId}.", row.DocumentId);
        return Results.Ok(new { status = "recorded" });
    }

    // Enforce MaxBodyBytes even when Content-Length is missing or lies. Reads into
    // a capped buffer and throws InvalidOperationException if the stream continues
    // beyond the cap. We do NOT rewind Body.Position — callers don't re-read.
    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        if (total > MaxBodyBytes)
        {
            throw new InvalidOperationException("Callback body exceeds cap.");
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u)
        && (u!.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

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
