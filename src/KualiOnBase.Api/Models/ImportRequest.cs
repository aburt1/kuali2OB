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

// Two shapes, chosen by the caller:
//
// pdf         → one PDF file via Kuali's exportDocument mutation. What's actually
//               *in* that PDF (form only, or form + PDF attachments merged) is
//               determined entirely by the Kuali tenant setting
//               "Include PDFs uploaded through the form" — not by us. See
//               README → "Kuali tenant prerequisite".
// attachments → raw attachment files downloaded directly from Kuali, preserving
//               original formats (.docx/.jpg/.xlsx/…). N files out, one per
//               attachment. Bypasses exportDocument entirely.
public enum DownloadMode
{
    Pdf,
    Attachments,
}

public static class DownloadModes
{
    public static bool TryParse(string value, out DownloadMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "pdf": mode = DownloadMode.Pdf; return true;
            case "attachments": mode = DownloadMode.Attachments; return true;
            default: mode = default; return false;
        }
    }
}
