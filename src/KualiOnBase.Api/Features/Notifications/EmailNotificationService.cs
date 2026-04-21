using System.Net;
using System.Net.Mail;
using System.Text;
using KualiOnBase.Api.Features.Import;
using KualiOnBase.Api.Features.Kuali;
using KualiOnBase.Api.Configuration;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Features.Notifications;

public sealed class EmailNotificationService : INotificationService
{
    private readonly NotificationsOptions _options;
    private readonly ILogger<EmailNotificationService> _log;

    public EmailNotificationService(
        IOptions<NotificationsOptions> options,
        ILogger<EmailNotificationService> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task NotifyJobFailedAsync(ImportJob job, CancellationToken ct)
    {
        var cfg = _options.Email;
        if (!cfg.Enabled)
        {
            return;
        }

        var recipients = (cfg.To ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0)
        {
            _log.LogWarning("Email notifications enabled but Notifications:Email:To is empty.");
            return;
        }

        // Validate per-recipient so one bad address doesn't drop the whole alert.
        var valid = new List<MailAddress>(recipients.Length);
        foreach (var r in recipients)
        {
            if (TryParseAddress(r, out var addr))
            {
                valid.Add(addr!);
            }
            else
            {
                _log.LogWarning("Skipping invalid notification recipient '{Recipient}'.", r);
            }
        }
        if (valid.Count == 0)
        {
            _log.LogError("All Notifications:Email:To entries are invalid; failure notification dropped for job {JobId}.", job.Id);
            return;
        }

        MailAddress from;
        try
        {
            from = new MailAddress(SanitizeHeader(cfg.From));
        }
        catch (FormatException ex)
        {
            _log.LogError(ex, "Notifications:Email:From is not a valid address; failure notification dropped for job {JobId}.", job.Id);
            return;
        }

        var safeDocId = SanitizeHeader(job.DocumentId);
        var subject = SanitizeHeader($"[Kuali->OnBase] Import job {job.Id} FAILED (document {safeDocId})");

        var body = new StringBuilder()
            .AppendLine($"Import job #{job.Id} moved to status: {job.Status}.")
            .AppendLine()
            .AppendLine($"Document id:       {job.DocumentId}")
            .AppendLine($"OnBase doc type:   {job.OnBaseDocType}")
            .AppendLine($"Target folder:     {job.TargetFolderPath}")
            .AppendLine($"Download mode:     {job.DownloadMode}")
            .AppendLine($"Delete attachments:{job.DeleteAttachments}")
            .AppendLine($"Delete document:   {job.DeleteDocument}")
            .AppendLine($"Attempts:          {job.AttemptCount}")
            .AppendLine()
            .AppendLine("Last error:")
            .AppendLine(job.LastError ?? "(no message)")
            .AppendLine()
            .AppendLine("Inspect the full event timeline in the Auditor dashboard.")
            .ToString();

        try
        {
            using var message = new MailMessage
            {
                From = from,
                Subject = subject,
                Body = body,
                IsBodyHtml = false,
            };
            foreach (var addr in valid)
            {
                message.To.Add(addr);
            }

            // SmtpClient is SYSLIB0014-obsolete but fine for simple internal
            // relays. Timeout bounds a hung SMTP handshake (not ct-backed).
#pragma warning disable SYSLIB0014
            using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
            {
                EnableSsl = cfg.UseSsl,
                Timeout = 30_000,
            };
#pragma warning restore SYSLIB0014
            if (!string.IsNullOrEmpty(cfg.SmtpUsername))
            {
                client.Credentials = new NetworkCredential(cfg.SmtpUsername, cfg.SmtpPassword ?? string.Empty);
            }

            await client.SendMailAsync(message, ct);
            _log.LogInformation(
                "Failure notification email sent for job {JobId} to {RecipientCount} recipient(s)",
                job.Id, valid.Count);
        }
        catch (Exception ex)
        {
            // Never propagate — SMTP issues must not mask the underlying job failure.
            _log.LogError(ex, "Failed to send failure-notification email for job {JobId}", job.Id);
        }
    }

    // Strip CR/LF so user-controlled values (documentId) can't smuggle headers
    // into the Subject line (CWE-93).
    internal static string SanitizeHeader(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static bool TryParseAddress(string raw, out MailAddress? addr)
    {
        addr = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            addr = new MailAddress(raw.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
