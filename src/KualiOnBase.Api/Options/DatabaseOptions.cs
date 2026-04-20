namespace KualiOnBase.Api.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Path { get; set; } = "./data/kuali-onbase.db";
}
