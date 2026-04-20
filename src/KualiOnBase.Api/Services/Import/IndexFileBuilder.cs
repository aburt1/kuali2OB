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

        var sb = new StringBuilder();
        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }
            sb.Append("ONBASE_DOC_TYPE: ").AppendLine(onbaseDocType);
            sb.Append("FILENAME: ").AppendLine(files[i].FileName);
            sb.Append("EXTERNAL_SOURCE: ").AppendLine(ExternalSourceLiteral);
            sb.Append("EXTERNAL_SOURCE_REF: ").AppendLine(files[i].ExternalSourceRef);
            foreach (var pair in keywords)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }
                sb.Append(pair.Key).Append(": ").AppendLine(pair.Value);
            }
        }
        return sb.ToString();
    }
}
