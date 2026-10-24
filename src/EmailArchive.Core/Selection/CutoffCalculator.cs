namespace EmailArchive.Core.Selection;

public static class CutoffCalculator
{
    /// <summary>UTC midnight of today's date (in UTC) minus the given number of years.</summary>
    public static DateTimeOffset Compute(DateTimeOffset now, int keepYears)
    {
        var todayUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return todayUtc.AddYears(-keepYears);
    }
}
