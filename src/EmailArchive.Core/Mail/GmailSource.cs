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
    private uint? _allMailUidValidity;
    private uint? _trashUidValidity;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _client = null;
        _allMail = null;
        _trash = null;

        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(settings.Address, appPassword, ct);

            RequireCapability(client, ImapCapabilities.GMailExt1, "Gmail extensions (X-GM-EXT-1)", "Check Gmail:ImapHost.");
            RequireCapability(client, ImapCapabilities.Move, "MOVE", "Trashing without MOVE could permanently delete messages.");
            RequireCapability(client, ImapCapabilities.UidPlus, "UIDPLUS", "Purging by UID needs UIDPLUS.");

            var allMail = client.GetFolder(SpecialFolder.All)
                ?? throw new InvalidOperationException("Gmail did not expose the All Mail folder. Enable 'Show in IMAP' for All Mail in Gmail label settings.");
            var trash = client.GetFolder(SpecialFolder.Trash)
                ?? throw new InvalidOperationException("Gmail did not expose the Trash folder. Enable 'Show in IMAP' for Trash in Gmail label settings.");

            await allMail.OpenAsync(FolderAccess.ReadWrite, ct);

            if (_allMailUidValidity is { } known && known != allMail.UidValidity)
                throw new InvalidOperationException("Gmail All Mail UIDVALIDITY changed; rerun to list messages again");
            _allMailUidValidity = allMail.UidValidity;

            _client = client;
            _allMail = allMail;
            _trash = trash;
        }
        catch
        {
            client.Dispose();
            throw;
        }

        logger.LogInformation("Connected to Gmail as {Address}", settings.Address);
    }

    public async Task ReconnectAsync(CancellationToken ct)
    {
        await ShutdownAsync(graceful: false, ct);
        await ConnectAsync(ct);
    }

    public async Task<IReadOnlyList<SourceMessageInfo>> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var folder = AllMail;
        // Pre-filter only: DeliveredBefore is date-granular, so ask for a superset; MessageSelector makes the exact cut.
        var uids = await folder.SearchAsync(SearchQuery.DeliveredBefore(cutoff.UtcDateTime.Date.AddDays(1)), ct);
        logger.LogInformation("Gmail All Mail: {Count} candidate messages delivered before {Cutoff:yyyy-MM-dd}", uids.Count, cutoff);

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
        if (moved is not { } trashed) return null;
        _trashUidValidity ??= trashed.Validity;
        return trashed.Id;
    }

    public async Task PurgeAsync(uint trashUid, CancellationToken ct)
    {
        var trash = Trash;
        try
        {
            await trash.OpenAsync(FolderAccess.ReadWrite, ct);
            if (_trashUidValidity is { } known && known != trash.UidValidity)
                throw new InvalidOperationException("Gmail Trash UIDVALIDITY changed; cannot purge by UID");

            var uid = new UniqueId(trashUid);
            await trash.AddFlagsAsync(uid, MessageFlags.Deleted, true, ct);
            await trash.ExpungeAsync(new[] { uid }, ct);
        }
        finally
        {
            try
            {
                await AllMail.OpenAsync(FolderAccess.ReadWrite, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not reopen All Mail after purge; the next operation will reconnect");
            }
        }
    }

    public ValueTask DisposeAsync() => ShutdownAsync(graceful: true, CancellationToken.None);

    private async ValueTask ShutdownAsync(bool graceful, CancellationToken ct)
    {
        var client = _client;
        _client = null;
        _allMail = null;
        _trash = null;
        if (client is null) return;
        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(graceful, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while disconnecting from Gmail");
        }
        client.Dispose();
    }

    private static void RequireCapability(ImapClient client, ImapCapabilities capability, string name, string hint)
    {
        if (!client.Capabilities.HasFlag(capability))
            throw new InvalidOperationException($"The IMAP server did not advertise {name}. {hint}");
    }

    private IMailFolder AllMail => _allMail ?? throw new InvalidOperationException("Not connected to Gmail");
    private IMailFolder Trash => _trash ?? throw new InvalidOperationException("Not connected to Gmail");

    private static SourceMessageInfo ToInfo(IMessageSummary s)
    {
        var labels = s.GMailLabels ?? Array.Empty<string>();
        var envelope = s.Envelope;
        var flags = s.Flags ?? MessageFlags.None;
        var internalDate = s.InternalDate
            ?? throw new InvalidOperationException($"Gmail did not report INTERNALDATE for UID {s.UniqueId.Id}");
        return new SourceMessageInfo(
            Uid: s.UniqueId.Id,
            MessageId: envelope?.MessageId,
            InternalDate: internalDate,
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
