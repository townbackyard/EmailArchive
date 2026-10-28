using EmailArchive.Core.Model;

namespace EmailArchive.Core.Verification;

public sealed record VerifyResult(bool Success, string? Reason, TargetMessageInfo? Match, bool SizeWarning, IReadOnlyList<TargetMessageInfo> Matches)
{
    public static VerifyResult Ok(TargetMessageInfo match, bool sizeWarning, IReadOnlyList<TargetMessageInfo> matches) => new(true, null, match, sizeWarning, matches);
    public static VerifyResult Fail(string reason) => new(false, reason, null, false, Array.Empty<TargetMessageInfo>());

    /// <summary>How many target copies matched; more than one means duplicates exist in the target folder.</summary>
    public int MatchCount => Matches.Count;
}

public static class Verifier
{
    /// <summary>Sizes differing by more than this fraction are logged, never failed: Exchange re-encodes MIME.</summary>
    public const double SizeWarningRatio = 0.10;

    /// <summary>
    /// Succeeds when at least one candidate matches on Date, From and Subject. Several matching copies
    /// (a manual archive attempt that copied twice, or a lagging search after our own append) still prove
    /// the message is archived; the lowest UID is recorded and <see cref="VerifyResult.Matches"/> lets
    /// the caller warn about, or remove, the extras.
    /// </summary>
    public static VerifyResult Verify(SourceMessageInfo source, IReadOnlyList<TargetMessageInfo> candidates)
    {
        var matches = candidates.Where(c => Matches(source, c)).OrderBy(c => c.Uid).ToList();

        if (matches.Count == 0)
            return VerifyResult.Fail(candidates.Count == 0 ? "not found in target" : "candidate headers do not match");

        var match = matches[0];
        var sizeWarning = source.Size > 0 && Math.Abs(match.Size - source.Size) > source.Size * SizeWarningRatio;
        return VerifyResult.Ok(match, sizeWarning, matches);
    }

    public static bool Matches(SourceMessageInfo source, TargetMessageInfo target) =>
        HeaderNormalizer.DatesMatch(source.HeaderDate, target.HeaderDate)
        && HeaderNormalizer.NormalizeAddress(source.From) == HeaderNormalizer.NormalizeAddress(target.From)
        && HeaderNormalizer.NormalizeSubject(source.Subject) == HeaderNormalizer.NormalizeSubject(target.Subject);

    /// <summary>Same header test as <see cref="Matches(SourceMessageInfo, TargetMessageInfo)"/>, between two target copies.</summary>
    public static bool Matches(TargetMessageInfo a, TargetMessageInfo b) =>
        HeaderNormalizer.DatesMatch(a.HeaderDate, b.HeaderDate)
        && HeaderNormalizer.NormalizeAddress(a.From) == HeaderNormalizer.NormalizeAddress(b.From)
        && HeaderNormalizer.NormalizeSubject(a.Subject) == HeaderNormalizer.NormalizeSubject(b.Subject);
}
