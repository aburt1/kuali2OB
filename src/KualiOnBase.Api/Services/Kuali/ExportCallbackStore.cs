using Dapper;
using KualiOnBase.Api.Data;

namespace KualiOnBase.Api.Services.Kuali;

public interface IExportCallbackStore
{
    Task CreatePendingAsync(string correlationId, string documentId, CancellationToken ct);
    Task<ExportCallbackRow?> GetAsync(string correlationId, CancellationToken ct);

    // Returns true iff the row transitioned from Pending to Completed. False means
    // the row was already Completed/Failed — the caller lost the race (legitimate
    // duplicate) or is an attacker trying to overwrite a finalized state.
    Task<bool> MarkCompletedAsync(string correlationId, string signedUrl, CancellationToken ct);
    Task<bool> MarkFailedAsync(string correlationId, string error, CancellationToken ct);

    Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct);
}

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

public sealed class ExportCallbackStore : IExportCallbackStore
{
    private readonly Db _db;

    public ExportCallbackStore(Db db)
    {
        _db = db;
    }

    public async Task CreatePendingAsync(string correlationId, string documentId, CancellationToken ct)
    {
        using var conn = _db.Open();
        var now = DateTime.UtcNow;
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ExportCallbacks
                (CorrelationId, DocumentId, Status, SignedUrl, ErrorMessage, CreatedAt, UpdatedAt)
            VALUES (@CorrelationId, @DocumentId, 'Pending', NULL, NULL, @Now, @Now);
            """,
            new { CorrelationId = correlationId, DocumentId = documentId, Now = now },
            cancellationToken: ct));
    }

    public async Task<ExportCallbackRow?> GetAsync(string correlationId, CancellationToken ct)
    {
        using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<ExportCallbackRow>(new CommandDefinition(
            "SELECT * FROM ExportCallbacks WHERE CorrelationId = @Id;",
            new { Id = correlationId },
            cancellationToken: ct));
    }

    public async Task<bool> MarkCompletedAsync(string correlationId, string signedUrl, CancellationToken ct)
    {
        using var conn = _db.Open();
        // WHERE Status='Pending' is the one-shot guard. If an attacker races to
        // re-POST the callback after we (or they) already finalized it, affected==0
        // and we surface a 409 to the caller without touching the row. Without this
        // guard, any authenticated-by-HMAC caller can overwrite SignedUrl at will.
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ExportCallbacks
               SET Status = 'Completed', SignedUrl = @Url, UpdatedAt = @Now
             WHERE CorrelationId = @Id AND Status = 'Pending';
            """,
            new { Id = correlationId, Url = signedUrl, Now = DateTime.UtcNow },
            cancellationToken: ct));
        return affected == 1;
    }

    public async Task<bool> MarkFailedAsync(string correlationId, string error, CancellationToken ct)
    {
        using var conn = _db.Open();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ExportCallbacks
               SET Status = 'Failed', ErrorMessage = @Error, UpdatedAt = @Now
             WHERE CorrelationId = @Id AND Status = 'Pending';
            """,
            new { Id = correlationId, Error = error, Now = DateTime.UtcNow },
            cancellationToken: ct));
        return affected == 1;
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        using var conn = _db.Open();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ExportCallbacks WHERE CreatedAt < @Cutoff;",
            new { Cutoff = cutoffUtc },
            cancellationToken: ct));
    }
}
