using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KualiOnBase.Api.Tests.Configuration;

public sealed class SecretProviderTests
{
    private sealed class FakeVault : ISecretProvider
    {
        private readonly Dictionary<string, string?> _values;

        public FakeVault(Dictionary<string, string?> values) => _values = values;

        public string Name => "FakeVault";

        public IReadOnlyDictionary<string, string?> Load() => _values;
    }

    [Fact]
    public void DefaultsToEnvironmentWhenNoProviderConfigured()
    {
        var configuration = new ConfigurationBuilder().Build();

        var provider = SecretConfigurationExtensions.ResolveSecretProvider(configuration);

        Assert.Equal("Environment", provider.Name);
    }

    [Theory]
    [InlineData("Environment")]
    [InlineData("environment")]
    [InlineData("  Environment  ")]
    public void ResolvesEnvironmentProviderCaseInsensitively(string configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Secrets:Provider"] = configured })
            .Build();

        Assert.Equal("Environment", SecretConfigurationExtensions.ResolveSecretProvider(configuration).Name);
    }

    // A typo must not silently fall back to reading secrets from somewhere else.
    [Fact]
    public void UnknownProviderNameThrows()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Secrets:Provider"] = "Thycotc" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => SecretConfigurationExtensions.ResolveSecretProvider(configuration));

        Assert.Contains("not a known secret provider", ex.Message);
    }

    [Fact]
    public void EnvironmentProviderChangesNothing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ApiKey"] = "from-env" })
            .AddSecretProvider(new EnvironmentSecretProvider())
            .Build();

        Assert.Equal("from-env", configuration["Auth:ApiKey"]);
    }

    [Fact]
    public void VaultValuesOverrideEarlierSources()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:ApiKey"] = "from-webconfig",
                ["Kuali:BaseUrl"] = "https://csub.kualibuild.com",
            })
            .AddSecretProvider(new FakeVault(new Dictionary<string, string?>
            {
                [SecretKeys.AuthApiKey] = "from-vault",
            }))
            .Build();

        Assert.Equal("from-vault", configuration["Auth:ApiKey"]);
        // Non-secret settings are untouched by the vault layer.
        Assert.Equal("https://csub.kualibuild.com", configuration["Kuali:BaseUrl"]);
    }

    // A partially populated vault should degrade to existing configuration rather
    // than blanking a value the app needs to boot.
    [Fact]
    public void NullVaultValueLeavesTheExistingValueInPlace()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ApiKey"] = "from-webconfig" })
            .AddSecretProvider(new FakeVault(new Dictionary<string, string?>
            {
                [SecretKeys.AuthApiKey] = null,
            }))
            .Build();

        Assert.Equal("from-webconfig", configuration["Auth:ApiKey"]);
    }

    [Fact]
    public void VaultSuppliedSecretsBindOntoAppSettingsAndSatisfyStartupValidation()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\onbase\drop" : "/onbase/drop";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kuali:BaseUrl"] = "https://csub.kualibuild.com",
                ["Kuali:PublicBaseUrl"] = "https://example.edu/kuali2OB",
                ["Backup:RootPath"] = root,
                ["Import:AllowedTargetRoots"] = root,
            })
            .AddSecretProvider(new FakeVault(new Dictionary<string, string?>
            {
                [SecretKeys.AuthApiKey] = "a-sufficiently-long-api-key",
                [SecretKeys.KualiApiToken] = "a-sufficiently-long-kuali-token",
                [SecretKeys.KualiCallbackSecret] = "a-sufficiently-long-callback-secret",
            }))
            .Build();

        var settings = new AppSettings();
        configuration.Bind(settings);

        Assert.Equal("a-sufficiently-long-api-key", settings.Auth.ApiKey);
        Assert.Equal("a-sufficiently-long-kuali-token", settings.Kuali.ApiToken);

        // The whole point of the seam: a vault-sourced value is indistinguishable
        // from an env var by the time StartupValidator sees it.
        StartupValidator.ValidateOrThrow(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    [Fact]
    public void SecretKeysListsOnlyRealSecrets()
    {
        // Deployment settings must not drift into the vault contract.
        Assert.DoesNotContain("Import:AllowedTargetRoots", SecretKeys.All);
        Assert.DoesNotContain("Database:Path", SecretKeys.All);
        Assert.Contains(SecretKeys.AuthApiKey, SecretKeys.All);
        Assert.Contains(SecretKeys.KualiApiToken, SecretKeys.All);
        Assert.Contains(SecretKeys.KualiCallbackSecret, SecretKeys.All);
    }
}
