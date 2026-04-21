namespace KualiOnBase.Api.Configuration;

public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    public string RootPath { get; set; } = string.Empty;
    public int RetentionDays { get; set; } = 30;
    public int CleanupIntervalHours { get; set; } = 24;
}
