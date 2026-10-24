using EmailArchive.Core.Auth;
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Workflows;
using EmailArchive.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Workflows;

public class MoveWorkflowTests
{
    private static readonly DateTimeOffset Cutoff = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeSource _source = new();
    private readonly FakeTarget _target = new();
    private readonly InMemoryLedger _ledger = new();

    private MoveWorkflow Workflow() => new(_source, _target, _ledger, NullLogger<MoveWorkflow>.Instance);

    private static MoveOptions Opts(bool purge = false, int? max = null, int failures = 3) =>
        new(new SelectionOptions(Cutoff, max, null, false), purge, failures);

    private LedgerMessage Row(SourceMessageInfo m) => _ledger.Rows[MessageKeyBuilder.For(m).Value];

    [Fact]
    public async Task Happy_path_copies_verifies_and_trashes()
    {
        var m = Msg(1, "2010-01-01", seen: false, flagged: true);
        _source.Messages.Add(m);

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Selected);
        Assert.Equal(1, summary.Copied);
        Assert.Equal(1, summary.Verified);
        Assert.Equal(1, summary.Trashed);
        Assert.Equal(0, summary.Purged);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.ExitCode);

        var appended = Assert.Single(_target.Appended);
        Assert.Equal(TargetFolder.Archive, appended.Folder);
        Assert.Equal(m.InternalDate, appended.Date);
        Assert.False(appended.Seen);
        Assert.True(appended.Flagged);

        Assert.Equal(new uint[] { 1 }, _source.Trashed);
        Assert.Empty(_source.Purged);

        var row = Row(m);
        Assert.Equal(MessageState.Trashed, row.State);
        Assert.Equal(1000, row.TrashUid);
        Assert.NotNull(row.CopiedAt);
        Assert.NotNull(row.VerifiedAt);
        Assert.NotNull(row.TrashedAt);
        Assert.Equal(new[] { MessageState.Copied, MessageState.Verified, MessageState.Trashed },
            _ledger.SaveLog.Select(s => s.State));
    }

    [Fact]
    public async Task Sent_mail_goes_to_sent_folder()
    {
        _source.Messages.Add(Msg(1, "2010-01-01", sent: true));
        await Workflow().RunAsync(Opts(), CancellationToken.None);
        Assert.Equal(TargetFolder.Sent, _target.Appended.Single().Folder);
        Assert.Single(_target.Folders[TargetFolder.Sent]);
    }

    [Fact]
    public async Task Purge_expunges_after_trash()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        var summary = await Workflow().RunAsync(Opts(purge: true), CancellationToken.None);
        Assert.Equal(1, summary.Purged);
        Assert.Equal(new uint[] { 1000 }, _source.Purged);
        Assert.Equal(MessageState.Purged, Row(_source.Messages[0]).State);
    }

    [Fact]
    public async Task Verify_failure_leaves_gmail_untouched()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _target.AppendTransform = t => t with { Subject = "corrupted" };

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.ExitCode);
        Assert.Empty(_source.Trashed);
        var row = Row(m);
        Assert.Equal(MessageState.Failed, row.State);
        Assert.Equal("candidate headers do not match", row.FailureReason);
    }

    [Fact]
    public async Task Invisible_append_fails_verification()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        _target.AppendIsInvisible = true;
        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);
        Assert.Equal(1, summary.Failed);
        Assert.Equal("not found in target", Row(_source.Messages[0]).FailureReason);
        Assert.Empty(_source.Trashed);
    }

    [Fact]
    public async Task Reconciled_row_skips_copy_and_still_trashes()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        var existing = Target(m, uid: 77);
        _target.Folders[TargetFolder.Archive].Add(existing);
        _ledger.Seed(m, MessageState.Pending, r => { r.TargetFolder = TargetFolder.Archive; r.TargetUid = 77; });

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(0, summary.Copied);
        Assert.Equal(1, summary.AlreadyInTarget);
        Assert.Equal(1, summary.Trashed);
        Assert.Empty(_target.Appended);
        Assert.Equal(new uint[] { 1 }, _source.Trashed);
    }

    [Fact]
    public async Task Resumes_from_Copied_state_without_second_append()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _target.Folders[TargetFolder.Archive].Add(Target(m, uid: 5));
        _ledger.Seed(m, MessageState.Copied, r => { r.TargetFolder = TargetFolder.Archive; r.TargetUid = 5; });

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Empty(_target.Appended);
        Assert.Equal(1, summary.Verified);
        Assert.Equal(1, summary.Trashed);
    }

    [Fact]
    public async Task Resumes_from_Verified_state_straight_to_trash()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _ledger.Seed(m, MessageState.Verified, r => { r.TargetFolder = TargetFolder.Archive; r.TargetUid = 5; });

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Empty(_target.Appended);
        Assert.Equal(0, summary.Verified);
        Assert.Equal(1, summary.Trashed);
        Assert.Equal(new uint[] { 1 }, _source.Trashed);
    }

    [Fact]
    public async Task Failed_row_is_retried_on_next_run()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _ledger.Seed(m, MessageState.Failed, r => r.FailureReason = "earlier");

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Copied);
        Assert.Equal(1, summary.Trashed);
        Assert.Null(Row(m).FailureReason);
    }

    [Fact]
    public async Task Already_trashed_same_uid_is_not_selected()
    {
        var m = Msg(1, "2010-01-01");
        _source.Messages.Add(m);
        _ledger.Seed(m, MessageState.Trashed);

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(0, summary.Selected);
        Assert.Empty(_source.Trashed);
    }

    [Fact]
    public async Task Duplicate_copy_of_trashed_key_is_verified_and_trashed()
    {
        var original = Msg(1, "2010-01-01", messageId: "<dup@x>");
        var copy = Msg(2, "2010-01-01", messageId: "<dup@x>");
        _source.Messages.Add(copy);
        _target.Folders[TargetFolder.Archive].Add(Target(original, uid: 9));
        _ledger.Seed(original, MessageState.Trashed, r => { r.TargetFolder = TargetFolder.Archive; r.TargetUid = 9; });

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Selected);
        Assert.Equal(0, summary.Copied);
        Assert.Equal(1, summary.Trashed);
        Assert.Equal(new uint[] { 2 }, _source.Trashed);
        Assert.Equal(2, Row(copy).GmailUid);
    }

    [Fact]
    public async Task Fetch_failure_marks_failed_and_continues()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        _source.Messages.Add(Msg(2, "2010-01-02"));
        _source.FetchFailure = uid => uid == 1 ? new InvalidOperationException("The message no longer exists") : null;

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Trashed);
        Assert.Equal(new uint[] { 2 }, _source.Trashed);
        Assert.Contains("no longer exists", Row(_source.Messages[0]).FailureReason);
        Assert.Equal(1, _source.ReconnectCount);
        Assert.Equal(1, _target.ReconnectCount);
    }

    [Fact]
    public async Task Three_consecutive_failures_abort()
    {
        for (uint i = 1; i <= 5; i++) _source.Messages.Add(Msg(i, $"2010-01-0{i}"));
        _source.FetchFailure = _ => new IOException("boom");

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Equal(2, summary.ExitCode);
        Assert.Equal(3, summary.Failed);
        Assert.Equal(3, _ledger.Rows.Count);
        Assert.Equal(2, _ledger.Runs.Single().ExitCode);
    }

    [Fact]
    public async Task Failure_counter_resets_after_success()
    {
        for (uint i = 1; i <= 5; i++) _source.Messages.Add(Msg(i, $"2010-01-0{i}"));
        _source.FetchFailure = uid => uid is 1 or 2 or 4 or 5 ? new IOException("boom") : null;

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.False(summary.Aborted);
        Assert.Equal(4, summary.Failed);
        Assert.Equal(1, summary.Trashed);
    }

    [Fact]
    public async Task Auth_required_during_reconnect_propagates()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        _source.Messages.Add(Msg(2, "2010-01-02"));
        _source.FetchFailure = _ => new IOException("dropped");
        _source.ReconnectFailure = new AuthRequiredException("sign in again");

        await Assert.ThrowsAsync<AuthRequiredException>(() => Workflow().RunAsync(Opts(), CancellationToken.None));

        Assert.Empty(_source.Trashed);
        Assert.Equal(3, _ledger.Runs.Single().ExitCode);
    }

    [Fact]
    public async Task Max_messages_limits_the_run()
    {
        for (uint i = 1; i <= 5; i++) _source.Messages.Add(Msg(i, $"2010-01-0{i}"));
        var summary = await Workflow().RunAsync(Opts(max: 2), CancellationToken.None);
        Assert.Equal(2, summary.Selected);
        Assert.Equal(new uint[] { 1, 2 }, _source.Trashed);
    }

    [Fact]
    public async Task Run_row_records_counts()
    {
        _source.Messages.Add(Msg(1, "2010-01-01"));
        await Workflow().RunAsync(Opts(), CancellationToken.None);
        var run = _ledger.Runs.Single();
        Assert.Equal(RunMode.Move, run.Mode);
        Assert.Equal(Cutoff, run.CutoffDate);
        Assert.Equal(1, run.Selected);
        Assert.Equal(1, run.Trashed);
        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task Fallback_key_message_verifies_by_headers()
    {
        var m = Msg(1, "2010-01-01", messageId: null, from: "x@y.z", subject: "no id");
        _source.Messages.Add(m);
        _target.AppendTransform = t => t with { MessageId = null };

        var summary = await Workflow().RunAsync(Opts(), CancellationToken.None);

        Assert.Equal(1, summary.Trashed);
        Assert.Equal(KeyType.Fallback, Row(m).KeyType);
    }
}
