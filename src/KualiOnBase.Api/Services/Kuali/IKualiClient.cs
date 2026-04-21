using KualiOnBase.Api.Models;

namespace KualiOnBase.Api.Services.Kuali;

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
