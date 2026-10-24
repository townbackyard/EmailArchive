using EmailArchive.Core.Ledger;
using EmailArchive.Core.Model;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Ledger;

public class LedgerRulesTests
{
    [Fact]
    public void NewFromSource_starts_pending_with_source_fields()
    {
        var m = Msg(5, "2010-03-04", from: "a@b.c", subject: "s", size: 123, sent: true);
        var key = MessageKeyBuilder.For(m);
        var row = LedgerRules.NewFromSource(key, m, runId: 9);

        Assert.Equal(MessageState.Pending, row.State);
        Assert.Equal(key.Value, row.Key);
        Assert.Equal(KeyType.MessageId, row.KeyType);
        Assert.Equal(5, row.GmailUid);
        Assert.Equal("a@b.c", row.FromAddress);
        Assert.Equal("s", row.Subject);
        Assert.Equal(123, row.SourceSize);
        Assert.True(row.IsSent);
        Assert.Equal(9, row.LastRunId);
        Assert.Null(row.TargetUid);
    }

    [Fact]
    public void NewFromSource_truncates_long_subject_and_address()
    {
        var m = Msg(5, "2010-03-04", from: new string('a', 600), subject: new string('s', 3000));
        var row = LedgerRules.NewFromSource(MessageKeyBuilder.For(m), m, runId: 1);

        Assert.Equal(2048, row.Subject.Length);
        Assert.Equal(512, row.FromAddress.Length);

        var applied = LedgerRules.ApplyTarget(null, MessageKeyBuilder.For(m), Target(m), runId: 1)!;
        Assert.Equal(2048, applied.Subject.Length);
        Assert.Equal(512, applied.FromAddress.Length);
    }

    [Fact]
    public void ApplyTarget_creates_AlreadyInTarget_row_when_missing()
    {
        var m = Msg(1, "2010-01-01");
        var t = Target(m, TargetFolder.Sent, uid: 77, size: 900);
        var row = LedgerRules.ApplyTarget(null, MessageKeyBuilder.For(t), t, runId: 2);

        Assert.NotNull(row);
        Assert.Equal(MessageState.AlreadyInTarget, row!.State);
        Assert.Equal(TargetFolder.Sent, row.TargetFolder);
        Assert.Equal(77, row.TargetUid);
        Assert.Equal(900, row.TargetSize);
        Assert.True(row.IsSent);
        Assert.Null(row.GmailUid);
    }

    [Theory]
    [InlineData(MessageState.Pending)]
    [InlineData(MessageState.Failed)]
    public void ApplyTarget_upgrades_pending_or_failed_rows(MessageState state)
    {
        var m = Msg(1, "2010-01-01");
        var existing = LedgerRules.NewFromSource(MessageKeyBuilder.For(m), m, 1);
        existing.State = state;
        existing.FailureReason = "old";
        var t = Target(m, uid: 44);

        var row = LedgerRules.ApplyTarget(existing, MessageKeyBuilder.For(t), t, runId: 3);

        Assert.Same(existing, row);
        Assert.Equal(MessageState.AlreadyInTarget, row!.State);
        Assert.Equal(44, row.TargetUid);
        Assert.Null(row.FailureReason);
        Assert.Equal(1, row.GmailUid);
    }

    [Theory]
    [InlineData(MessageState.Copied)]
    [InlineData(MessageState.Verified)]
    [InlineData(MessageState.Trashed)]
    [InlineData(MessageState.Purged)]
    [InlineData(MessageState.AlreadyInTarget)]
    public void ApplyTarget_leaves_other_states_alone(MessageState state)
    {
        var m = Msg(1, "2010-01-01");
        var existing = LedgerRules.NewFromSource(MessageKeyBuilder.For(m), m, 1);
        existing.State = state;
        existing.TargetUid = 10;
        var t = Target(m, uid: 44);

        Assert.Null(LedgerRules.ApplyTarget(existing, MessageKeyBuilder.For(t), t, 3));
        Assert.Equal(10, existing.TargetUid);
    }
}
