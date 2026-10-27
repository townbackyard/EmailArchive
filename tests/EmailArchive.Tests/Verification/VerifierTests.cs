using EmailArchive.Core.Model;
using EmailArchive.Core.Verification;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Verification;

public class VerifierTests
{
    [Fact]
    public void Exact_match_succeeds()
    {
        var src = Msg(1, "2010-01-01", size: 1000);
        var result = Verifier.Verify(src, new[] { Target(src, size: 1050) });
        Assert.True(result.Success);
        Assert.False(result.SizeWarning);
        Assert.Equal(500u, result.Match!.Uid);
    }

    [Fact]
    public void No_candidates_fails_with_not_found()
    {
        var src = Msg(1, "2010-01-01");
        var result = Verifier.Verify(src, Array.Empty<TargetMessageInfo>());
        Assert.False(result.Success);
        Assert.Equal("not found in target", result.Reason);
    }

    [Fact]
    public void Candidate_with_different_subject_fails()
    {
        var src = Msg(1, "2010-01-01", subject: "A");
        var bad = Target(src) with { Subject = "B" };
        var result = Verifier.Verify(src, new[] { bad });
        Assert.False(result.Success);
        Assert.Equal("candidate headers do not match", result.Reason);
    }

    [Fact]
    public void Two_matching_copies_verify_against_lowest_uid_and_report_count()
    {
        var src = Msg(1, "2010-01-01");
        var result = Verifier.Verify(src, new[] { Target(src, uid: 7), Target(src, uid: 2) });
        Assert.True(result.Success);
        Assert.Equal(2u, result.Match!.Uid);
        Assert.Equal(2, result.MatchCount);
    }

    [Fact]
    public void Single_match_reports_count_of_one()
    {
        var src = Msg(1, "2010-01-01");
        var result = Verifier.Verify(src, new[] { Target(src, uid: 1) });
        Assert.True(result.Success);
        Assert.Equal(1, result.MatchCount);
    }

    [Fact]
    public void Header_formatting_differences_still_match()
    {
        var src = Msg(1, "2010-01-01T10:00:00Z", from: "Jane <jane@x.com>", subject: "Re:  hi");
        var tgt = Target(src) with
        {
            From = "jane@x.com",
            Subject = "Re: hi",
            HeaderDate = new DateTimeOffset(2010, 1, 1, 5, 0, 30, TimeSpan.FromHours(-5)),
        };
        Assert.True(Verifier.Verify(src, new[] { tgt }).Success);
    }

    [Fact]
    public void Size_difference_over_ten_percent_warns_but_succeeds()
    {
        var src = Msg(1, "2010-01-01", size: 1000);
        var result = Verifier.Verify(src, new[] { Target(src, size: 1200) });
        Assert.True(result.Success);
        Assert.True(result.SizeWarning);
    }

    [Fact]
    public void Empty_from_and_subject_match()
    {
        var src = Msg(1, "2010-01-01", from: "", subject: "");
        var result = Verifier.Verify(src, new[] { Target(src) });
        Assert.True(result.Success);
    }

    [Fact]
    public void Null_header_date_on_both_sides_matches()
    {
        var src = Msg(1, "2010-01-01") with { HeaderDate = null };
        var result = Verifier.Verify(src, new[] { Target(src) with { HeaderDate = null } });
        Assert.True(result.Success);
    }
}
