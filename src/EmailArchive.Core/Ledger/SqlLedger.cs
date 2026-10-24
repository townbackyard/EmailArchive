using EmailArchive.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace EmailArchive.Core.Ledger;

public sealed class SqlLedger(EmailArchiveDbContext db) : ILedger
{
    public async Task<LedgerRun> StartRunAsync(RunMode mode, DateTimeOffset? cutoff, CancellationToken ct)
    {
        var run = new LedgerRun { Mode = mode, StartedAt = DateTimeOffset.UtcNow, CutoffDate = cutoff };
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return run;
    }

    public async Task FinishRunAsync(LedgerRun run, CancellationToken ct)
    {
        db.Runs.Update(run);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyDictionary<string, uint>> CompletedAsync(CancellationToken ct)
    {
        var rows = await db.Messages
            .Where(m => (m.State == MessageState.Trashed || m.State == MessageState.Purged) && m.GmailUid != null)
            .Select(m => new { m.Key, m.GmailUid })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (uint)r.GmailUid!.Value);
    }

    public Task<LedgerMessage?> FindAsync(MessageKey key, CancellationToken ct) =>
        db.Messages.SingleOrDefaultAsync(m => m.Key == key.Value, ct);

    public async Task<LedgerMessage> GetOrCreateAsync(MessageKey key, SourceMessageInfo source, int runId, CancellationToken ct)
    {
        var existing = await FindAsync(key, ct);
        if (existing is not null) return existing;

        var row = LedgerRules.NewFromSource(key, source, runId);
        db.Messages.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Do not leave the failed insert tracked, or it would be retried with every later SaveChanges.
            db.Entry(row).State = EntityState.Detached;
            throw;
        }
        db.ChangeTracker.Clear();
        return row;
    }

    public async Task SaveAsync(LedgerMessage message, CancellationToken ct)
    {
        db.Messages.Update(message);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<bool> RecordTargetAsync(MessageKey key, TargetMessageInfo target, int runId, CancellationToken ct)
    {
        var existing = await FindAsync(key, ct);
        var row = LedgerRules.ApplyTarget(existing, key, target, runId);
        if (row is null) return false;
        if (existing is null) db.Messages.Add(row);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return true;
    }
}
