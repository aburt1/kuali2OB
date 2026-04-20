using KualiOnBase.Api.Models;

namespace KualiOnBase.Api.Services.Kuali;

public interface IKualiClient
{
    Task<KualiDocument> GetDocumentAsync(string documentId, CancellationToken ct);

    // `exportOptions` passes straight through to Kuali's `options: [String!]!`.
    // Kuali's semantics are *additive inclusion* — list what you want in the PDF.
    // Examples: ["Form"], ["Attachments"], ["Form","Attachments"] (merged).
    // Empty array = tenant default.
    Task<string> ExportPdfAsync(string documentId, IReadOnlyList<string> exportOptions, CancellationToken ct);

    Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct);

    Task ClearAttachmentsAsync(string documentId, IReadOnlyList<string> fieldPaths, CancellationToken ct);

    Task DeleteDocumentAsync(string documentId, CancellationToken ct);
}
