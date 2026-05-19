using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Models;

// Keep configuration in one predictable file. These classes bind directly to
// appsettings.json / environment variables, and StartupValidator gives operators
// a clear failure before the app starts doing work with placeholder secrets.
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string ApiKey { get; set; } = string.Empty;
}

public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    public string RootPath { get; set; } = string.Empty;
    public int RetentionDays { get; set; } = 30;
    public int CleanupIntervalHours { get; set; } = 24;
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string Path { get; set; } = "./data/kuali-onbase.db";
}

public sealed class ImportOptions
{
    public const string SectionName = "Import";

    // Semicolon-separated list of absolute path prefixes that `targetFolderPath`
    // requests are allowed to land under. Empty means every request is rejected.
    public string AllowedTargetRoots { get; set; } = string.Empty;

    public IReadOnlyList<string> ParseAllowedRoots() =>
        string.IsNullOrWhiteSpace(AllowedTargetRoots)
            ? Array.Empty<string>()
            : AllowedTargetRoots
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Path.GetFullPath)
                .ToArray();
}

public sealed class KualiOptions
{
    public const string SectionName = "Kuali";

    public string BaseUrl { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string PublicBaseUrl { get; set; } = string.Empty;
    public string CallbackSecret { get; set; } = string.Empty;
    public string ExportTimeZone { get; set; } = "America/Los_Angeles";
    public int ExportCallbackTimeoutSeconds { get; set; } = 180;
    public int ExportCallbackPollMilliseconds { get; set; } = 500;
}

public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public EmailOptions Email { get; set; } = new();

    public sealed class EmailOptions
    {
        public bool Enabled { get; set; }
        public string SmtpHost { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 25;
        public string? SmtpUsername { get; set; }
        public string? SmtpPassword { get; set; }
        public bool UseSsl { get; set; }
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
    }
}

public sealed class RetryOptions
{
    public const string SectionName = "Retry";

    public int MaxAttempts { get; set; } = 5;
    public int BaseDelaySeconds { get; set; } = 60;
    public int PollIntervalSeconds { get; set; } = 30;
    public int SucceededJobRetentionDays { get; set; } = 30;
}

public sealed class UiOptions
{
    public const string SectionName = "Ui";

    public bool Enabled { get; set; } = true;
}

public static class StartupValidator
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHANGEME", "CHANGE_ME", "<SET-ME>",
    };

    private const int MinSecretLength = 16;

    public static void ValidateOrThrow(IServiceProvider services, ILogger log)
    {
        var errors = new List<string>();
        var auth = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var kuali = services.GetRequiredService<IOptions<KualiOptions>>().Value;
        var backup = services.GetRequiredService<IOptions<BackupOptions>>().Value;
        var notifs = services.GetRequiredService<IOptions<NotificationsOptions>>().Value;
        var import = services.GetRequiredService<IOptions<ImportOptions>>().Value;

        Require(errors, "Auth:ApiKey", auth.ApiKey, secret: true);
        Require(errors, "Kuali:BaseUrl", kuali.BaseUrl);
        Require(errors, "Kuali:ApiToken", kuali.ApiToken, secret: true);
        Require(errors, "Kuali:PublicBaseUrl", kuali.PublicBaseUrl);
        Require(errors, "Kuali:CallbackSecret", kuali.CallbackSecret, secret: true);
        Require(errors, "Backup:RootPath", backup.RootPath);

        RequireAbsoluteUri(errors, "Kuali:BaseUrl", kuali.BaseUrl);
        RequireAbsoluteUri(errors, "Kuali:PublicBaseUrl", kuali.PublicBaseUrl);

        var roots = import.ParseAllowedRoots();
        if (roots.Count == 0)
        {
            errors.Add("Import:AllowedTargetRoots is not set. Configure at least one absolute path prefix.");
        }
        else
        {
            for (var i = 0; i < roots.Count; i++)
            {
                if (!Path.IsPathRooted(roots[i]))
                {
                    errors.Add($"Import:AllowedTargetRoots[{i}] = '{roots[i]}' is not an absolute path.");
                }
            }
        }

        if (notifs.Email.Enabled)
        {
            Require(errors, "Notifications:Email:SmtpHost", notifs.Email.SmtpHost);
            Require(errors, "Notifications:Email:From", notifs.Email.From);
            Require(errors, "Notifications:Email:To", notifs.Email.To);
            ValidateEmailAddress(errors, "Notifications:Email:From", notifs.Email.From);
            ValidateEmailCsv(errors, "Notifications:Email:To", notifs.Email.To);
        }

        if (errors.Count > 0)
        {
            var message = "Refusing to start - config is missing or still placeholder:\n  - "
                + string.Join("\n  - ", errors)
                + "\nSet the missing values via env vars (Auth__ApiKey, Kuali__ApiToken, ...) or appsettings.";
            log.LogCritical("{Message}", message);
            throw new InvalidOperationException(message);
        }

        log.LogInformation(
            "Startup config validation passed. AllowedTargetRoots={Roots}",
            string.Join(";", roots));
    }

    private static void Require(List<string> errors, string name, string? value, bool secret = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is not set.");
            return;
        }

        var trimmed = value.Trim();
        if (Placeholders.Contains(trimmed))
        {
            errors.Add($"{name} is still set to a known placeholder (\"{trimmed}\").");
            return;
        }

        if (secret && trimmed.Length < MinSecretLength)
        {
            errors.Add($"{name} is shorter than {MinSecretLength} characters; generate a real secret.");
        }
    }

    private static void RequireAbsoluteUri(List<string> errors, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{name} = '{value}' is not a valid http(s) URL.");
        }
    }

    private static void ValidateEmailAddress(List<string> errors, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try { _ = new MailAddress(value.Trim()); }
        catch (FormatException) { errors.Add($"{name} = '{value}' is not a valid email address."); }
    }

    private static void ValidateEmailCsv(List<string> errors, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            try { _ = new MailAddress(p); }
            catch (FormatException)
            {
                errors.Add($"{name} contains an invalid address: '{p}'.");
            }
        }
    }
}
