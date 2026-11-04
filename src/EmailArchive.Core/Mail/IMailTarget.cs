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
    /// <summary>Fingerprint of the decoded MIME content, independent of how the server serialized it; null when the UID does not exist.</summary>
    Task<ContentSummary?> GetContentAsync(TargetFolder folder, uint uid, CancellationToken ct);
    /// <summary>Moves one message to the mailbox's Deleted Items folder. Never expunges.</summary>
    Task MoveToDeletedItemsAsync(TargetFolder folder, uint uid, CancellationToken ct);
    /// <summary>Downloads one full message (headers and body) without marking it read; null when the UID does not exist.</summary>
    Task<MimeMessage?> DownloadAsync(TargetFolder folder, uint uid, CancellationToken ct);
    /// <summary>The raw bytes after the header block (IMAP BODY[TEXT]) without marking the message read; null when the UID does not exist.</summary>
    Task<byte[]?> GetBodyBytesAsync(TargetFolder folder, uint uid, CancellationToken ct);
}
