using EmailArchive.Core.Dedupe;
using EmailArchive.Core.Model;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Dedupe;

public class DuplicatePlanTests
{
    [Fact]
    public void Single_copy_is_not_a_plan()
    {
        var m = Msg(1, "2010-01-01");
        Assert.Null(DuplicatePlan.Create(new[] { Target(m, uid: 5) }, null));
    }

    [Fact]
    public void Lowest_uid_survives_without_a_ledger_row()
    {
        var m = Msg(1, "2010-01-01");
        var plan = DuplicatePlan.Create(new[] { Target(m, uid: 9), Target(m, uid: 3), Target(m, uid: 7) }, null);

        Assert.NotNull(plan);
        Assert.Equal(3u, plan.Keep.Uid);
        Assert.Equal(new uint[] { 7, 9 }, plan.Extras.Select(e => e.Uid));
    }

    [Fact]
    public void Ledger_referenced_copy_survives_even_when_not_lowest()
    {
        var m = Msg(1, "2010-01-01");
        var plan = DuplicatePlan.Create(new[] { Target(m, uid: 3), Target(m, uid: 9) }, ledgerUid: 9);

        Assert.NotNull(plan);
        Assert.Equal(9u, plan.Keep.Uid);
        Assert.Equal(new uint[] { 3 }, plan.Extras.Select(e => e.Uid));
    }

    [Fact]
    public void Ledger_uid_not_among_copies_falls_back_to_lowest()
    {
        var m = Msg(1, "2010-01-01");
        var plan = DuplicatePlan.Create(new[] { Target(m, uid: 3), Target(m, uid: 9) }, ledgerUid: 42);

        Assert.NotNull(plan);
        Assert.Equal(3u, plan.Keep.Uid);
    }

    [Fact]
    public void Copy_with_different_headers_is_not_an_extra()
    {
        var m = Msg(1, "2010-01-01");
        var other = Target(m, uid: 9) with { Subject = "something else" };
        var plan = DuplicatePlan.Create(new[] { Target(m, uid: 3), other, Target(m, uid: 5) }, null);

        Assert.NotNull(plan);
        Assert.Equal(new uint[] { 5 }, plan.Extras.Select(e => e.Uid));
    }

    [Fact]
    public void Only_header_mismatches_means_no_plan()
    {
        var m = Msg(1, "2010-01-01");
        var other = Target(m, uid: 9) with { From = "someone-else@example.com" };
        Assert.Null(DuplicatePlan.Create(new[] { Target(m, uid: 3), other }, null));
    }
}
