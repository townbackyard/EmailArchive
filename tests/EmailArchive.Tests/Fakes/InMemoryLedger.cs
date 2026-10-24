using EmailArchive.Core.Ledger;
using EmailArchive.Core.Model;

namespace EmailArchive.Tests.Fakes;

/// <summary>
/// Rows holds snapshots: callers receive copies, so a change is only visible in Rows after SaveAsync.
/// </summary>
public sealed class InMemoryLedger : ILedger
{
    public Dictionary<string, LedgerMessage> Rows { get; } = new();
    public List<LedgerRun> Runs { get; } = new();
    /// <summary>Every state a row passed through, in order, for assertions on save ordering.</summary>
    public List<(string Key, MessageState State)> SaveLog { get; } = new();
    /// <summary>Shared call trace; when set, every method appends an entry.</summary>
    public List<string>? Trace { get; set; }
    private int _nextId = 1;

    private static LedgerMessage Clone(LedgerMessage m) => new()
    {
        Id = m.Id,
        KeyType = m.KeyType,
        Key = m.Key,
        GmailUid = m.GmailUid,
        TrashUid = m.TrashUid,
        InternalDate = m.InternalDate,
        FromAddress = m.FromAddress,
        Subject = m.Subject,
        IsSent = m.IsSent,
        SourceSize = m.SourceSize,
        TargetSize = m.TargetSize,
        TargetFolder = m.TargetFolder,
        TargetUid = m.TargetUid,
        State = m.State,
        FailureReason = m.FailureReason,
        CopiedAt = m.CopiedAt,
        VerifiedAt = m.VerifiedAt,
        TrashedAt = m.TrashedAt,
        PurgedAt = m.PurgedAt,
        LastRunId = m.LastRunId,
    };

    public Task<LedgerRun> StartRunAsync(RunMode mode, DateTimeOffset? cutoff, CancellationToken ct)
    {
        Trace?.Add("ledger.startRun");
        var run = new LedgerRun { Id = Runs.Count + 1, Mode = mode, StartedAt = DateTimeOffset.UtcNow, CutoffDate = cutoff };
        Runs.Add(run);
        return Task.FromResult(run);
    }

    public Task FinishRunAsync(LedgerRun run, CancellationToken ct)
    {
        Trace?.Add("ledger.finishRun");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, uint>> CompletedAsync(CancellationToken ct)
    {
        Trace?.Add("ledger.completed");
        return Task.FromResult<IReadOnlyDictionary<string, uint>>(Rows.Values
            .Where(r => r.State is MessageState.Trashed or MessageState.Purged && r.GmailUid is not null)
            .ToDictionary(r => r.Key, r => (uint)r.GmailUid!.Value));
    }

    public Task<LedgerMessage?> FindAsync(MessageKey key, CancellationToken ct)
    {
        Trace?.Add("ledger.find");
        return Task.FromResult(Rows.TryGetValue(key.Value, out var stored) ? Clone(stored) : null);
    }

    public Task<LedgerMessage> GetOrCreateAsync(MessageKey key, SourceMessageInfo source, int runId, CancellationToken ct)
    {
        Trace?.Add("ledger.getOrCreate");
        if (Rows.TryGetValue(key.Value, out var existing)) return Task.FromResult(Clone(existing));
        var row = LedgerRules.NewFromSource(key, source, runId);
        row.Id = _nextId++;
        Rows[key.Value] = Clone(row);
        return Task.FromResult(row);
    }

    public Task SaveAsync(LedgerMessage message, CancellationToken ct)
    {
        Trace?.Add($"ledger.save:{message.State}");
        Rows[message.Key] = Clone(message);
        SaveLog.Add((message.Key, message.State));
        return Task.CompletedTask;
    }

    public Task<bool> RecordTargetAsync(MessageKey key, TargetMessageInfo target, int runId, CancellationToken ct)
    {
        Trace?.Add("ledger.recordTarget");
        var existing = Rows.TryGetValue(key.Value, out var stored) ? Clone(stored) : null;
        var row = LedgerRules.ApplyTarget(existing, key, target, runId);
        if (row is null) return Task.FromResult(false);
        if (row.Id == 0) row.Id = _nextId++;
        Rows[key.Value] = Clone(row);
        return Task.FromResult(true);
    }

    /// <summary>Seeds a row as if a previous run left it in the given state.</summary>
    public LedgerMessage Seed(SourceMessageInfo source, MessageState state, Action<LedgerMessage>? tweak = null)
    {
        var key = MessageKeyBuilder.For(source);
        var row = LedgerRules.NewFromSource(key, source, 0);
        row.Id = _nextId++;
        row.State = state;
        tweak?.Invoke(row);
        Rows[key.Value] = Clone(row);
        return Clone(row);
    }
}
