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

    /// <summary>
    /// Subject comparison for verification. Exchange stores a subject whose characters it cannot map (a curly
    /// apostrophe in a mislabelled charset, for example) with '?' in their place, and a UTF-8 character it decodes as
    /// Latin-1 becomes two or three characters of mojibake. Every non-ASCII character is folded to '?' on both sides
    /// and runs of '?' collapse to one, so one original character matches however many the server made of it.
    /// Message keys keep the exact subject; only matching is lenient.
    /// </summary>
    public static bool SubjectsMatch(string? a, string? b) => Fold(NormalizeSubject(a)) == Fold(NormalizeSubject(b));

    private static string Fold(string subject)
    {
        var sb = new System.Text.StringBuilder(subject.Length);
        foreach (var rune in subject.EnumerateRunes())
        {
            var c = rune.IsAscii ? (char)rune.Value : '?';
            if (c == '?' && sb.Length > 0 && sb[^1] == '?') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

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
