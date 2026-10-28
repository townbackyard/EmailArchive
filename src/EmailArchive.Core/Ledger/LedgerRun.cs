using EmailArchive.Core.Model;

namespace EmailArchive.Core.Ledger;

public sealed class LedgerRun
{
    public int Id { get; set; }
    public RunMode Mode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? CutoffDate { get; set; }
    public int Selected { get; set; }
    public int Copied { get; set; }
    public int AlreadyInTarget { get; set; }
    public int Verified { get; set; }
    public int Trashed { get; set; }
    public int Purged { get; set; }
    public int Failed { get; set; }
    /// <summary>Duplicate target copies moved to Deleted Items (move --dedupe and dedupe --apply), or that would have been (dedupe dry run).</summary>
    public int DuplicatesRemoved { get; set; }
    public int? ExitCode { get; set; }
}
