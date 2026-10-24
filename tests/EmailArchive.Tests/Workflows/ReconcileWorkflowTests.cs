using EmailArchive.Core.Model;
using EmailArchive.Core.Workflows;
using EmailArchive.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Workflows;

public class ReconcileWorkflowTests
{
    private readonly FakeTarget _target = new();
    private readonly InMemoryLedger _ledger = new();

    private ReconcileWorkflow Workflow() => new(_target, _ledger, NullLogger<ReconcileWorkflow>.Instance);

    [Fact]
    public async Task Records_every_target_message_in_both_folders()
    {
        var a = Msg(1, "2010-01-01");
        var b = Msg(2, "2010-01-02", sent: true);
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Sent].Add(Target(b, TargetFolder.Sent, uid: 20));

        var summary = await Workflow().RunAsync(CancellationToken.None);

        Assert.Equal(2, summary.Scanned);
        Assert.Equal(2, summary.Recorded);
        var rowA = _ledger.Rows[MessageKeyBuilder.For(a).Value];
        Assert.Equal(MessageState.AlreadyInTarget, rowA.State);
        Assert.Equal(10, rowA.TargetUid);
        Assert.Equal(TargetFolder.Archive, rowA.TargetFolder);
        var rowB = _ledger.Rows[MessageKeyBuilder.For(b).Value];
        Assert.Equal(TargetFolder.Sent, rowB.TargetFolder);
        Assert.True(rowB.IsSent);
    }

    [Fact]
    public async Task Does_not_downgrade_trashed_rows()
    {
        var a = Msg(1, "2010-01-01");
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _ledger.Seed(a, MessageState.Trashed, r => r.TargetUid = 3);

        var summary = await Workflow().RunAsync(CancellationToken.None);

        var row = _ledger.Rows[MessageKeyBuilder.For(a).Value];
        Assert.Equal(MessageState.Trashed, row.State);
        Assert.Equal(3, row.TargetUid);
        Assert.Equal(1, summary.Scanned);
        Assert.Equal(0, summary.Recorded);
    }

    [Fact]
    public async Task Writes_a_reconcile_run_row()
    {
        await Workflow().RunAsync(CancellationToken.None);
        var run = _ledger.Runs.Single();
        Assert.Equal(RunMode.Reconcile, run.Mode);
        Assert.Null(run.CutoffDate);
        Assert.Equal(0, run.ExitCode);
    }
}
