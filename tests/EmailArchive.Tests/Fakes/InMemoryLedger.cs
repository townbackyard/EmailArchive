using EmailArchive.Core.Ledger;
using EmailArchive.Core.Model;

namespace EmailArchive.Tests.Fakes;

public sealed class InMemoryLedger : ILedger
{
    public Dictionary<string, LedgerMessage> Rows { get; } = new();
    public List<LedgerRun> Runs { get; } = new();
    /// <summary>Every state a row passed through, in order, for assertions on save ordering.</summary>
    public List<(string Key, MessageState State)> SaveLog { get; } = new();
    private int _nextId = 1;

    public Task<LedgerRun> StartRunAsync(RunMode mode, DateTimeOffset? cutoff, CancellationToken ct)
    {
        var run = new LedgerRun { Id = Runs.Count + 1, Mode = mode, StartedAt = DateTimeOffset.UtcNow, CutoffDate = cutoff };
        Runs.Add(run);
        return Task.FromResult(run);
    }

    public Task FinishRunAsync(LedgerRun run, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, uint>> CompletedAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, uint>>(Rows.Values
            .Where(r => r.State is MessageState.Trashed or MessageState.Purged && r.GmailUid is not null)
            .ToDictionary(r => r.Key, r => (uint)r.GmailUid!.Value));

    public Task<LedgerMessage?> FindAsync(MessageKey key, CancellationToken ct) =>
        Task.FromResult(Rows.GetValueOrDefault(key.Value));

    public Task<LedgerMessage> GetOrCreateAsync(MessageKey key, SourceMessageInfo source, int runId, CancellationToken ct)
    {
        if (Rows.TryGetValue(key.Value, out var existing)) return Task.FromResult(existing);
        var row = LedgerRules.NewFromSource(key, source, runId);
        row.Id = _nextId++;
        Rows[key.Value] = row;
        return Task.FromResult(row);
    }

    public Task SaveAsync(LedgerMessage message, CancellationToken ct)
    {
        Rows[message.Key] = message;
        SaveLog.Add((message.Key, message.State));
        return Task.CompletedTask;
    }

    public Task RecordTargetAsync(MessageKey key, TargetMessageInfo target, int runId, CancellationToken ct)
    {
        var existing = Rows.GetValueOrDefault(key.Value);
        var row = LedgerRules.ApplyTarget(existing, key, target, runId);
        if (row is null) return Task.CompletedTask;
        if (row.Id == 0) row.Id = _nextId++;
        Rows[key.Value] = row;
        return Task.CompletedTask;
    }

    /// <summary>Seeds a row as if a previous run left it in the given state.</summary>
    public LedgerMessage Seed(SourceMessageInfo source, MessageState state, Action<LedgerMessage>? tweak = null)
    {
        var key = MessageKeyBuilder.For(source);
        var row = LedgerRules.NewFromSource(key, source, 0);
        row.Id = _nextId++;
        row.State = state;
        tweak?.Invoke(row);
        Rows[key.Value] = row;
        return row;
    }
}
