using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services.Kuali;

namespace KualiOnBase.Tests.Fakes;

public sealed class FakeKualiClient : IKualiClient
{
    public KualiDocument Document { get; set; } =
        new("doc-1", "0014", "Jason", "Ferguson", "001933711", []);

    public string PdfUrl { get; set; } = "https://fake/pdf";
    public byte[] PdfBytes { get; set; } = [0x25, 0x50, 0x44, 0x46]; // %PDF
    public Dictionary<string, byte[]> AttachmentBytes { get; } = new();

    public bool AttachmentsCleared { get; private set; }
    public bool DocumentDeleted { get; private set; }
    public List<string> ClearedFieldPaths { get; } = new();

    public Exception? ExportPdfThrows { get; set; }

    public Task<KualiDocument> GetDocumentAsync(string documentId, CancellationToken ct)
        => Task.FromResult(Document);

    public Task<string> ExportPdfAsync(string documentId, CancellationToken ct)
    {
        if (ExportPdfThrows is not null) throw ExportPdfThrows;
        return Task.FromResult(PdfUrl);
    }

    public async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var bytes = url == PdfUrl
            ? PdfBytes
            : AttachmentBytes.TryGetValue(url, out var b) ? b : [0x00];

        await File.WriteAllBytesAsync(destinationPath, bytes, ct);
    }

    public Task ClearAttachmentsAsync(string documentId, IReadOnlyList<string> fieldPaths, CancellationToken ct)
    {
        AttachmentsCleared = true;
        ClearedFieldPaths.AddRange(fieldPaths);
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(string documentId, CancellationToken ct)
    {
        DocumentDeleted = true;
        return Task.CompletedTask;
    }
}
