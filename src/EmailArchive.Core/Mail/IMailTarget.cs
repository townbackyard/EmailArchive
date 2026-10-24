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
    Task<IReadOnlyList<TargetMessageInfo>> ListAllAsync(TargetFolder folder, CancellationToken ct);
}
