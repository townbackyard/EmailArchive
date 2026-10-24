using EmailArchive.Core.Config;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EmailArchive.Core.Mail;

/// <summary>
/// Gmail over IMAP with an app password. Lists from [Gmail]/All Mail (which never contains Spam or Trash),
/// trashes by moving to [Gmail]/Trash, and purges by flagging Deleted inside Trash and expunging.
/// </summary>
public sealed class GmailSource(GmailSettings settings, string appPassword, ILogger<GmailSource> logger) : IMailSource
{
    private const int FetchBatchSize = 500;

    private ImapClient? _client;
    private IMailFolder? _allMail;
    private IMailFolder? _trash;

    public async Task ConnectAsync(CancellationToken ct)
    {
        var client = new ImapClient();
        await client.ConnectAsync(settings.ImapHost, settings.ImapPort, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(settings.Address, appPassword, ct);

        if (!client.Capabilities.HasFlag(ImapCapabilities.GMailExt1))
            throw new InvalidOperationException("The IMAP server did not advertise Gmail extensions. Check Gmail:ImapHost.");

        _allMail = client.GetFolder(SpecialFolder.All)
            ?? throw new InvalidOperationException("Gmail did not expose the All Mail folder. Enable 'Show in IMAP' for All Mail in Gmail label settings.");
        _trash = client.GetFolder(SpecialFolder.Trash)
            ?? throw new InvalidOperationException("Gmail did not expose the Trash folder. Enable 'Show in IMAP' for Trash in Gmail label settings.");

        await _allMail.OpenAsync(FolderAccess.ReadWrite, ct);
        _client = client;
        logger.LogInformation("Connected to Gmail as {Address}", settings.Address);
    }

    public async Task ReconnectAsync(CancellationToken ct)
    {
        await DisposeAsync();
        await ConnectAsync(ct);
    }

    public async Task<IReadOnlyList<SourceMessageInfo>> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var folder = AllMail;
        var uids = await folder.SearchAsync(SearchQuery.DeliveredBefore(cutoff.UtcDateTime), ct);
        logger.LogInformation("Gmail All Mail: {Count} messages delivered before {Cutoff:yyyy-MM-dd}", uids.Count, cutoff);

        const MessageSummaryItems items =
            MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate | MessageSummaryItems.Envelope |
            MessageSummaryItems.Size | MessageSummaryItems.Flags | MessageSummaryItems.GMailLabels;

        var result = new List<SourceMessageInfo>(uids.Count);
        foreach (var batch in uids.Chunk(FetchBatchSize))
        {
            var summaries = await folder.FetchAsync(batch, items, ct);
            result.AddRange(summaries.Select(ToInfo));
        }
        return result;
    }

    public Task<MimeMessage> FetchAsync(uint uid, CancellationToken ct) =>
        AllMail.GetMessageAsync(new UniqueId(uid), ct);

    public async Task<uint?> TrashAsync(uint uid, CancellationToken ct)
    {
        var moved = await AllMail.MoveToAsync(new UniqueId(uid), Trash, ct);
        return moved?.Id;
    }

    public async Task PurgeAsync(uint trashUid, CancellationToken ct)
    {
        var trash = Trash;
        await trash.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var uid = new UniqueId(trashUid);
            await trash.AddFlagsAsync(uid, MessageFlags.Deleted, true, ct);
            await trash.ExpungeAsync(new[] { uid }, ct);
        }
        finally
        {
            await AllMail.OpenAsync(FolderAccess.ReadWrite, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null) return;
        try
        {
            if (_client.IsConnected) await _client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while disconnecting from Gmail");
        }
        _client.Dispose();
        _client = null;
        _allMail = null;
        _trash = null;
    }

    private IMailFolder AllMail => _allMail ?? throw new InvalidOperationException("Not connected to Gmail");
    private IMailFolder Trash => _trash ?? throw new InvalidOperationException("Not connected to Gmail");

    private static SourceMessageInfo ToInfo(IMessageSummary s)
    {
        var labels = s.GMailLabels ?? Array.Empty<string>();
        var envelope = s.Envelope;
        var flags = s.Flags ?? MessageFlags.None;
        return new SourceMessageInfo(
            Uid: s.UniqueId.Id,
            MessageId: envelope?.MessageId,
            InternalDate: s.InternalDate ?? envelope?.Date ?? DateTimeOffset.MinValue,
            HeaderDate: envelope?.Date,
            From: envelope?.From?.Mailboxes.FirstOrDefault()?.Address ?? "",
            Subject: envelope?.Subject ?? "",
            Size: s.Size ?? 0,
            IsSent: GmailLabels.IsSent(labels),
            IsChat: GmailLabels.IsChat(labels),
            IsSeen: flags.HasFlag(MessageFlags.Seen),
            IsFlagged: flags.HasFlag(MessageFlags.Flagged));
    }
}
