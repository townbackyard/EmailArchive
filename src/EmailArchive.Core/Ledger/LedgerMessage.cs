using EmailArchive.Core.Model;

namespace EmailArchive.Core.Ledger;

public sealed class LedgerMessage
{
    public int Id { get; set; }
    public KeyType KeyType { get; set; }
    public string Key { get; set; } = "";
    public long? GmailUid { get; set; }
    public long? TrashUid { get; set; }
    public DateTimeOffset? InternalDate { get; set; }
    public string FromAddress { get; set; } = "";
    public string Subject { get; set; } = "";
    public bool IsSent { get; set; }
    public long? SourceSize { get; set; }
    public long? TargetSize { get; set; }
    public TargetFolder? TargetFolder { get; set; }
    public long? TargetUid { get; set; }
    public MessageState State { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? CopiedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? TrashedAt { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }
    public int? LastRunId { get; set; }
}
