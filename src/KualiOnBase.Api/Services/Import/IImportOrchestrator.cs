using KualiOnBase.Api.Models;

namespace KualiOnBase.Api.Services.Import;

public interface IImportOrchestrator
{
    Task<ImportOrchestratorResult> RunAsync(ImportJob job, CancellationToken ct);
}

public sealed record ImportOrchestratorResult(
    IReadOnlyList<string> ProducedFiles,
    string BackupFolder);
