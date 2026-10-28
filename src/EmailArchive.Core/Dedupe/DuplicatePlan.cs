using EmailArchive.Core.Model;
using EmailArchive.Core.Verification;

namespace EmailArchive.Core.Dedupe;

/// <summary>Which copy of a duplicated message survives and which are candidates for removal.</summary>
public sealed record DuplicatePlan(TargetMessageInfo Keep, IReadOnlyList<TargetMessageInfo> Extras)
{
    /// <summary>
    /// Keeps the copy the ledger points at when it is one of <paramref name="copies"/>, otherwise the lowest UID.
    /// Only copies whose Date, From and Subject match the survivor are extras; anything else is left alone.
    /// Returns null when fewer than two copies share the survivor's headers.
    /// </summary>
    public static DuplicatePlan? Create(IReadOnlyList<TargetMessageInfo> copies, long? ledgerUid)
    {
        if (copies.Count < 2) return null;

        var ordered = copies.OrderBy(c => c.Uid).ToList();
        var keep = ledgerUid is long uid ? ordered.FirstOrDefault(c => c.Uid == (uint)uid) ?? ordered[0] : ordered[0];
        var extras = ordered.Where(c => c.Uid != keep.Uid && Verifier.Matches(keep, c)).ToList();
        return extras.Count == 0 ? null : new DuplicatePlan(keep, extras);
    }
}
