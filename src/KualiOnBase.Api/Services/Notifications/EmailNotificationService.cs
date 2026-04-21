using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Options;
using Microsoft.Extensions.Options;

namespace KualiOnBase.Api.Services.Notifications;

public sealed class EmailNotificationService : INotificationService
{
    // Anything user-controlled (documentId flows in via the URL) that lands in
    // the Subject line must have CR/LF stripped — .NET generally encodes them,
    // but we don't want to rely on that for a CWE-93 class vulnerability.
    private static readonly Regex HeaderUnsafeChars = new(@"[\r\n\x00-\x1F\x7F]", RegexOptions.Compiled);

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

        // Per-recipient validation BEFORE constructing the message. An invalid
        // address used to throw inside the foreach loop and take down the whole
        // notification, meaning zero recipients got the alert.
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

        // Sanitize anything that reaches the Subject line. documentId is the
        // user-controlled one (query-string), but the rest get cleaned for
        // defense-in-depth; SmtpClient will otherwise encode or reject them.
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

            // SmtpClient is marked SYSLIB0014-obsolete by Microsoft in favor of
            // MailKit, but it still works fine for simple internal SMTP relays
            // and avoids pulling in another dependency. Swap to MailKit if CSUB
            // ever needs OAuth or modern auth.
#pragma warning disable SYSLIB0014
            using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort)
            {
                EnableSsl = cfg.UseSsl,
                // SmtpClient.Timeout is the operation timeout (not cancellation-token
                // backed). Without this, a hung SMTP server pins a retry worker
                // indefinitely. 30s is well over any sane relay's handshake window.
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
            // Must never propagate — a broken SMTP config would otherwise mask
            // the underlying job failure from the caller.
            _log.LogError(ex, "Failed to send failure-notification email for job {JobId}", job.Id);
        }
    }

    internal static string SanitizeHeader(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return HeaderUnsafeChars.Replace(value, " ").Trim();
    }

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
