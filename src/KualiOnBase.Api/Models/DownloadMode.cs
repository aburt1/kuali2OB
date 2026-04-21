namespace KualiOnBase.Api.Models;

// Two download shapes, chosen by the caller:
//
// pdf         → one PDF via Kuali's exportDocument mutation. Whether that PDF
//               contains just the form render or form + merged attachments is
//               controlled by the Kuali tenant setting "Include PDFs uploaded
//               through the form" — not by this API. See README.
// attachments → raw attachment files downloaded directly, preserving original
//               formats (.docx/.jpg/.xlsx/…). N files out, one per attachment.
public enum DownloadMode
{
    Pdf,
    Attachments,
}

public static class DownloadModes
{
    public static bool TryParse(string? value, out DownloadMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "pdf":         mode = DownloadMode.Pdf;         return true;
            case "attachments": mode = DownloadMode.Attachments; return true;
            default:            mode = default;                  return false;
        }
    }
}
