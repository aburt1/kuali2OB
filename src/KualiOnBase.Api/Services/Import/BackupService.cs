using System.Globalization;
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
    // The name format we write: "yyyyMMdd_HHmmss_{safeDocId}".
    // PurgeOlderThan parses the leading 15 chars back out; we don't trust
    // Directory.GetCreationTimeUtc because on some network filesystems (SMB
    // via CIFS, certain NFS configurations, containers that bind-mount a
    // host dir, backup-and-restore scenarios) creation time is reset to
    // "now" on every access/touch, which would keep purged folders alive
    // forever. Parsing the name is the ground-truth timestamp.
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
                if (!TryParseFolderTimestamp(leaf, out var stamp))
                {
                    // Not a folder we created — leave it alone. Operators
                    // occasionally drop manual diagnostic folders in the
                    // backup root; blindly deleting "anything old" would
                    // nuke those without warning.
                    continue;
                }

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
