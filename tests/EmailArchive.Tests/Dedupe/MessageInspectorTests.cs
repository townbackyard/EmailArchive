using System.Text;
using EmailArchive.Core.Dedupe;
using MimeKit;

namespace EmailArchive.Tests.Dedupe;

public class MessageInspectorTests
{
    private static MimeMessage Parse(string raw) => MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw)));

    private const string Plain = "From: a@example.com\r\nSubject: hi\r\nContent-Type: text/plain\r\n\r\nhello world\r\n";

    [Fact]
    public void Describes_a_single_text_part()
    {
        var parts = MessageInspector.Describe(Parse(Plain));

        var part = Assert.Single(parts);
        Assert.Equal("1", part.Path);
        Assert.Equal("text/plain", part.ContentType);
        Assert.NotNull(part.DecodedHash);
        Assert.NotNull(part.NormalizedTextHash);
    }

    [Fact]
    public void Describes_multipart_tree_with_attachment()
    {
        const string raw =
            "From: a@example.com\r\nContent-Type: multipart/mixed; boundary=\"B1\"\r\n\r\n" +
            "--B1\r\nContent-Type: text/plain\r\n\r\nbody\r\n" +
            "--B1\r\nContent-Type: application/pdf; name=\"f.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\nAAEC\r\n" +
            "--B1--\r\n";

        var parts = MessageInspector.Describe(Parse(raw));

        Assert.Equal(new[] { "1", "1.1", "1.2" }, parts.Select(p => p.Path));
        Assert.Equal("B1", parts[0].Boundary);
        Assert.Equal("f.pdf", parts[2].FileName);
        Assert.Equal("base64", parts[2].Encoding);
        Assert.Equal(3, parts[2].DecodedSize);
    }

    [Fact]
    public void Same_text_with_different_boundary_and_encoding_compares_as_identical()
    {
        var a = Parse("Content-Type: multipart/alternative; boundary=\"X\"\r\n\r\n--X\r\nContent-Type: text/plain\r\n\r\nhello\r\n--X--\r\n");
        var b = Parse("Content-Type: multipart/alternative; boundary=\"Y\"\r\n\r\n--Y\r\nContent-Type: text/plain\r\nContent-Transfer-Encoding: base64\r\n\r\naGVsbG8=\r\n--Y--\r\n");

        var lines = MessageInspector.Compare(MessageInspector.Describe(a), MessageInspector.Describe(b));

        Assert.Contains("Part 1 multipart/alternative: boundary differs", lines);
        Assert.Contains("Part 1.1 text/plain: transfer encoding differs (default vs base64)", lines);
        Assert.Contains("Part 1.1 text/plain: decoded content identical", lines);
    }

    [Fact]
    public void Rewrapped_text_is_identical_ignoring_whitespace()
    {
        var a = Parse("Content-Type: text/plain\r\n\r\nhello there world\r\n");
        var b = Parse("Content-Type: text/plain\r\n\r\nhello there\r\nworld\r\n");

        var lines = MessageInspector.Compare(MessageInspector.Describe(a), MessageInspector.Describe(b));

        Assert.Contains(lines, l => l.StartsWith("Part 1 text/plain: text identical ignoring whitespace"));
    }

    [Fact]
    public void Different_text_is_reported_as_differing()
    {
        var a = Parse("Content-Type: text/plain\r\n\r\nhello\r\n");
        var b = Parse("Content-Type: text/plain\r\n\r\ngoodbye\r\n");

        var lines = MessageInspector.Compare(MessageInspector.Describe(a), MessageInspector.Describe(b));

        Assert.Contains(lines, l => l.StartsWith("Part 1 text/plain: decoded content DIFFERS"));
    }

    [Fact]
    public void First_difference_reports_offset_and_printable_context()
    {
        var a = Encoding.ASCII.GetBytes("abc\r\ndef");
        var b = Encoding.ASCII.GetBytes("abc\r\ndXf");

        var diff = MessageInspector.FirstDifference(a, b);

        Assert.NotNull(diff);
        Assert.Equal(6, diff.Value.Offset);
        Assert.Equal("abc\\r\\ndef", diff.Value.Left);
        Assert.Equal("abc\\r\\ndXf", diff.Value.Right);
    }

    [Fact]
    public void First_difference_is_null_for_identical_bytes()
    {
        var a = Encoding.ASCII.GetBytes("same");
        Assert.Null(MessageInspector.FirstDifference(a, a));
    }

    [Fact]
    public void First_difference_sees_a_longer_copy_as_differing_at_the_end()
    {
        var a = Encoding.ASCII.GetBytes("same");
        var b = Encoding.ASCII.GetBytes("same plus");
        Assert.Equal(4, MessageInspector.FirstDifference(a, b)!.Value.Offset);
    }
}
