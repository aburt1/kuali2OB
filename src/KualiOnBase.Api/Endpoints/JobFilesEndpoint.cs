using System.Text.Json;
using KualiOnBase.Api.Services;

namespace KualiOnBase.Api.Endpoints;

public static class JobFilesEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app)
    {
        return app.MapGet("/api/jobs/{id:long}/files/{index:int}", Handle);
    }

    public static async Task<IResult> Handle(
        long id,
        int index,
        RetryQueue queue,
        CancellationToken ct)
    {
        var job = await queue.GetAsync(id, ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        var files = DecodeFiles(job.ProducedFiles);
        if (index < 0 || index >= files.Count)
        {
            return Results.NotFound();
        }

        var candidate = files[index];
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch
        {
            return Results.BadRequest();
        }

        // Containment check: the resolved file must sit inside the job's target
        // folder OR the backup folder we recorded — reject anything else so this
        // endpoint can't be used to read arbitrary files off disk.
        if (!IsUnder(fullPath, job.TargetFolderPath)
            && !(job.BackupFolderPath is { Length: > 0 } backup && IsUnder(fullPath, backup)))
        {
            return Results.NotFound();
        }

        if (!File.Exists(fullPath))
        {
            return Results.NotFound();
        }

        var contentType = InferContentType(fullPath);
        // No fileDownloadName → no Content-Disposition: attachment header, so
        // the browser renders PDFs inline and shows .txt as plain text.
        return Results.File(fullPath, contentType, enableRangeProcessing: true);
    }

    private static bool IsUnder(string fullPath, string folder)
    {
        string fullFolder;
        try { fullFolder = Path.GetFullPath(folder); }
        catch { return false; }

        var folderWithSep = fullFolder.EndsWith(Path.DirectorySeparatorChar)
            ? fullFolder
            : fullFolder + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(folderWithSep, StringComparison.Ordinal);
    }

    private static string InferContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".txt" => "text/plain; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    private static IReadOnlyList<string> DecodeFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}
