using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KualiOnBase.Api.Models;

public sealed record ImportRequest
{
    [FromQuery(Name = "documentId")]
    public string DocumentId { get; init; } = string.Empty;

    [FromQuery(Name = "onbaseDocType")]
    public string OnBaseDocType { get; init; } = string.Empty;

    [FromQuery(Name = "targetFolderPath")]
    public string TargetFolderPath { get; init; } = string.Empty;

    [FromQuery(Name = "downloadMode")]
    public string DownloadMode { get; init; } = string.Empty;

    [FromQuery(Name = "deleteAttachments")]
    public bool DeleteAttachments { get; init; }

    [FromQuery(Name = "deleteDocument")]
    public bool DeleteDocument { get; init; }
}

public enum DownloadMode
{
    Pdf,
    Attachments,
    All,
}

public static class DownloadModes
{
    public static bool TryParse(string value, out DownloadMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "pdf": mode = DownloadMode.Pdf; return true;
            case "attachments": mode = DownloadMode.Attachments; return true;
            case "all": mode = DownloadMode.All; return true;
            default: mode = default; return false;
        }
    }
}

// Maps 1:1 to values Kuali's exportDocument mutation accepts in its
// `options` array. Passing null / empty on the wire leaves the array empty
// and Kuali uses its tenant default (typically Combined).
public static class PdfExportOptions
{
    public const string Form = "Form";
    public const string Combined = "Combined";
    public const string Attachments = "Attachments";

    public static bool TryNormalize(string? value, out string? canonical)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            canonical = null;
            return true;
        }
        switch (value.Trim().ToLowerInvariant())
        {
            case "form": canonical = Form; return true;
            case "combined": canonical = Combined; return true;
            case "attachments": canonical = Attachments; return true;
            default: canonical = null; return false;
        }
    }
}
