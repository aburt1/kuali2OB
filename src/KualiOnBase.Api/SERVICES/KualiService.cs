using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Services;

// Kuali integration boundary. The public client methods are the only things the
// import workflow needs to know about Kuali; GraphQL strings and callback polling
// stay tucked in this file so the workflow reads like business logic.
public interface IKualiClient
{
    Task<KualiDocument> GetDocumentAsync(string documentId, CancellationToken ct);

    // `exportOptions` passes through to Kuali's `options: [String!]!`.
    // Production callers send `["Combined"]`. The tenant setting "Include PDFs
    // uploaded through the form" is what actually decides whether attachments
    // get merged into the returned PDF — see README.
    Task<string> ExportPdfAsync(string documentId, IReadOnlyList<string> exportOptions, CancellationToken ct);

    Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct);

    Task ClearAttachmentsAsync(string documentId, IReadOnlyList<string> fieldPaths, CancellationToken ct);

    Task DeleteDocumentAsync(string documentId, CancellationToken ct);
}

public sealed class ExportCallbackStore
{
    private readonly Db _db;

    public ExportCallbackStore(Db db)
    {
        _db = db;
    }

    public async Task CreatePendingAsync(string correlationId, string documentId, CancellationToken ct)
    {
        using var conn = _db.Open();
        var now = DateTime.UtcNow;
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ExportCallbacks
                (CorrelationId, DocumentId, Status, SignedUrl, ErrorMessage, CreatedAt, UpdatedAt)
            VALUES (@CorrelationId, @DocumentId, 'Pending', NULL, NULL, @Now, @Now);
            """,
            new { CorrelationId = correlationId, DocumentId = documentId, Now = now },
            cancellationToken: ct));
    }

    public async Task<ExportCallbackRow?> GetAsync(string correlationId, CancellationToken ct)
    {
        using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<ExportCallbackRow>(new CommandDefinition(
            "SELECT * FROM ExportCallbacks WHERE CorrelationId = @Id;",
            new { Id = correlationId },
            cancellationToken: ct));
    }

    public async Task<bool> MarkCompletedAsync(string correlationId, string signedUrl, CancellationToken ct)
    {
        using var conn = _db.Open();
        // WHERE Status='Pending' is the one-shot guard. If an attacker races to
        // re-POST the callback after we (or they) already finalized it, affected==0
        // and we surface a 409 to the caller without touching the row. Without this
        // guard, any authenticated-by-HMAC caller can overwrite SignedUrl at will.
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ExportCallbacks
               SET Status = 'Completed', SignedUrl = @Url, UpdatedAt = @Now
             WHERE CorrelationId = @Id AND Status = 'Pending';
            """,
            new { Id = correlationId, Url = signedUrl, Now = DateTime.UtcNow },
            cancellationToken: ct));
        return affected == 1;
    }

    public async Task<bool> MarkFailedAsync(string correlationId, string error, CancellationToken ct)
    {
        using var conn = _db.Open();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ExportCallbacks
               SET Status = 'Failed', ErrorMessage = @Error, UpdatedAt = @Now
             WHERE CorrelationId = @Id AND Status = 'Pending';
            """,
            new { Id = correlationId, Error = error, Now = DateTime.UtcNow },
            cancellationToken: ct));
        return affected == 1;
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        using var conn = _db.Open();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ExportCallbacks WHERE CreatedAt < @Cutoff;",
            new { Cutoff = cutoffUtc },
            cancellationToken: ct));
    }
}

internal static class KualiGraphQl
{
    public const string GetDocument = """
        query GetDocument($id: ID!) {
          document(id: $id) {
            id
            meta
            data
          }
        }
        """;

    // exportDocument is callback-based: Kuali POSTs the signed PDF URL to callbackUrl
    // when rendering completes. Returns a job id string (not used further by us).
    // `options` is required ([String!]!) by the schema; an empty list means "defaults".
    public const string ExportDocument = """
        mutation ExportDocument(
          $id: ID!,
          $callbackUrl: String!,
          $options: [String!]!,
          $sendAsPost: Boolean!,
          $timeZone: String
        ) {
          exportDocument(
            id: $id,
            callbackUrl: $callbackUrl,
            options: $options,
            sendAsPost: $sendAsPost,
            timeZone: $timeZone
          )
        }
        """;

    // UpdateDocumentInput = { id: ID, data: JSON, comment: String }
    public const string UpdateDocument = """
        mutation UpdateDocument($args: UpdateDocumentInput!) {
          updateDocument(args: $args) { id }
        }
        """;

    public const string DeleteDocument = """
        mutation DeleteDocument($id: ID!) {
          deleteDocument(id: $id)
        }
        """;
}

public sealed class KualiClient : IKualiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Factory-named client for external signed URLs (S3/CDN). We must NOT
    // forward the Kuali Bearer token to third parties and we go through the
    // factory to avoid socket-exhaustion from `new HttpClient()`.
    public const string DownloadHttpClientName = "KualiDownload";

    private readonly HttpClient _http;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AppSettings.KualiSettings _options;
    private readonly ExportCallbackStore _callbacks;
    private readonly ILogger<KualiClient> _log;

    public KualiClient(
        HttpClient http,
        IHttpClientFactory httpFactory,
        IOptions<AppSettings> options,
        ExportCallbackStore callbacks,
        ILogger<KualiClient> log)
    {
        _http = http;
        _httpFactory = httpFactory;
        _options = options.Value.Kuali;
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

        var sig = SignCallback(correlationId, _options.CallbackSecret);
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
        // Two URL shapes land here: absolute external URLs (S3 signed, no Bearer)
        // and relative/same-host Kuali URLs (Bearer required).
        var (requestUri, useAuth) = ResolveDownloadUrl(url);

        var client = useAuth ? _http : _httpFactory.CreateClient(DownloadHttpClientName);
        using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destinationPath);
        await src.CopyToAsync(dst, ct);
    }

    // Callback URL HMAC signer. Inlined from the former KualiCallbackSigner
    // so there's one callsite here and one in ApiController.
    internal static string SignCallback(string correlationId, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(correlationId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal (Uri Uri, bool UseAuth) ResolveDownloadUrl(string url)
    {
        // Treat only real http(s) URLs as absolute. Root-relative values like
        // `/files/123` parse as file:// when forced through UriKind.Absolute, but
        // they are still valid same-origin Kuali paths and should keep the Bearer.
        Uri? absolute = null;
        var isHttpAbsolute =
            Uri.TryCreate(url, UriKind.Absolute, out absolute)
            && absolute is not null
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);

        if (!isHttpAbsolute)
        {
            if (!Uri.TryCreate(url, UriKind.Relative, out _))
            {
                throw new InvalidOperationException(
                    $"Unsupported download URL scheme for '{url}'.");
            }
            if (_http.BaseAddress is null)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve relative Kuali URL '{url}' — Kuali:BaseUrl is not configured.");
            }
            return (new Uri(_http.BaseAddress, url.TrimStart('/')), UseAuth: true);
        }

        var sameAuthority = _http.BaseAddress is { } baseUri
            && baseUri.Scheme == Uri.UriSchemeHttps
            && absolute!.Scheme == Uri.UriSchemeHttps
            && string.Equals(absolute.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            && absolute.Port == baseUri.Port;

        return (absolute!, UseAuth: sameAuthority);
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
        catch (KualiApiException ex) when (
            ex.Message.Contains("required", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("field", StringComparison.OrdinalIgnoreCase))
        {
            // Kuali rejects null-ing out a required attachment field unless the form
            // has "Ignore required field validation on save" enabled.
            throw new KualiApiException(
                $"Kuali rejected clearing attachment fields on document {documentId}: {ex.Message}. " +
                "Enable \"Ignore required field validation on save\" on the Kuali form (Form → Settings) " +
                "so the deleteAttachments step can null out required attachment fields.",
                ex);
        }
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
            var message = string.Join("; ",
                errors.Select(e => e?["message"]?.GetValue<string>() ?? "unknown error"));
            throw new KualiApiException($"Kuali GraphQL errors: {message}");
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
}

// Posts terminal job status back to the X-Response-URL Kuali handed us on the
// initial integration request. Kuali advances the paused workflow step on any
// 2xx from us; we set X-Status-Code so the workflow can branch on success/fail.
// The X-Response-URL embeds a one-time token, so no Authorization header is sent.
public sealed class KualiResponseUrlNotifier
{
    public const string HttpClientName = "KualiResponseUrl";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<KualiResponseUrlNotifier> _log;

    public KualiResponseUrlNotifier(IHttpClientFactory httpFactory, ILogger<KualiResponseUrlNotifier> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<bool> NotifyAsync(ImportJob job, bool succeeded, string? error, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ResponseUrl)) return false;

        var producedFiles = string.IsNullOrWhiteSpace(job.ProducedFiles)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(job.ProducedFiles!, JsonOptions) ?? Array.Empty<string>();

        var payload = succeeded
            ? (object)new
                {
                    jobId = job.Id,
                    status = JobStatus.Succeeded,
                    documentId = job.DocumentId,
                    producedFiles,
                    backupFolder = job.BackupFolderPath,
                }
            : new
                {
                    jobId = job.Id,
                    status = JobStatus.Failed,
                    documentId = job.DocumentId,
                    error = error ?? job.LastError ?? "unknown",
                };

        using var client = _httpFactory.CreateClient(HttpClientName);
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, job.ResponseUrl) { Content = content };
        // Kuali reads X-Status-Code to surface the actual outcome of the
        // integration step; anything in 2xx-range advances the workflow.
        req.Headers.TryAddWithoutValidation("X-Status-Code", succeeded ? "200" : "500");

        try
        {
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "Kuali X-Response-URL POST for job {JobId} returned {Status}",
                    job.Id, (int)resp.StatusCode);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Kuali X-Response-URL POST for job {JobId} failed; will not stamp KualiNotifiedAt",
                job.Id);
            return false;
        }
    }
}
