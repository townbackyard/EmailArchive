using EmailArchive.Core.Model;

namespace EmailArchive.Core.Ledger;

/// <summary>Pure ledger transitions shared by the SQL implementation and test fakes.</summary>
public static class LedgerRules
{
    public static LedgerMessage NewFromSource(MessageKey key, SourceMessageInfo source, int runId) => new()
    {
        KeyType = key.Type,
        Key = key.Value,
        GmailUid = source.Uid,
        InternalDate = source.InternalDate,
        FromAddress = source.From,
        Subject = source.Subject,
        IsSent = source.IsSent,
        SourceSize = source.Size,
        State = MessageState.Pending,
        LastRunId = runId,
    };

    /// <returns>The row to persist, or null when the existing row must not change.</returns>
    public static LedgerMessage? ApplyTarget(LedgerMessage? existing, MessageKey key, TargetMessageInfo target, int runId)
    {
        if (existing is null)
        {
            return new LedgerMessage
            {
                KeyType = key.Type,
                Key = key.Value,
                InternalDate = target.HeaderDate,
                FromAddress = target.From,
                Subject = target.Subject,
                IsSent = target.Folder == TargetFolder.Sent,
                TargetFolder = target.Folder,
                TargetUid = target.Uid,
                TargetSize = target.Size,
                State = MessageState.AlreadyInTarget,
                LastRunId = runId,
            };
        }

        if (existing.State is not (MessageState.Pending or MessageState.Failed))
            return null;

        existing.TargetFolder = target.Folder;
        existing.TargetUid = target.Uid;
        existing.TargetSize = target.Size;
        existing.State = MessageState.AlreadyInTarget;
        existing.FailureReason = null;
        existing.LastRunId = runId;
        return existing;
    }
}
