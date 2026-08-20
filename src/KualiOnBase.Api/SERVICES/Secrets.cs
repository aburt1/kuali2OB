using Microsoft.Extensions.Configuration;

namespace KualiOnBase.Api.Services;

/// <summary>
/// Where secret values come from.
///
/// Today the only implementation is <see cref="EnvironmentSecretProvider"/>, which
/// is a no-op: environment variables (on IIS, the entries in web.config) are already
/// loaded by the host's default configuration providers, so nothing extra is needed.
///
/// The point of this interface is that adding a vault later — Thycotic/Delinea
/// Secret Server, for example — is a matter of writing one class and flipping one
/// setting, with no change to AppSettings, StartupValidator, or any consumer.
/// See "Adding a secret provider" in DEVELOPER-GUIDE.md.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Name used in the startup log line, so it is obvious which source is live.</summary>
    string Name { get; }

    /// <summary>
    /// Returns configuration keys to overlay, in standard configuration notation
    /// ("Auth:ApiKey", "Kuali:ApiToken"). Called once during startup, before
    /// AppSettings is bound and before StartupValidator runs, so a value fetched
    /// here is indistinguishable from one set in web.config.
    /// </summary>
    IReadOnlyDictionary<string, string?> Load();
}

/// <summary>
/// The default. Returns nothing, because environment variables are already loaded
/// by the host. Exists so the "no vault configured" case is an explicit, named
/// choice rather than the absence of one.
/// </summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    public string Name => "Environment";

    public IReadOnlyDictionary<string, string?> Load() =>
        new Dictionary<string, string?>();
}

/// <summary>
/// The configuration keys a vault provider is expected to supply. Listed in one
/// place so a new provider knows exactly what it must return, and so a missing
/// value produces the same StartupValidator error as a missing env var.
/// </summary>
public static class SecretKeys
{
    public const string AuthApiKey = "Auth:ApiKey";
    public const string KualiApiToken = "Kuali:ApiToken";
    public const string KualiCallbackSecret = "Kuali:CallbackSecret";
    public const string SmtpPassword = "Notifications:Email:SmtpPassword";

    /// <summary>
    /// Every value that is genuinely a secret. Deployment settings — paths, URLs,
    /// Import:AllowedTargetRoots — are deliberately NOT here: they belong in
    /// web.config, and putting them in a vault adds a failure mode for no gain.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        AuthApiKey,
        KualiApiToken,
        KualiCallbackSecret,
        SmtpPassword,
    ];
}

// Adapts an ISecretProvider into the configuration pipeline so the values it
// returns layer over the earlier sources, exactly like any built-in provider.
internal sealed class SecretConfigurationSource : IConfigurationSource
{
    private readonly ISecretProvider _provider;

    public SecretConfigurationSource(ISecretProvider provider) => _provider = provider;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new SecretConfigurationProvider(_provider);
}

internal sealed class SecretConfigurationProvider : ConfigurationProvider
{
    private readonly ISecretProvider _provider;

    public SecretConfigurationProvider(ISecretProvider provider) => _provider = provider;

    public override void Load()
    {
        foreach (var (key, value) in _provider.Load())
        {
            // A null means "the vault had nothing for this key" — leave whatever an
            // earlier source supplied rather than blanking it, so a partially
            // populated vault degrades to the existing configuration instead of
            // failing the app outright.
            if (value is not null) Data[key] = value;
        }
    }
}

public static class SecretConfigurationExtensions
{
    /// <summary>
    /// Registers a secret provider as the last configuration source, so its values
    /// win over appsettings.json and environment variables.
    /// </summary>
    public static IConfigurationBuilder AddSecretProvider(
        this IConfigurationBuilder builder, ISecretProvider provider) =>
        builder.Add(new SecretConfigurationSource(provider));

    /// <summary>
    /// Chooses the provider from the "Secrets:Provider" setting. Unrecognised names
    /// throw rather than silently falling back, so a typo in a deployment cannot
    /// quietly leave the app reading secrets from the wrong place.
    /// </summary>
    public static ISecretProvider ResolveSecretProvider(IConfiguration configuration)
    {
        var name = configuration["Secrets:Provider"];
        if (string.IsNullOrWhiteSpace(name)) return new EnvironmentSecretProvider();

        return name.Trim().ToLowerInvariant() switch
        {
            "environment" => new EnvironmentSecretProvider(),

            // Register a vault implementation here. Nothing else needs to change.
            // e.g. "thycotic" => new ThycoticSecretProvider(configuration),

            _ => throw new InvalidOperationException(
                $"Secrets:Provider '{name}' is not a known secret provider. " +
                $"Valid values: Environment."),
        };
    }
}
