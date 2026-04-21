using System.Text;

namespace KualiOnBase.Api.Services.Import;

public sealed record IndexFileEntry(string FileName, string ExternalSourceRef);

public static class IndexFileBuilder
{
    public const string ExternalSourceLiteral = "KUALI BUILD";

    public static string Build(
        string onbaseDocType,
        IReadOnlyList<IndexFileEntry> files,
        IReadOnlyList<KeyValuePair<string, string>> keywords)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("At least one file entry is required.", nameof(files));
        }

        // Every string written to this index file came (directly or via Kuali) from
        // an untrusted caller. OnBase DIP parses the file as line-oriented
        // "KEY: VALUE" — a smuggled \r\n in any value lets the caller inject
        // arbitrary DIP directives (re-writing ONBASE_DOC_TYPE, FILENAME, etc.)
        // and deliver the document into a different OnBase doctype than declared.
        // Collapse CR, LF, and other control characters to spaces before writing.
        var safeDocType = StripLineBreaks(onbaseDocType);

        var sb = new StringBuilder();
        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }
            sb.Append("ONBASE_DOC_TYPE: ").AppendLine(safeDocType);
            sb.Append("FILENAME: ").AppendLine(StripLineBreaks(files[i].FileName));
            sb.Append("EXTERNAL_SOURCE: ").AppendLine(ExternalSourceLiteral);
            sb.Append("EXTERNAL_SOURCE_REF: ").AppendLine(StripLineBreaks(files[i].ExternalSourceRef));
            foreach (var pair in keywords)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }
                sb.Append(StripLineBreaks(pair.Key)).Append(": ").AppendLine(StripLineBreaks(pair.Value));
            }
        }
        return sb.ToString();
    }

    internal static string StripLineBreaks(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(ch == '\r' || ch == '\n' || char.IsControl(ch) ? ' ' : ch);
        }
        return sb.ToString().Trim();
    }
}
