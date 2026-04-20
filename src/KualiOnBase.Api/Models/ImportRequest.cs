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
