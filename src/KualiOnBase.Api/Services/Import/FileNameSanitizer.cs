using System.Text;

namespace KualiOnBase.Api.Services.Import;

public static class FileNameSanitizer
{
    private static readonly char[] Invalid =
        ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0', '\r', '\n', '\t'];

    // Windows-reserved device names. If an OnBase drop is served from an SMB
    // share to a Windows host, creating `CON.pdf` / `NUL.txt` fails with
    // UnauthorizedAccessException. Prefix these so the filename is safe to
    // create everywhere. Kuali documentIds are UUID-ish in practice so this
    // almost never trips, but it's cheap insurance.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (Array.IndexOf(Invalid, ch) >= 0 || char.IsControl(ch))
            {
                sb.Append('-');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var cleaned = sb.ToString().Trim(' ', '.', '-', '_');
        if (cleaned.Length == 0) return "file";
        if (ReservedNames.Contains(cleaned)) return "doc-" + cleaned;
        return cleaned;
    }

    // Filenames are the Kuali documentId. Sanitization is defensive; the id is
    // already URL-safe. Multi-file jobs get `_2`, `_3`, … via MakeUnique.
    public static string BuildContentFileName(string documentId, string extension)
    {
        var stem = Sanitize(documentId);
        var ext = NormalizeExtension(extension);
        return ext.Length == 0 ? stem : $"{stem}.{ext}";
    }

    public static string MakeUnique(string fileName, ISet<string> existing)
    {
        if (existing.Add(fileName))
        {
            return fileName;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var counter = 2;
        while (true)
        {
            var candidate = string.IsNullOrEmpty(ext)
                ? $"{stem}_{counter}"
                : $"{stem}_{counter}{ext}";
            if (existing.Add(candidate))
            {
                return candidate;
            }
            counter++;
        }
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }
        var trimmed = extension.Trim().TrimStart('.');
        return Sanitize(trimmed).ToLowerInvariant();
    }
}
