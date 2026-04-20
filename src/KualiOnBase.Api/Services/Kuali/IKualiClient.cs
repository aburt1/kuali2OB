using KualiOnBase.Api.Models;

namespace KualiOnBase.Api.Services.Kuali;

public interface IKualiClient
{
    Task<KualiDocument> GetDocumentAsync(string documentId, CancellationToken ct);

    // `exportOption` maps to a single entry in Kuali's `options: [String!]!` array
     // (Form | Combined | Attachments). Pass null for tenant default (empty array).
    Task<string> ExportPdfAsync(string documentId, string? exportOption, CancellationToken ct);

    Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct);

    Task ClearAttachmentsAsync(string documentId, IReadOnlyList<string> fieldPaths, CancellationToken ct);

    Task DeleteDocumentAsync(string documentId, CancellationToken ct);
}
