namespace KualiOnBase.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Path { get; set; } = "./data/kuali-onbase.db";
}
