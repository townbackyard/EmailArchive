using EmailArchive.Core.Model;

namespace EmailArchive.Core.Verification;

public sealed record VerifyResult(bool Success, string? Reason, TargetMessageInfo? Match, bool SizeWarning)
{
    public static VerifyResult Ok(TargetMessageInfo match, bool sizeWarning) => new(true, null, match, sizeWarning);
    public static VerifyResult Fail(string reason) => new(false, reason, null, false);
}

public static class Verifier
{
    /// <summary>Sizes differing by more than this fraction are logged, never failed: Exchange re-encodes MIME.</summary>
    public const double SizeWarningRatio = 0.10;

    public static VerifyResult Verify(SourceMessageInfo source, IReadOnlyList<TargetMessageInfo> candidates)
    {
        var matches = candidates.Where(c => Matches(source, c)).ToList();

        if (matches.Count == 0)
            return VerifyResult.Fail(candidates.Count == 0 ? "not found in target" : "candidate headers do not match");
        if (matches.Count > 1)
            return VerifyResult.Fail($"{matches.Count} matching copies in target");

        var match = matches[0];
        var sizeWarning = source.Size > 0 && Math.Abs(match.Size - source.Size) > source.Size * SizeWarningRatio;
        return VerifyResult.Ok(match, sizeWarning);
    }

    public static bool Matches(SourceMessageInfo source, TargetMessageInfo target) =>
        HeaderNormalizer.DatesMatch(source.HeaderDate, target.HeaderDate)
        && HeaderNormalizer.NormalizeAddress(source.From) == HeaderNormalizer.NormalizeAddress(target.From)
        && HeaderNormalizer.NormalizeSubject(source.Subject) == HeaderNormalizer.NormalizeSubject(target.Subject);
}
