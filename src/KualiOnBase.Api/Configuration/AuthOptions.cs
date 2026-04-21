namespace KualiOnBase.Api.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string ApiKey { get; set; } = string.Empty;
}
