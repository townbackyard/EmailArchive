using EmailArchive.Core.Selection;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Selection;

public class MessageSelectorTests
{
    private static readonly DateTimeOffset Cutoff = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, uint> None = new();

    private static SelectionOptions Opts(int? max = null, long? bytes = null, bool excludeChats = false) =>
        new(Cutoff, max, bytes, excludeChats);

    [Fact]
    public void Only_messages_strictly_before_cutoff_are_selected()
    {
        var msgs = new[]
        {
            Msg(1, "2020-12-31T23:59:59Z"),
            Msg(2, "2021-01-01T00:00:00Z"),
            Msg(3, "2021-06-01"),
        };
        var selected = MessageSelector.Select(msgs, Opts(), None);
        Assert.Equal(new uint[] { 1 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void Oldest_first_then_by_uid()
    {
        var msgs = new[]
        {
            Msg(30, "2012-01-01"),
            Msg(10, "2010-01-01"),
            Msg(21, "2011-01-01"),
            Msg(20, "2011-01-01"),
        };
        var selected = MessageSelector.Select(msgs, Opts(), None);
        Assert.Equal(new uint[] { 10, 20, 21, 30 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void MaxMessages_caps_the_count()
    {
        var msgs = Enumerable.Range(1, 10).Select(i => Msg((uint)i, $"2010-01-{i:00}")).ToArray();
        var selected = MessageSelector.Select(msgs, Opts(max: 3), None);
        Assert.Equal(3, selected.Count);
        Assert.Equal(new uint[] { 1, 2, 3 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void MaxBytes_stops_before_exceeding()
    {
        var msgs = new[]
        {
            Msg(1, "2010-01-01", size: 400),
            Msg(2, "2010-01-02", size: 400),
            Msg(3, "2010-01-03", size: 400),
        };
        var selected = MessageSelector.Select(msgs, Opts(bytes: 1000), None);
        Assert.Equal(new uint[] { 1, 2 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void MaxBytes_always_allows_first_message()
    {
        var msgs = new[]
        {
            Msg(1, "2010-01-01", size: 5_000_000),
            Msg(2, "2010-01-02", size: 10),
        };
        var selected = MessageSelector.Select(msgs, Opts(bytes: 1000), None);
        Assert.Equal(new uint[] { 1 }, selected.Select(s => s.Message.Uid));
    }

    [Fact]
    public void Completed_key_with_same_uid_is_skipped()
    {
        var m = Msg(7, "2010-01-01");
        var key = EmailArchive.Core.Model.MessageKeyBuilder.For(m);
        var completed = new Dictionary<string, uint> { [key.Value] = 7 };
        Assert.Empty(MessageSelector.Select(new[] { m }, Opts(), completed));
    }

    [Fact]
    public void Completed_key_with_different_uid_is_still_selected()
    {
        var m = Msg(8, "2010-01-01");
        var key = EmailArchive.Core.Model.MessageKeyBuilder.For(m);
        var completed = new Dictionary<string, uint> { [key.Value] = 7 };
        Assert.Single(MessageSelector.Select(new[] { m }, Opts(), completed));
    }

    [Fact]
    public void Chats_excluded_only_when_requested()
    {
        var msgs = new[] { Msg(1, "2010-01-01", chat: true), Msg(2, "2010-01-02") };
        Assert.Equal(2, MessageSelector.Select(msgs, Opts(), None).Count);
        Assert.Single(MessageSelector.Select(msgs, Opts(excludeChats: true), None));
    }

    [Fact]
    public void Selected_message_carries_its_key()
    {
        var m = Msg(1, "2010-01-01", messageId: "<x@y>");
        var selected = MessageSelector.Select(new[] { m }, Opts(), None);
        Assert.Equal("x@y", selected[0].Key.Value);
    }
}
