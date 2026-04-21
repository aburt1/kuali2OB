namespace KualiOnBase.Api.Options;

public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public EmailOptions Email { get; set; } = new();

    public sealed class EmailOptions
    {
        // Toggle the whole email path off without deleting config.
        public bool Enabled { get; set; }

        public string SmtpHost { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 25;
        public string? SmtpUsername { get; set; }
        public string? SmtpPassword { get; set; }
        public bool UseSsl { get; set; }

        public string From { get; set; } = string.Empty;
        // Comma-separated list of recipients.
        public string To { get; set; } = string.Empty;
    }
}
