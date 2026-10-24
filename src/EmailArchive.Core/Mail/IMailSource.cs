using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Core.Mail;

public interface IMailSource : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    Task ReconnectAsync(CancellationToken ct);
    Task<IReadOnlyList<SourceMessageInfo>> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);
    Task<MimeMessage> FetchAsync(uint uid, CancellationToken ct);
    /// <returns>The message's UID inside the Trash folder when the server reports it.</returns>
    Task<uint?> TrashAsync(uint uid, CancellationToken ct);
    Task PurgeAsync(uint trashUid, CancellationToken ct);
}
