using KualiOnBase.Api.Options;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Services.Import;

public interface IBackupService
{
    string CreateBackupFolder(string documentId, DateTime timestampUtc);
    Task CopyAsync(string sourcePath, string backupFolder, CancellationToken ct);
    void PurgeOlderThan(DateTime cutoffUtc);
}

public sealed class BackupService : IBackupService
{
    private readonly BackupOptions _options;
    private readonly ILogger<BackupService> _log;

    public BackupService(IOptions<BackupOptions> options, ILogger<BackupService> log)
    {
        _options = options.Value;
        _log = log;
    }

    public string CreateBackupFolder(string documentId, DateTime timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(_options.RootPath))
        {
            throw new InvalidOperationException("Backup:RootPath is not configured.");
        }

        var safeId = FileNameSanitizer.Sanitize(documentId);
        var name = $"{timestampUtc:yyyyMMdd_HHmmss}_{safeId}";
        var path = Path.Combine(_options.RootPath, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task CopyAsync(string sourcePath, string backupFolder, CancellationToken ct)
    {
        Directory.CreateDirectory(backupFolder);
        var dest = Path.Combine(backupFolder, Path.GetFileName(sourcePath));
        await using var src = File.OpenRead(sourcePath);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct);
    }

    public void PurgeOlderThan(DateTime cutoffUtc)
    {
        if (string.IsNullOrWhiteSpace(_options.RootPath) || !Directory.Exists(_options.RootPath))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(_options.RootPath))
        {
            try
            {
                var created = Directory.GetCreationTimeUtc(dir);
                if (created < cutoffUtc)
                {
                    Directory.Delete(dir, recursive: true);
                    _log.LogInformation("Purged expired backup {Folder}", dir);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to purge backup {Folder}", dir);
            }
        }
    }
}
