using EmailArchive.Core.Ledger;
using EmailArchive.Core.Model;
using Microsoft.EntityFrameworkCore;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Ledger;

/// <summary>
/// Runs only when EMAILARCHIVE_TEST_CONNECTION is set, for example:
/// Server=localhost;Database=EmailArchive_Test;Trusted_Connection=True;TrustServerCertificate=True
/// The database named there is dropped and recreated.
/// </summary>
public class SqlLedgerTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("EMAILARCHIVE_TEST_CONNECTION");

    private static async Task<EmailArchiveDbContext> FreshDbAsync()
    {
        var options = new DbContextOptionsBuilder<EmailArchiveDbContext>().UseSqlServer(Conn).Options;
        var db = new EmailArchiveDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        return db;
    }

    [Fact]
    public async Task Round_trips_message_and_run_rows()
    {
        if (Conn is null) return; // integration test, see class comment

        await using var db = await FreshDbAsync();
        var ledger = new SqlLedger(db);

        var run = await ledger.StartRunAsync(RunMode.Move, new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
        Assert.True(run.Id > 0);

        var m = Msg(42, "2010-01-01");
        var key = MessageKeyBuilder.For(m);
        var row = await ledger.GetOrCreateAsync(key, m, run.Id, CancellationToken.None);
        Assert.Equal(MessageState.Pending, row.State);

        row.State = MessageState.Trashed;
        await ledger.SaveAsync(row, CancellationToken.None);
        Assert.Empty(db.ChangeTracker.Entries());

        var completed = await ledger.CompletedAsync(CancellationToken.None);
        Assert.Equal(42u, completed[key.Value]);

        var again = await ledger.GetOrCreateAsync(key, m, run.Id, CancellationToken.None);
        Assert.Equal(row.Id, again.Id);

        run.Trashed = 1;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await ledger.FinishRunAsync(run, CancellationToken.None);
        Assert.Empty(db.ChangeTracker.Entries());

        // Read back through a separate context so persistence, not the change tracker, is what is proven.
        var readOptions = new DbContextOptionsBuilder<EmailArchiveDbContext>().UseSqlServer(Conn).Options;
        await using var reader = new EmailArchiveDbContext(readOptions);
        Assert.Equal(1, (await reader.Runs.SingleAsync(r => r.Id == run.Id)).Trashed);
        Assert.Equal(MessageState.Trashed, (await reader.Messages.SingleAsync(x => x.Key == key.Value)).State);
    }

    [Fact]
    public async Task RecordTarget_creates_and_then_leaves_row()
    {
        if (Conn is null) return; // integration test, see class comment

        await using var db = await FreshDbAsync();
        var ledger = new SqlLedger(db);
        var run = await ledger.StartRunAsync(RunMode.Reconcile, null, CancellationToken.None);

        var m = Msg(1, "2010-01-01");
        var t = Target(m, uid: 7);
        var key = MessageKeyBuilder.For(t);

        Assert.True(await ledger.RecordTargetAsync(key, t, run.Id, CancellationToken.None));
        var row = await ledger.FindAsync(key, CancellationToken.None);
        Assert.Equal(MessageState.AlreadyInTarget, row!.State);
        Assert.Equal(7, row.TargetUid);

        Assert.False(await ledger.RecordTargetAsync(key, t with { Uid = 8 }, run.Id, CancellationToken.None));
        Assert.Equal(7, (await ledger.FindAsync(key, CancellationToken.None))!.TargetUid);
    }
}
