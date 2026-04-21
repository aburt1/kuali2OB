using KualiOnBase.Api.Features.Import;
using KualiOnBase.Api.Features.Kuali;

namespace KualiOnBase.Api.Features.Import;

public interface IImportOrchestrator
{
    Task<ImportOrchestratorResult> RunAsync(ImportJob job, CancellationToken ct);
}

public sealed record ImportOrchestratorResult(
    IReadOnlyList<string> ProducedFiles,
    string BackupFolder);
