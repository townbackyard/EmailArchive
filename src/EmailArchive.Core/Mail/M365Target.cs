using EmailArchive.Core.Auth;
using EmailArchive.Core.Config;
using EmailArchive.Core.Model;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EmailArchive.Core.Mail;

/// <summary>
/// Exchange Online over IMAP with OAuth2 (SASL XOAUTH2). Folder paths in settings use '/' and are created on demand
/// under the personal namespace. Exchange indexes appended mail asynchronously, so header searches retry briefly.
/// </summary>
public sealed class M365Target(
    Microsoft365Settings settings,
    MicrosoftAuth auth,
    ILogger<M365Target> logger,
    TimeSpan? searchRetryDelay = null) : IMailTarget
{
    private const int FetchBatchSize = 500;
    private const int SearchAttempts = 3;
    private readonly TimeSpan _searchRetryDelay = searchRetryDelay ?? TimeSpan.FromSeconds(2);

    private ImapClient? _client;
    private IMailFolder? _archive;
    private IMailFolder? _sent;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _client = null;
        _archive = null;
        _sent = null;

        var (username, token) = await auth.AcquireTokenSilentAsync(ct);
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(username, token), ct);
            _client = client;
        }
        catch (AuthenticationException ex)
        {
            client.Dispose();
            throw new AuthRequiredException("Microsoft 365 rejected the sign-in token. Run: EmailArchive login", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        logger.LogInformation("Connected to Microsoft 365 as {Username}", username);
    }

    public async Task ReconnectAsync(CancellationToken ct)
    {
        await ShutdownAsync(graceful: false, ct);
        await ConnectAsync(ct);
        await EnsureFoldersAsync(ct);
    }

    public async Task EnsureFoldersAsync(CancellationToken ct)
    {
        _archive = await EnsurePathAsync(settings.ArchiveFolder, ct);
        _sent = await EnsurePathAsync(settings.SentFolder, ct);
    }

    public async Task<uint?> AppendAsync(TargetFolder folder, MimeMessage message, DateTimeOffset internalDate, bool seen, bool flagged, CancellationToken ct)
    {
        var flags = (seen ? MessageFlags.Seen : MessageFlags.None) | (flagged ? MessageFlags.Flagged : MessageFlags.None);
        var uid = await Resolve(folder).AppendAsync(message, flags, internalDate, ct);
        return uid?.Id;
    }

    public async Task<IReadOnlyList<TargetMessageInfo>> FindByMessageIdAsync(TargetFolder folder, string messageId, CancellationToken ct)
    {
        var mailFolder = Resolve(folder);
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, ct);

        // HEADER search is a substring match; keep only exact Message-ID matches, and retry while none match.
        for (var attempt = 1; ; attempt++)
        {
            var uids = await mailFolder.SearchAsync(SearchQuery.HeaderContains("Message-Id", messageId), ct);
            var exact = (await SummarizeAsync(mailFolder, folder, uids, ct))
                .Where(s => string.Equals(MessageKeyBuilder.NormalizeMessageId(s.MessageId), messageId, StringComparison.Ordinal))
                .ToList();
            if (exact.Count > 0 || attempt >= SearchAttempts)
                return exact;

            logger.LogDebug("Message-Id {MessageId} not yet searchable in {Folder}, retrying", messageId, mailFolder.FullName);
            await Task.Delay(_searchRetryDelay, ct);
        }
    }

    public async Task<IReadOnlyList<TargetMessageInfo>> FindByFallbackAsync(TargetFolder folder, DateTimeOffset? headerDate, string from, string subject, CancellationToken ct)
    {
        // Nothing to search on: an unfiltered search would match the whole folder.
        if (headerDate is null && string.IsNullOrWhiteSpace(HeaderNormalizer.NormalizeAddress(from)) && string.IsNullOrWhiteSpace(HeaderNormalizer.NormalizeSubject(subject)))
            return Array.Empty<TargetMessageInfo>();

        var mailFolder = Resolve(folder);
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, ct);

        SearchQuery query = SearchQuery.All;
        if (headerDate is DateTimeOffset date)
            query = query.And(SearchQuery.SentSince(date.UtcDateTime.AddDays(-1))).And(SearchQuery.SentBefore(date.UtcDateTime.AddDays(2)));
        if (!string.IsNullOrWhiteSpace(subject))
            query = query.And(SearchQuery.SubjectContains(subject));
        if (!string.IsNullOrWhiteSpace(from))
            query = query.And(SearchQuery.FromContains(HeaderNormalizer.NormalizeAddress(from)));

        var uids = await SearchWithRetryAsync(mailFolder, query, "fallback match", ct);
        return await SummarizeAsync(mailFolder, folder, uids, ct);
    }

    public async Task<TargetMessageInfo?> GetByUidAsync(TargetFolder folder, uint uid, CancellationToken ct)
    {
        var mailFolder = Resolve(folder);
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, ct);
        var result = await SummarizeAsync(mailFolder, folder, new[] { new UniqueId(uid) }, ct);
        return result.Count > 0 ? result[0] : null;
    }

    public async Task<IReadOnlyList<TargetMessageInfo>> ListAllAsync(TargetFolder folder, CancellationToken ct)
    {
        var mailFolder = Resolve(folder);
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, ct);
        var uids = await mailFolder.SearchAsync(SearchQuery.All, ct);
        return await SummarizeAsync(mailFolder, folder, uids, ct);
    }

    public ValueTask DisposeAsync() => ShutdownAsync(graceful: true, CancellationToken.None);

    private async ValueTask ShutdownAsync(bool graceful, CancellationToken ct)
    {
        var client = _client;
        _client = null;
        _archive = null;
        _sent = null;
        if (client is null) return;
        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(graceful, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring error while disconnecting from Microsoft 365");
        }
        client.Dispose();
    }

    private async Task<IList<UniqueId>> SearchWithRetryAsync(IMailFolder mailFolder, SearchQuery query, string what, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var uids = await mailFolder.SearchAsync(query, ct);
            if (uids.Count > 0 || attempt >= SearchAttempts)
                return uids;

            logger.LogDebug("{What} not yet searchable in {Folder}, retrying", what, mailFolder.FullName);
            await Task.Delay(_searchRetryDelay, ct);
        }
    }

    private async Task<IMailFolder> EnsurePathAsync(string path, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to Microsoft 365");
        if (client.PersonalNamespaces.Count == 0)
            throw new InvalidOperationException("Microsoft 365 did not report a personal IMAP namespace");
        IMailFolder folder = client.GetFolder(client.PersonalNamespaces[0])
            ?? throw new InvalidOperationException("Microsoft 365 did not expose its personal IMAP namespace root");

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IMailFolder? next = null;
            try
            {
                next = await folder.GetSubfolderAsync(segment, ct);
            }
            catch (FolderNotFoundException)
            {
                logger.LogInformation("Creating folder {Segment} under {Parent}", segment, folder.FullName);
            }
            folder = next ?? await folder.CreateAsync(segment, true, ct)
                ?? throw new InvalidOperationException($"Microsoft 365 did not create folder {segment}");
        }
        return folder;
    }

    private IMailFolder Resolve(TargetFolder folder) => folder switch
    {
        TargetFolder.Archive => _archive ?? throw new InvalidOperationException("Folders not ensured"),
        TargetFolder.Sent => _sent ?? throw new InvalidOperationException("Folders not ensured"),
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };

    private static async Task<IReadOnlyList<TargetMessageInfo>> SummarizeAsync(IMailFolder mailFolder, TargetFolder folder, IList<UniqueId> uids, CancellationToken ct)
    {
        const MessageSummaryItems items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Size;
        var result = new List<TargetMessageInfo>(uids.Count);
        foreach (var batch in uids.Chunk(FetchBatchSize))
        {
            var summaries = await mailFolder.FetchAsync(batch, items, ct);
            result.AddRange(summaries.Select(s => new TargetMessageInfo(
                folder,
                s.UniqueId.Id,
                s.Envelope?.MessageId,
                s.Envelope?.Date,
                s.Envelope?.From?.Mailboxes.FirstOrDefault()?.Address ?? "",
                s.Envelope?.Subject ?? "",
                s.Size ?? 0)));
        }
        return result;
    }
}
