namespace KualiOnBase.Api.Options;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string ApiKey { get; set; } = string.Empty;
}
