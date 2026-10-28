using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Core.Mail;

public interface IMailTarget : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Task ReconnectAsync(CancellationToken ct);
    Task EnsureFoldersAsync(CancellationToken ct);
    Task<uint?> AppendAsync(TargetFolder folder, MimeMessage message, DateTimeOffset internalDate, bool seen, bool flagged, CancellationToken ct);
    Task<IReadOnlyList<TargetMessageInfo>> FindByMessageIdAsync(TargetFolder folder, string messageId, CancellationToken ct);
    Task<IReadOnlyList<TargetMessageInfo>> FindByFallbackAsync(TargetFolder folder, DateTimeOffset? headerDate, string from, string subject, CancellationToken ct);
    /// <summary>Fetches one message's headers by UID; null when the UID does not exist. Not index-dependent.</summary>
    Task<TargetMessageInfo?> GetByUidAsync(TargetFolder folder, uint uid, CancellationToken ct);
    Task<IReadOnlyList<TargetMessageInfo>> ListAllAsync(TargetFolder folder, CancellationToken ct);
    /// <summary>SHA-256 of the raw message bytes after the header block (IMAP BODY[TEXT]); null when the UID does not exist.</summary>
    Task<string?> GetBodyHashAsync(TargetFolder folder, uint uid, CancellationToken ct);
    /// <summary>Moves one message to the mailbox's Deleted Items folder. Never expunges.</summary>
    Task MoveToDeletedItemsAsync(TargetFolder folder, uint uid, CancellationToken ct);
}
