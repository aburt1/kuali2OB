using System.Text;

namespace KualiOnBase.Api.Features.Import;

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

        // DIP parses line-oriented "KEY: VALUE". Strip control chars from every
        // user-controlled value so a smuggled \r\n can't inject DIP directives
        // (CWE-93 — would otherwise let a caller rewrite ONBASE_DOC_TYPE).
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

    internal static string StripLineBreaks(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
}
