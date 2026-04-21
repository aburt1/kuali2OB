using System.Text;

namespace KualiOnBase.Api.Services.Import;

public static class FileNameSanitizer
{
    private static readonly char[] Invalid =
        ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0', '\r', '\n', '\t'];

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
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    // Filenames are the Kuali documentId, period. This keeps DIP-side lookup
    // trivial (grep by id, no fuzzy match on sanitized names), survives any
    // changes to Kuali's form-field shape (serial / school / name no longer
    // baked into the filename), and the id is already URL-safe so sanitization
    // is defensive-only. Multiple files from the same job get `_2`, `_3`, …
    // suffixes via MakeUnique.
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
