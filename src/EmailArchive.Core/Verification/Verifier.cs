using EmailArchive.Core.Model;

namespace EmailArchive.Core.Verification;

public sealed record VerifyResult(bool Success, string? Reason, TargetMessageInfo? Match, bool SizeWarning, int MatchCount)
{
    public static VerifyResult Ok(TargetMessageInfo match, bool sizeWarning, int matchCount) => new(true, null, match, sizeWarning, matchCount);
    public static VerifyResult Fail(string reason) => new(false, reason, null, false, 0);
}

public static class Verifier
{
    /// <summary>Sizes differing by more than this fraction are logged, never failed: Exchange re-encodes MIME.</summary>
    public const double SizeWarningRatio = 0.10;

    /// <summary>
    /// Succeeds when at least one candidate matches on Date, From and Subject. Several matching copies
    /// (a manual archive attempt that copied twice, or a lagging search after our own append) still prove
    /// the message is archived; the lowest UID is recorded and <see cref="VerifyResult.MatchCount"/> lets
    /// the caller warn about the extras.
    /// </summary>
    public static VerifyResult Verify(SourceMessageInfo source, IReadOnlyList<TargetMessageInfo> candidates)
    {
        var matches = candidates.Where(c => Matches(source, c)).OrderBy(c => c.Uid).ToList();

        if (matches.Count == 0)
            return VerifyResult.Fail(candidates.Count == 0 ? "not found in target" : "candidate headers do not match");

        var match = matches[0];
        var sizeWarning = source.Size > 0 && Math.Abs(match.Size - source.Size) > source.Size * SizeWarningRatio;
        return VerifyResult.Ok(match, sizeWarning, matches.Count);
    }

    public static bool Matches(SourceMessageInfo source, TargetMessageInfo target) =>
        HeaderNormalizer.DatesMatch(source.HeaderDate, target.HeaderDate)
        && HeaderNormalizer.NormalizeAddress(source.From) == HeaderNormalizer.NormalizeAddress(target.From)
        && HeaderNormalizer.NormalizeSubject(source.Subject) == HeaderNormalizer.NormalizeSubject(target.Subject);
}
