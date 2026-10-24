using EmailArchive.Core.Model;

namespace EmailArchive.Tests.Model;

public class HeaderNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("  Re:   hello\r\n world ", "Re: hello world")]
    public void NormalizeSubject_collapses_whitespace(string? input, string expected)
    {
        Assert.Equal(expected, HeaderNormalizer.NormalizeSubject(input));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Jane Doe <Jane@Example.COM>", "jane@example.com")]
    [InlineData("jane@example.com", "jane@example.com")]
    [InlineData("  JANE@example.com  ", "jane@example.com")]
    public void NormalizeAddress_extracts_lowercase_address(string? input, string expected)
    {
        Assert.Equal(expected, HeaderNormalizer.NormalizeAddress(input));
    }

    [Fact]
    public void DatesMatch_within_two_minutes()
    {
        var a = new DateTimeOffset(2010, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.True(HeaderNormalizer.DatesMatch(a, a.AddSeconds(90)));
        Assert.True(HeaderNormalizer.DatesMatch(a, a.ToOffset(TimeSpan.FromHours(-5))));
        Assert.False(HeaderNormalizer.DatesMatch(a, a.AddMinutes(3)));
    }

    [Fact]
    public void DatesMatch_null_only_matches_null()
    {
        var a = new DateTimeOffset(2010, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.True(HeaderNormalizer.DatesMatch(null, null));
        Assert.False(HeaderNormalizer.DatesMatch(a, null));
        Assert.False(HeaderNormalizer.DatesMatch(null, a));
    }
}
