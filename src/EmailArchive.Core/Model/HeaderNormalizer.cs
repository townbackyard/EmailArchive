using System.Text.RegularExpressions;

namespace EmailArchive.Core.Model;

public static partial class HeaderNormalizer
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<([^>]+)>")]
    private static partial Regex AngleAddress();

    public static string NormalizeSubject(string? subject) =>
        string.IsNullOrWhiteSpace(subject) ? "" : Whitespace().Replace(subject.Trim(), " ");

    public static string NormalizeAddress(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return "";
        var match = AngleAddress().Match(from);
        var address = match.Success ? match.Groups[1].Value : from;
        return address.Trim().ToLowerInvariant();
    }

    public static bool DatesMatch(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return Math.Abs((a.Value - b.Value).TotalMinutes) <= 2;
    }
}
