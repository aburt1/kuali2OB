using KualiOnBase.Api.Models;

namespace KualiOnBase.Api.Services.Kuali;

public interface IKualiClient
{
    Task<KualiDocument> GetDocumentAsync(string documentId, CancellationToken ct);

    // `exportOptions` passes straight through to Kuali's `options: [String!]!`.
    // Production callers send `["Combined"]` (the documented canonical value).
    // Empirically, on tenants with "Include PDFs uploaded through the form"
    // disabled, Kuali ignores this array entirely and returns the form render
    // only — the tenant setting is the real switch. The diagnostic probe
    // endpoint uses this signature to sweep arbitrary values for future debugging.
    Task<string> ExportPdfAsync(string documentId, IReadOnlyList<string> exportOptions, CancellationToken ct);

    Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct);

    Task ClearAttachmentsAsync(string documentId, IReadOnlyList<string> fieldPaths, CancellationToken ct);

    Task DeleteDocumentAsync(string documentId, CancellationToken ct);
}
