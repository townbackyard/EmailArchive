using EmailArchive.Core.Model;

namespace EmailArchive.Tests.Model;

public class MessageKeyBuilderTests
{
    private static readonly DateTimeOffset Date = new(2009, 5, 6, 7, 8, 9, TimeSpan.Zero);

    [Fact]
    public void Message_id_key_strips_angle_brackets_and_whitespace()
    {
        var key = MessageKeyBuilder.Build(" <abc@example.com> ", Date, "a@b.c", "s");
        Assert.Equal(KeyType.MessageId, key.Type);
        Assert.Equal("abc@example.com", key.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<>")]
    public void Missing_message_id_uses_fallback_hash(string? messageId)
    {
        var key = MessageKeyBuilder.Build(messageId, Date, "a@b.c", "s");
        Assert.Equal(KeyType.Fallback, key.Type);
        Assert.Equal(64, key.Value.Length);
    }

    [Fact]
    public void Overlong_message_id_falls_back_to_hash()
    {
        var key = MessageKeyBuilder.Build("<" + new string('x', 600) + "@example.com>", Date, "a@b.c", "s");
        Assert.Equal(KeyType.Fallback, key.Type);
        Assert.Equal(64, key.Value.Length);
    }

    [Fact]
    public void Fallback_is_stable_across_header_formatting()
    {
        var k1 = MessageKeyBuilder.Build(null, Date, "Jane <JANE@x.com>", "Hi  there");
        var k2 = MessageKeyBuilder.Build(null, Date.ToOffset(TimeSpan.FromHours(2)), "jane@x.com", "Hi there");
        Assert.Equal(k1.Value, k2.Value);
    }

    [Fact]
    public void Fallback_differs_when_subject_differs()
    {
        var k1 = MessageKeyBuilder.Build(null, Date, "a@b.c", "one");
        var k2 = MessageKeyBuilder.Build(null, Date, "a@b.c", "two");
        Assert.NotEqual(k1.Value, k2.Value);
    }

    [Fact]
    public void Fallback_key_handles_empty_fields()
    {
        var key = MessageKeyBuilder.Build(null, null, "", "");
        Assert.Equal(KeyType.Fallback, key.Type);
        Assert.Equal(64, key.Value.Length);
    }

    [Fact]
    public void Source_and_target_with_same_headers_share_a_key()
    {
        var src = new SourceMessageInfo(1, "<m@x>", Date, Date, "a@b.c", "s", 10, false, false, true, false);
        var tgt = new TargetMessageInfo(TargetFolder.Archive, 9, "m@x", Date, "a@b.c", "s", 12);
        Assert.Equal(MessageKeyBuilder.For(src), MessageKeyBuilder.For(tgt));
    }
}
