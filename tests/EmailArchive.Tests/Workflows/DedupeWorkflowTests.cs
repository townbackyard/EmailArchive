using EmailArchive.Core.Auth;
using EmailArchive.Core.Model;
using EmailArchive.Core.Workflows;
using EmailArchive.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Workflows;

public class DedupeWorkflowTests
{
    private readonly FakeTarget _target = new();
    private readonly InMemoryLedger _ledger = new();

    private DedupeWorkflow Workflow() => new(_target, _ledger, NullLogger<DedupeWorkflow>.Instance);

    [Fact]
    public async Task Dry_run_reports_duplicates_and_changes_nothing()
    {
        var a = Msg(1, "2010-01-01");
        var b = Msg(2, "2010-01-02", sent: true);
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 11));
        _target.Folders[TargetFolder.Sent].Add(Target(b, TargetFolder.Sent, uid: 20));
        _target.Folders[TargetFolder.Sent].Add(Target(b, TargetFolder.Sent, uid: 21));
        _target.Folders[TargetFolder.Sent].Add(Target(b, TargetFolder.Sent, uid: 22));

        var summary = await Workflow().RunAsync(apply: false, CancellationToken.None);

        Assert.Equal(new DedupeSummary(2, 3, 0, 0, false), summary);
        Assert.Empty(_target.DeletedItems);
        Assert.Equal(0, summary.ExitCode);
        var run = Assert.Single(_ledger.Runs);
        Assert.Equal(RunMode.Dedupe, run.Mode);
        Assert.Equal(2, run.Selected);
        Assert.Equal(3, run.DuplicatesRemoved);
    }

    [Fact]
    public async Task Apply_moves_extras_and_keeps_one_copy_per_message()
    {
        var a = Msg(1, "2010-01-01");
        var single = Msg(3, "2010-01-03");
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 11));
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(single, uid: 30));

        var summary = await Workflow().RunAsync(apply: true, CancellationToken.None);

        Assert.Equal(new DedupeSummary(1, 1, 0, 0, true), summary);
        Assert.Equal(new[] { (TargetFolder.Archive, 11u) }, _target.DeletedItems);
        Assert.Equal(new uint[] { 10, 30 }, _target.Folders[TargetFolder.Archive].Select(m => m.Uid).OrderBy(u => u));
    }

    [Fact]
    public async Task Ledger_referenced_copy_survives()
    {
        var a = Msg(1, "2010-01-01");
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 11));
        _ledger.Seed(a, MessageState.Trashed, r => { r.TargetFolder = TargetFolder.Archive; r.TargetUid = 11; });

        await Workflow().RunAsync(apply: true, CancellationToken.None);

        Assert.Equal(new[] { (TargetFolder.Archive, 10u) }, _target.DeletedItems);
        Assert.Equal(11u, Assert.Single(_target.Folders[TargetFolder.Archive]).Uid);
    }

    [Fact]
    public async Task Ledger_row_for_the_other_folder_does_not_pick_the_survivor()
    {
        var a = Msg(1, "2010-01-01");
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 11));
        _ledger.Seed(a, MessageState.Trashed, r => { r.TargetFolder = TargetFolder.Sent; r.TargetUid = 11; });

        await Workflow().RunAsync(apply: true, CancellationToken.None);

        Assert.Equal(new[] { (TargetFolder.Archive, 11u) }, _target.DeletedItems);
    }

    [Fact]
    public async Task Differing_bodies_are_kept_and_reported()
    {
        var a = Msg(1, "2010-01-01");
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 11));
        _target.Bodies[11] = "edited";

        var summary = await Workflow().RunAsync(apply: true, CancellationToken.None);

        Assert.Equal(new DedupeSummary(1, 0, 1, 0, true), summary);
        Assert.Empty(_target.DeletedItems);
    }

    [Fact]
    public async Task Same_message_id_with_different_headers_is_left_alone()
    {
        var a = Msg(1, "2010-01-01");
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 11) with { Subject = "forwarded" });

        var summary = await Workflow().RunAsync(apply: true, CancellationToken.None);

        Assert.Equal(0, summary.Groups);
        Assert.Empty(_target.DeletedItems);
    }

    [Fact]
    public async Task Messages_without_message_id_are_never_grouped()
    {
        var a = Msg(1, "2010-01-01", messageId: null);
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 11));

        var summary = await Workflow().RunAsync(apply: true, CancellationToken.None);

        Assert.Equal(0, summary.Groups);
        Assert.Empty(_target.DeletedItems);
    }

    [Fact]
    public async Task Failed_removal_gives_exit_code_1()
    {
        var a = Msg(1, "2010-01-01");
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 11));
        _target.MoveToDeletedFailure = new IOException("boom");

        var summary = await Workflow().RunAsync(apply: true, CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.ExitCode);
        Assert.Equal(1, _ledger.Runs[0].ExitCode);
    }

    [Fact]
    public async Task Reconnects_after_a_failed_group()
    {
        var a = Msg(1, "2010-01-01");
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(a, uid: 11));
        _target.MoveToDeletedFailure = new IOException("connection dropped");

        await Workflow().RunAsync(apply: true, CancellationToken.None);

        Assert.Equal(1, _target.ReconnectCount);
    }

    [Fact]
    public async Task Auth_failure_propagates_and_records_exit_3()
    {
        _target.Folders[TargetFolder.Archive].Add(Target(Msg(1, "2010-01-01"), uid: 10));
        _target.Folders[TargetFolder.Archive].Add(Target(Msg(1, "2010-01-01"), uid: 11));
        _target.BodyHashFailure = new AuthRequiredException("token expired");

        await Assert.ThrowsAsync<AuthRequiredException>(() => Workflow().RunAsync(apply: true, CancellationToken.None));

        Assert.Equal(3, _ledger.Runs[0].ExitCode);
    }
}
