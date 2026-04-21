using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KualiOnBase.Api.Endpoints;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Options;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Services.Kuali;

public sealed class KualiClient : IKualiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Idle-chunk timeout for DownloadToFileAsync: if the source stalls for this
    // long mid-body, we abort. Prevents a dead Kuali/S3 peer from pinning a
    // socket and a worker task forever while the outer HttpClient timeout
    // (which only covers headers under ResponseHeadersRead) doesn't fire.
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromMinutes(2);

    // IHttpClientFactory name for the no-auth download client. Used when the
    // signed URL is an external (S3/CDN) URL that carries its own auth — we
    // must NOT forward the Kuali Bearer token to third parties, and we must
    // go through the factory so sockets aren't leaked on `new HttpClient()`.
    public const string DownloadHttpClientName = "KualiDownload";

    private readonly HttpClient _http;
    private readonly IHttpClientFactory _httpFactory;
    private readonly KualiOptions _options;
    private readonly IExportCallbackStore _callbacks;
    private readonly ILogger<KualiClient> _log;

    public KualiClient(
        HttpClient http,
        IHttpClientFactory httpFactory,
        IOptions<KualiOptions> options,
        IExportCallbackStore callbacks,
        ILogger<KualiClient> log)
    {
        _http = http;
        _httpFactory = httpFactory;
        _options = options.Value;
        _callbacks = callbacks;
        _log = log;

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }
    }

    public async Task<KualiDocument> GetDocumentAsync(string documentId, CancellationToken ct)
    {
        var data = await ExecuteAsync(KualiGraphQl.GetDocument, new { id = documentId }, ct);
        var node = data["document"]
            ?? throw new KualiApiException($"Document '{documentId}' not found.");

        var id = node["id"]?.GetValue<string>() ?? documentId;

        // meta and data are JSON scalars — they come back as nested objects/arrays directly.
        var meta = node["meta"] as JsonObject;
        var payload = node["data"] as JsonObject;

        var serial = ReadString(meta, "serialNumber") ?? string.Empty;

        var firstName = ReadString(payload, "firstName") ?? string.Empty;
        var lastName = ReadString(payload, "lastName") ?? string.Empty;
        var schoolId = ReadString(payload, "schoolId")
            ?? ReadString(payload, "schoolID")
            ?? ReadString(payload, "studentId")
            ?? string.Empty;

        var attachments = ExtractAttachments(payload);

        // Keep a snapshot of the raw data tree so the import timeline can surface
        // it when attachment detection misses — different tenants shape file-upload
        // fields differently and we want a diagnostic we can look at after the fact.
        var rawDataJson = payload?.ToJsonString();

        return new KualiDocument(id, serial, firstName, lastName, schoolId, attachments, rawDataJson);
    }

    public async Task<string> ExportPdfAsync(string documentId, IReadOnlyList<string> exportOptions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
        {
            throw new InvalidOperationException(
                "Kuali:PublicBaseUrl must be configured to use PDF export " +
                "(exportDocument is callback-based).");
        }
        if (string.IsNullOrWhiteSpace(_options.CallbackSecret))
        {
            throw new InvalidOperationException(
                "Kuali:CallbackSecret must be configured to use PDF export.");
        }

        var correlationId = Guid.NewGuid().ToString("N");
        await _callbacks.CreatePendingAsync(correlationId, documentId, ct);

        var sig = KualiCallbackSigner.Sign(correlationId, _options.CallbackSecret);
        var callbackUrl =
            $"{_options.PublicBaseUrl.TrimEnd('/')}/kuali-export-callback/{correlationId}?sig={sig}";

        await ExecuteAsync(
            KualiGraphQl.ExportDocument,
            new
            {
                id = documentId,
                callbackUrl,
                options = exportOptions,
                sendAsPost = true,
                timeZone = _options.ExportTimeZone,
            },
            ct);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.ExportCallbackTimeoutSeconds));
        var pollDelay = TimeSpan.FromMilliseconds(Math.Max(100, _options.ExportCallbackPollMilliseconds));
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var row = await _callbacks.GetAsync(correlationId, ct);
            if (row is null)
            {
                throw new KualiApiException($"Export callback row {correlationId} disappeared.");
            }
            if (row.Status == "Completed" && !string.IsNullOrEmpty(row.SignedUrl))
            {
                return row.SignedUrl!;
            }
            if (row.Status == "Failed")
            {
                throw new KualiApiException(
                    $"Kuali export for document {documentId} failed: {row.ErrorMessage ?? "unknown"}");
            }
            await Task.Delay(pollDelay, ct);
        }

        throw new KualiApiException(
            $"Timed out waiting {timeout.TotalSeconds:0}s for Kuali export callback for document {documentId}.");
    }

    public async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        // Two URL shapes land here:
        //   1. Absolute external URLs (e.g. S3 signed URLs returned from exportDocument).
        //      These carry their own auth — we must NOT forward our Bearer token.
        //      Pulled through IHttpClientFactory (NOT `new HttpClient()`) to avoid
        //      socket exhaustion under load.
        //   2. Relative or same-host Kuali URLs (e.g. attachment permaLink / temporaryUrl).
        //      These live behind Kuali auth and need our Bearer token.
        var (requestUri, useAuth) = ResolveDownloadUrl(url);

        HttpClient client;
        bool disposeClient = false; // factory-owned clients are pooled; we don't dispose.
        HttpResponseMessage response;
        if (useAuth)
        {
            client = _http;
            using var req = new HttpRequestMessage(HttpMethod.Get, requestUri);
            response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        else
        {
            client = _httpFactory.CreateClient(DownloadHttpClientName);
            response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        try
        {
            response.EnsureSuccessStatusCode();

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destinationPath);
            await CopyWithIdleTimeoutAsync(src, dst, DownloadIdleTimeout, ct);
        }
        finally
        {
            response.Dispose();
            if (disposeClient) client.Dispose();
        }
    }

    // Streaming copy with a per-chunk idle timeout: if the source goes quiet
    // mid-body for longer than `idle`, abort. HttpClient.Timeout under
    // ResponseHeadersRead only covers the header phase, so without this guard
    // a stalled-forever download would pin a worker task indefinitely.
    private static async Task CopyWithIdleTimeoutAsync(
        Stream src, Stream dst, TimeSpan idle, CancellationToken ct)
    {
        var buffer = new byte[81_920];
        while (true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(idle);
            int read;
            try
            {
                read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException(
                    $"Download stalled — no bytes received for {idle.TotalSeconds:0}s.");
            }
            if (read <= 0) return;
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    internal (Uri Uri, bool UseAuth) ResolveDownloadUrl(string url)
    {
        // Only treat http(s) as truly absolute. On Unix, Uri.TryCreate happily parses
        // a leading-slash path like "/app/forms/..." as a file:// URI — which is why
        // we can't just ask IsAbsoluteUri. Anything else is a Kuali-relative path.
        var isHttp =
            Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && (absolute!.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);

        if (!isHttp)
        {
            if (_http.BaseAddress is null)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve relative Kuali URL '{url}' — Kuali:BaseUrl is not configured.");
            }
            return (new Uri(_http.BaseAddress, url.TrimStart('/')), UseAuth: true);
        }

        var sameHost = _http.BaseAddress is { } baseUri
            && string.Equals(absolute!.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);
        return (absolute!, UseAuth: sameHost);
    }

    public async Task ClearAttachmentsAsync(
        string documentId,
        IReadOnlyList<string> fieldPaths,
        CancellationToken ct)
    {
        if (fieldPaths.Count == 0)
        {
            return;
        }

        var updates = new JsonObject();
        foreach (var path in fieldPaths.Distinct(StringComparer.Ordinal))
        {
            updates[path] = null;
        }

        try
        {
            await ExecuteAsync(
                KualiGraphQl.UpdateDocument,
                new
                {
                    args = new
                    {
                        id = documentId,
                        data = updates,
                        comment = "Attachments cleared by KualiOnBase integration after successful OnBase import.",
                    },
                },
                ct);
        }
        catch (KualiApiException ex) when (ex.IsRequiredFieldValidation)
        {
            // Kuali rejects null-ing out a required attachment field unless the form
            // has "Ignore required field validation on save" enabled. Surface a
            // pointed hint so operators don't have to puzzle over a generic error.
            throw new KualiApiException(
                $"Kuali rejected clearing attachment fields on document {documentId}: {ex.Message}. " +
                "Enable \"Ignore required field validation on save\" on the Kuali form (Form → Settings) " +
                "so the deleteAttachments step can null out required attachment fields.",
                ex);
        }
    }

    // Tightened detection (H7): previously we substring-matched "required" / "validation",
    // which would false-positive on any error message mentioning those words (e.g. "token
    // has expired, re-authentication required"). Now we look at Kuali's structured error
    // shape: GraphQL errors carry `extensions.code` — we check for that specifically,
    // and only fall back to a very narrow message-substring pattern for tenants whose
    // error payload doesn't surface extensions.
    internal static bool IsRequiredFieldValidationError(JsonArray? errors)
    {
        if (errors is null) return false;
        foreach (var err in errors)
        {
            if (err is not JsonObject obj) continue;

            // Preferred: structured error code from Kuali.
            var code = obj["extensions"]?["code"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(code))
            {
                if (string.Equals(code, "FIELD_VALIDATION_FAILED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(code, "REQUIRED_FIELD_MISSING", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(code, "VALIDATION_ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Fallback: a narrow phrase-match that requires BOTH "required" AND "field"
            // (so generic uses of either word in auth/network errors don't match).
            var msg = obj["message"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(msg)
                && msg.Contains("required", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("field", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken ct)
    {
        await ExecuteAsync(KualiGraphQl.DeleteDocument, new { id = documentId }, ct);
    }

    private async Task<JsonObject> ExecuteAsync(string query, object variables, CancellationToken ct)
    {
        var body = new { query, variables };
        using var response = await _http.PostAsJsonAsync("app/api/v0/graphql", body, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            _log.LogWarning("Kuali GraphQL call returned {Status}: {Body}", response.StatusCode, text);
            response.EnsureSuccessStatusCode();
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions, ct)
            ?? throw new KualiApiException("Kuali returned an empty response.");

        var errors = payload["errors"]?.AsArray();
        if (errors is { Count: > 0 })
        {
            var isRequired = IsRequiredFieldValidationError(errors);
            var message = string.Join("; ",
                errors.Select(e => e?["message"]?.GetValue<string>() ?? "unknown error"));
            throw new KualiApiException($"Kuali GraphQL errors: {message}")
            {
                IsRequiredFieldValidation = isRequired,
            };
        }

        return payload["data"]?.AsObject()
            ?? throw new KualiApiException("Kuali response missing 'data'.");
    }

    private static string? ReadString(JsonObject? obj, string key)
    {
        if (obj is null)
        {
            return null;
        }
        var value = obj[key];
        return value is null ? null : value.ToString();
    }

    internal static IReadOnlyList<KualiAttachment> ExtractAttachments(JsonObject? data)
    {
        var result = new List<KualiAttachment>();
        if (data is null)
        {
            return result;
        }

        foreach (var property in data)
        {
            Walk(property.Key, property.Value, result);
        }
        return result;
    }

    private static void Walk(string path, JsonNode? node, List<KualiAttachment> result)
    {
        switch (node)
        {
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    Walk($"{path}[{i}]", arr[i], result);
                }
                break;
            case JsonObject obj:
                if (TryReadAttachment(path, obj, out var attachment))
                {
                    result.Add(attachment);
                    return;
                }
                foreach (var child in obj)
                {
                    Walk($"{path}.{child.Key}", child.Value, result);
                }
                break;
        }
    }

    private static bool TryReadAttachment(string path, JsonObject obj, out KualiAttachment attachment)
    {
        var fileName =
            obj["filename"]?.GetValue<string>()
            ?? obj["fileName"]?.GetValue<string>()
            ?? obj["name"]?.GetValue<string>();
        // Kuali Build's file-upload field stores a `permaLink` (JWT-carrying,
        // doesn't expire) and a `temporaryUrl`. Prefer permaLink. We also accept
        // the generic url / downloadUrl / href keys for other possible shapes.
        var url =
            obj["permaLink"]?.GetValue<string>()
            ?? obj["temporaryUrl"]?.GetValue<string>()
            ?? obj["url"]?.GetValue<string>()
            ?? obj["downloadUrl"]?.GetValue<string>()
            ?? obj["href"]?.GetValue<string>();
        var id =
            obj["id"]?.GetValue<string>()
            ?? obj["retrievalId"]?.GetValue<string>()
            ?? path;

        if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(url))
        {
            attachment = new KualiAttachment(id, path, fileName, url);
            return true;
        }

        attachment = default!;
        return false;
    }
}

public sealed class KualiApiException : Exception
{
    public KualiApiException(string message) : base(message) { }
    public KualiApiException(string message, Exception inner) : base(message, inner) { }

    // Set by ExecuteAsync when the GraphQL error batch includes a structured
    // "required field validation" code (or a narrow phrase fallback). Used by
    // ClearAttachmentsAsync to attach the "Ignore required field validation
    // on save" operator hint without substring-matching arbitrary text.
    public bool IsRequiredFieldValidation { get; init; }
}
