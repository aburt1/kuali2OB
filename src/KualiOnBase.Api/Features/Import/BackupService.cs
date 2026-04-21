using System.Globalization;
using KualiOnBase.Api.Configuration;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Features.Import;

public interface IBackupService
{
    string CreateBackupFolder(string documentId, DateTime timestampUtc);
    Task CopyAsync(string sourcePath, string backupFolder, CancellationToken ct);
    void PurgeOlderThan(DateTime cutoffUtc);
}

public sealed class BackupService : IBackupService
{
    // PurgeOlderThan parses the leading 15 chars back out — we can't trust
    // Directory.GetCreationTimeUtc because SMB/CIFS and some bind-mount setups
    // reset creation time on every touch, which would keep backups alive forever.
    private const string BackupNameTimestampFormat = "yyyyMMdd_HHmmss";

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
        var name = $"{timestampUtc.ToString(BackupNameTimestampFormat, CultureInfo.InvariantCulture)}_{safeId}";
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
                var leaf = Path.GetFileName(dir);
                // Skip folders we didn't create (operator-dropped diagnostics etc.).
                if (!TryParseFolderTimestamp(leaf, out var stamp)) continue;

                if (stamp < cutoffUtc)
                {
                    Directory.Delete(dir, recursive: true);
                    _log.LogInformation("Purged expired backup {Folder}", leaf);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to purge backup {Folder}", Path.GetFileName(dir));
            }
        }
    }

    internal static bool TryParseFolderTimestamp(string? folderName, out DateTime timestampUtc)
    {
        timestampUtc = default;
        if (string.IsNullOrEmpty(folderName) || folderName.Length < BackupNameTimestampFormat.Length)
        {
            return false;
        }
        var stampSegment = folderName.Substring(0, BackupNameTimestampFormat.Length);
        if (!DateTime.TryParseExact(
                stampSegment,
                BackupNameTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }
        timestampUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
}
