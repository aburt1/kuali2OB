using KualiOnBase.Api.Options;
using Microsoft.Extensions.Options;
using System.Net.Mail;

namespace KualiOnBase.Api.Startup;

// Refuses to boot when critical config is missing or still the shipped
// placeholder. Catches the #1 deploy mistake: env vars not injected, app boots
// "successfully", first request silently exposes the misconfig.
public static class StartupValidator
{
    // Known placeholder strings that quietly slip through when operators "fill
    // in" config without real secrets. Case-insensitive equality after trim.
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHANGEME", "CHANGE_ME", "CHANGE-ME", "PLACEHOLDER", "TODO",
        "SECRET", "PASSWORD", "PWD", "REPLACE", "REPLACE_ME", "<SET-ME>",
        "FIXME", "XXXX", "TBD",
    };

    // Secrets shorter than this are almost certainly weak/test values
    // ("asdf", "1234", …). 16 chars ~ 96 bits of base64, cheap bar to clear.
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

        // At least one allowed target root, and each must be an absolute path.
        // Without this the import endpoint would accept arbitrary paths (any
        // location the container user can write — /etc, /, another share, …).
        var roots = import.ParseAllowedRoots();
        if (roots.Count == 0)
        {
            errors.Add("Import:AllowedTargetRoots is not set. " +
                       "Configure at least one absolute path prefix (semicolon-separated) " +
                       "that targetFolderPath is permitted to land under.");
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

            // Parse each entry once at startup so bad recipients surface at deploy
            // time, not at 3am when a job actually fails.
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
        if (string.IsNullOrWhiteSpace(value)) return; // already caught by Require
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
