namespace KualiOnBase.Api.Features.Import;

public sealed record ImportResponse(
    long JobId,
    string Status,
    IReadOnlyList<string> Files,
    string? BackupFolder,
    int Attempt,
    DateTime? NextAttemptAt,
    string? Error);
