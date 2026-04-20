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

    public static string BuildContentFileName(
        string serialNumber,
        string schoolId,
        string lastName,
        string firstName,
        string onbaseDocType,
        string extension)
    {
        var parts = new[]
        {
            Sanitize(serialNumber),
            Sanitize(schoolId),
            Sanitize(lastName),
            Sanitize(firstName),
            Sanitize(onbaseDocType),
        }.Where(p => p.Length > 0);

        var stem = string.Join('_', parts);
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
