using EmailArchive.Core.Selection;

namespace EmailArchive.Tests.Selection;

public class CutoffCalculatorTests
{
    [Fact]
    public void Cutoff_is_utc_midnight_minus_years()
    {
        var now = new DateTimeOffset(2026, 10, 24, 15, 30, 0, TimeSpan.FromHours(-4));
        var cutoff = CutoffCalculator.Compute(now, 5);
        Assert.Equal(new DateTimeOffset(2021, 10, 24, 0, 0, 0, TimeSpan.Zero), cutoff);
    }

    [Fact]
    public void Zero_years_means_today()
    {
        var now = new DateTimeOffset(2026, 2, 1, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), CutoffCalculator.Compute(now, 0));
    }
}
