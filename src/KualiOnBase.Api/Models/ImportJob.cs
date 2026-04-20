namespace KualiOnBase.Api.Models;

public sealed class ImportJob
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string OnBaseDocType { get; set; } = string.Empty;
    public string TargetFolderPath { get; set; } = string.Empty;
    public string DownloadMode { get; set; } = string.Empty;
    public bool DeleteAttachments { get; set; }
    public bool DeleteDocument { get; set; }
    public string? KeywordsJson { get; set; }
    public string? PdfExport { get; set; }
    public string Status { get; set; } = JobStatus.Running;
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? BackupFolderPath { get; set; }
    public string? ProducedFiles { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class JobStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Retrying = "Retrying";
}
