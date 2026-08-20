using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace KualiOnBase.Api.Services;

/// <summary>
/// Shared rules for every URL this service will make an outbound request to.
/// Two sinks use it: Kuali attachment/export downloads, and the POST back to the
/// caller-supplied X-Response-URL. Both take their URL from data the caller or the
/// form submitter controls, so both get the same guard — a request this server
/// makes on someone else's behalf can otherwise reach anything the host can reach.
/// </summary>
public static class OutboundUrl
{
    /// <summary>Returns a reason the URL must not be requested, or null if it is acceptable.</summary>
    public static string? DescribeProblem(string? url, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(url)) return $"{parameterName} is empty.";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return $"{parameterName} is not an absolute URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"{parameterName} must use https (got '{uri.Scheme}').";
        }

        if (IsNonRoutableHost(uri))
        {
            return $"{parameterName} points at {uri.Host}, which is a loopback, link-local or " +
                   "private address; outbound calls must target public hosts.";
        }

        return null;
    }

    /// <summary>
    /// Literal-IP check only. A hostname that RESOLVES to an internal address is not
    /// caught here — blocking that reliably needs resolve-then-pin at socket level,
    /// which is a bigger change than this guard is worth.
    /// </summary>
    public static bool IsNonRoutableHost(Uri uri)
    {
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return true;                              // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;              // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;              // link-local / cloud metadata
            return false;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
            || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;              // fc00::/7 unique-local
    }
}

/// <summary>
/// Thrown for failures that can never succeed on a retry — bad parameters,
/// disallowed target paths. RetryWorker marks these Failed immediately instead
/// of burning the full backoff schedule on a job that is permanently broken.
/// </summary>
public sealed class PermanentImportException : Exception
{
    public PermanentImportException(string message) : base(message) { }

    public PermanentImportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Validates everything a Kuali HTTP Action sends before a job is queued, so an
/// operator sees every problem at once in Kuali's "response data" dialog rather
/// than discovering them one retry at a time.
/// </summary>
public static class ImportRequestValidator
{
    // Kuali Build document ids are 24-character hex ObjectIds.
    private static readonly Regex DocumentIdPattern = new(@"\A[0-9a-fA-F]{24}\z", RegexOptions.Compiled);

    public const int MaxKeywordSlots = 20;

    // IndexFileBuilder emits each keyword pair as "<key>: <value>" on its own line,
    // the same shape as the directives that tell OnBase how to file the document.
    // An unchecked key can therefore forge a second directive and change the doc
    // type or source of a record — no line break required.
    private static readonly HashSet<string> ReservedIndexDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "ONBASE_DOC_TYPE",
        "FILENAME",
        "EXTERNAL_SOURCE",
        "EXTERNAL_SOURCE_REF",
    };

    private static readonly HashSet<string> KnownParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "documentId",
        "onbaseDocType",
        "targetFolderPath",
        "downloadMode",
        "deleteAttachments",
        "deleteDocument",
    };

    public sealed class Result
    {
        public List<string> Errors { get; } = new();
        public bool DeleteAttachments { get; set; }
        public bool DeleteDocument { get; set; }
        public bool IsValid => Errors.Count == 0;
    }

    /// <summary>
    /// A Kuali HTTP Action URL that still contains {{...}} means the token never
    /// resolved — the field name is wrong, or the step runs before the value exists.
    /// Catching it here beats writing "{{data.department}}" into an OnBase index file.
    /// </summary>
    public static bool LooksLikeUnsubstitutedToken(string value) =>
        value.Contains("{{", StringComparison.Ordinal) || value.Contains("}}", StringComparison.Ordinal);

    /// <summary>
    /// Accepts the spellings Kuali's URL editor realistically produces. Anything
    /// else is reported rather than silently coerced to false.
    /// </summary>
    public static bool TryParseBoolean(string raw, out bool value)
    {
        var trimmed = raw.Trim();
        if (bool.TryParse(trimmed, out value)) return true;
        if (trimmed == "1") { value = true; return true; }
        if (trimmed == "0") { value = false; return true; }
        value = false;
        return false;
    }

    /// <summary>
    /// Returns a human-readable problem with <paramref name="requested"/>, or null
    /// when it sits under one of <paramref name="allowedRoots"/>. Shared with
    /// ImportService so the endpoint and the job runner cannot disagree.
    /// </summary>
    public static string? DescribeTargetPathProblem(string? requested, IReadOnlyList<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(requested)) return "targetFolderPath is required.";

        if (allowedRoots.Count == 0)
        {
            return "Import:AllowedTargetRoots is not configured on the server; " +
                   "refusing to write to any targetFolderPath.";
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(requested);
        }
        catch (Exception ex)
        {
            return $"targetFolderPath '{Show(requested)}' is not a valid path: {ex.Message}";
        }

        // OnBase SMB shares are case-insensitive on Windows hosts.
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var root in allowedRoots)
        {
            // Trailing separator on both sides so `/allowed` doesn't accept `/allowed-sibling`.
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedPath = canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (normalizedPath.StartsWith(normalizedRoot, cmp)) return null;
        }

        // Name the configured roots: without them the operator has to go read the
        // server's environment variables to work out what would have been accepted.
        return $"targetFolderPath '{Show(requested)}' is not under any configured " +
               $"Import:AllowedTargetRoots. Allowed: {string.Join("; ", allowedRoots)}";
    }

    public static Result Validate(IQueryCollection query, IReadOnlyList<string> allowedRoots)
    {
        var result = new Result();
        var errors = result.Errors;

        ValidateRequiredText(query, "documentId", errors, out var documentId);
        if (documentId is not null && !DocumentIdPattern.IsMatch(documentId))
        {
            errors.Add($"documentId '{Show(documentId)}' is not a valid Kuali document id " +
                       "(expected 24 hexadecimal characters).");
        }

        ValidateRequiredText(query, "onbaseDocType", errors, out _);

        if (ValidateRequiredText(query, "targetFolderPath", errors, out var targetFolderPath)
            && targetFolderPath is not null)
        {
            var problem = DescribeTargetPathProblem(targetFolderPath, allowedRoots);
            if (problem is not null) errors.Add(problem);
        }

        var downloadMode = query["downloadMode"].ToString();
        if (string.IsNullOrWhiteSpace(downloadMode))
        {
            errors.Add("downloadMode is required.");
        }
        else if (downloadMode != "pdf" && downloadMode != "attachments")
        {
            errors.Add($"downloadMode must be one of: pdf, attachments (got '{Show(downloadMode)}').");
        }

        result.DeleteAttachments = ValidateBoolean(query, "deleteAttachments", required: true, errors);
        result.DeleteDocument = ValidateBoolean(query, "deleteDocument", required: false, errors);

        ValidateKeywordSlots(query, errors);
        ValidateNoUnknownParameters(query, errors);

        return result;
    }


    // Values echoed back into an error message end up in two places: the HTTP 400
    // body and the Serilog file. A raw CR/LF in either forges a log line, so every
    // untrusted value is flattened and length-capped before it is quoted.
    public static string Show(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var flattened = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return flattened.Length <= 120 ? flattened : flattened[..120] + "…";
    }

    private static bool ValidateRequiredText(
        IQueryCollection query, string name, List<string> errors, out string? value)
    {
        value = query[name].ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required.");
            value = null;
            return false;
        }

        if (LooksLikeUnsubstitutedToken(value))
        {
            errors.Add($"{name} still contains an unsubstituted Kuali template token ('{Show(value)}') — " +
                       "check the field name in the HTTP Action URL.");
            value = null;
            return false;
        }

        return true;
    }

    private static bool ValidateBoolean(
        IQueryCollection query, string name, bool required, List<string> errors)
    {
        var raw = query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (required) errors.Add($"{name} is required.");
            return false;
        }

        if (!TryParseBoolean(raw, out var parsed))
        {
            errors.Add($"{name} must be true or false (got '{Show(raw)}').");
            return false;
        }

        return parsed;
    }

    /// <summary>
    /// A key with no value (or the reverse) used to be dropped silently, which is
    /// how an OnBase index file quietly loses a keyword the operator thought they
    /// had configured.
    /// </summary>
    private static void ValidateKeywordSlots(IQueryCollection query, List<string> errors)
    {
        for (var i = 1; i <= MaxKeywordSlots; i++)
        {
            var keyName = $"KeywordKey{i}";
            var valueName = $"KeywordValue{i}";
            var key = query[keyName].ToString();
            var value = query[valueName].ToString();

            var keySet = !IsIgnored(key);
            var valueSet = !IsIgnored(value);

            if (keySet && !valueSet)
            {
                errors.Add($"{keyName} was provided ('{Show(key)}') without a matching {valueName}.");
            }
            else if (!keySet && valueSet)
            {
                errors.Add($"{valueName} was provided ('{Show(value)}') without a matching {keyName}.");
            }

            if (keySet && ReservedIndexDirectives.Contains(key.Trim()))
            {
                errors.Add($"{keyName} '{Show(key)}' is a reserved OnBase index directive " +
                           $"({string.Join(", ", ReservedIndexDirectives)}) and would change how " +
                           "the document is filed. Choose a different keyword name.");
            }

            if (keySet && key.Contains(':', StringComparison.Ordinal))
            {
                errors.Add($"{keyName} '{Show(key)}' contains ':', which separates a directive " +
                           "name from its value in the OnBase index file. Remove the colon.");
            }

            if (keySet && LooksLikeUnsubstitutedToken(key))
            {
                errors.Add($"{keyName} still contains an unsubstituted Kuali template token ('{Show(key)}').");
            }
            if (valueSet && LooksLikeUnsubstitutedToken(value))
            {
                errors.Add($"{valueName} still contains an unsubstituted Kuali template token ('{Show(value)}').");
            }
        }
    }

    /// <summary>
    /// A misspelled parameter used to be ignored in silence, so the request looked
    /// accepted while the intended setting never applied.
    /// </summary>
    private static void ValidateNoUnknownParameters(IQueryCollection query, List<string> errors)
    {
        foreach (var key in query.Keys)
        {
            if (KnownParameters.Contains(key)) continue;
            if (IsKeywordSlotName(key)) continue;
            errors.Add($"'{Show(key)}' is not a recognized parameter. Expected one of: " +
                       $"{string.Join(", ", KnownParameters)}, KeywordKey1-{MaxKeywordSlots}, " +
                       $"KeywordValue1-{MaxKeywordSlots}.");
        }
    }

    private static bool IsKeywordSlotName(string key)
    {
        foreach (var prefix in new[] { "KeywordKey", "KeywordValue" })
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = key[prefix.Length..];
            if (int.TryParse(suffix, out var slot) && slot >= 1 && slot <= MaxKeywordSlots) return true;
        }
        return false;
    }

    // Kuali Build's HTTP-Action URL editor forces a value into every token, so
    // "unused" keyword slots get filled with a literal "|" by convention.
    private static bool IsIgnored(string s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim() == "|";
}
