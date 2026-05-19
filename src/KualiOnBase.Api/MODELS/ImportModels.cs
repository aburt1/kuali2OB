namespace KualiOnBase.Api.Models;

// Simple shapes only: database rows, response DTOs, and the tiny records passed
// between services. Behavior lives in SERVICES so these stay easy to scan.
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

public sealed record ImportResponse(
    long JobId,
    string Status,
    IReadOnlyList<string> Files,
    string? BackupFolder,
    int Attempt,
    DateTime? NextAttemptAt,
    string? Error);

public sealed record IndexFileEntry(string FileName, string ExternalSourceRef);

public sealed record KualiDocument(
    string Id,
    string SerialNumber,
    string FirstName,
    string LastName,
    string SchoolId,
    IReadOnlyList<KualiAttachment> Attachments,
    string? RawDataJson = null);

public sealed record KualiAttachment(
    string Id,
    string FieldPath,
    string FileName,
    string Url);

public sealed class ExportCallbackRow
{
    public string CorrelationId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? SignedUrl { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}


public sealed class JobEventRow
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public DateTime At { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? PayloadJson { get; set; }
}
