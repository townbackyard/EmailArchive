using EmailArchive.Core.Model;

namespace EmailArchive.Core.Ledger;

public interface ILedger
{
    Task<LedgerRun> StartRunAsync(RunMode mode, DateTimeOffset? cutoff, CancellationToken ct);
    Task FinishRunAsync(LedgerRun run, CancellationToken ct);

    /// <summary>Key value to Gmail UID for every row in Trashed or Purged state.</summary>
    Task<IReadOnlyDictionary<string, uint>> CompletedAsync(CancellationToken ct);

    Task<LedgerMessage?> FindAsync(MessageKey key, CancellationToken ct);
    Task<LedgerMessage> GetOrCreateAsync(MessageKey key, SourceMessageInfo source, int runId, CancellationToken ct);
    Task SaveAsync(LedgerMessage message, CancellationToken ct);

    /// <summary>Reconcile upsert: records that a message exists in the target folder. Returns true when a row was created or upgraded to AlreadyInTarget, false when the existing row was left unchanged.</summary>
    Task<bool> RecordTargetAsync(MessageKey key, TargetMessageInfo target, int runId, CancellationToken ct);
}
