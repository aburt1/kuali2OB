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

// form        → Kuali exportDocument(Form) → 1 PDF (form render only)
// combined    → Kuali exportDocument(Combined) → 1 PDF (form + attachments merged).
//               NOTE: Kuali can only merge PDF attachments. Non-PDF uploads on the
//               source document will make this mode fail — use `attachments` instead.
// attachments → raw attachment files downloaded directly, preserving original
//               formats (.docx/.jpg/.xlsx/…). N files out (one per attachment).
public enum DownloadMode
{
    Form,
    Combined,
    Attachments,
}

public static class DownloadModes
{
    public static bool TryParse(string value, out DownloadMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "form": mode = DownloadMode.Form; return true;
            case "combined": mode = DownloadMode.Combined; return true;
            case "attachments": mode = DownloadMode.Attachments; return true;
            default: mode = default; return false;
        }
    }

    // Canonical value sent to Kuali in the `options: [String!]!` array.
    public static string ToKualiOption(DownloadMode mode) => mode switch
    {
        DownloadMode.Form => "Form",
        DownloadMode.Combined => "Combined",
        DownloadMode.Attachments => "Attachments",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
