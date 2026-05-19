using KualiOnBase.Api.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KualiOnBase.Api.Tests.Configuration;

public sealed class ConfigurationTests
{
    [Fact]
    public void AppSettings_BindsExistingSectionAndEnvironmentNames()
    {
        var values = new Dictionary<string, string?>
        {
            ["Auth:ApiKey"] = "0123456789abcdef",
            ["Kuali:BaseUrl"] = "https://csub.kualibuild.com",
            ["Kuali:ApiToken"] = "abcdef0123456789",
            ["Kuali:PublicBaseUrl"] = "https://kuali2ob.example.edu",
            ["Kuali:CallbackSecret"] = "abcdef0123456789",
            ["Backup:RootPath"] = "/backup",
            ["Database:Path"] = "/data/kuali-onbase.db",
            ["Import:AllowedTargetRoots"] = "/target;/other-target",
            ["Notifications:Email:Enabled"] = "true",
            ["Notifications:Email:SmtpHost"] = "smtp.example.edu",
            ["Notifications:Email:From"] = "from@example.edu",
            ["Notifications:Email:To"] = "ops@example.edu",
        };

        var settings = new AppSettings();
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .Bind(settings);

        Assert.Equal("0123456789abcdef", settings.Auth.ApiKey);
        Assert.Equal("https://csub.kualibuild.com", settings.Kuali.BaseUrl);
        Assert.Equal("/data/kuali-onbase.db", settings.Database.Path);
        Assert.True(settings.Notifications.Email.Enabled);
        Assert.Equal(new[] { "/target", "/other-target" }, settings.Import.ParseAllowedRoots());
    }

    [Fact]
    public void ProjectLayout_KeepsDashboardAndEmbeddedMigrations()
    {
        var repo = FindRepoRoot();
        var app = Path.Combine(repo, "src", "KualiOnBase.Api");
        var project = File.ReadAllText(Path.Combine(app, "KualiOnBase.Api.csproj"));

        Assert.True(File.Exists(Path.Combine(app, "WWWROOT", "index.html")));
        Assert.Contains("SERVICES/Data/Migrations/*.sql", project);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "KualiOnBase.Api.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root.");
    }
}
