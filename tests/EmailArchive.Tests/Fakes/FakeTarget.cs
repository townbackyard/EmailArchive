using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Tests.Fakes;

public sealed class FakeTarget : IMailTarget
{
    public Dictionary<TargetFolder, List<TargetMessageInfo>> Folders { get; } = new()
    {
        [TargetFolder.Archive] = new(),
        [TargetFolder.Sent] = new(),
    };
    public List<(TargetFolder Folder, MimeMessage Message, DateTimeOffset Date, bool Seen, bool Flagged)> Appended { get; } = new();
    /// <summary>Lets a test corrupt what lands in the folder (wrong subject, etc.).</summary>
    public Func<TargetMessageInfo, TargetMessageInfo>? AppendTransform { get; set; }
    /// <summary>Append reports success but nothing becomes searchable.</summary>
    public bool AppendIsInvisible { get; set; }
    public Exception? AppendFailure { get; set; }
    /// <summary>Models a lost APPEND response: the message lands, then the call throws once.</summary>
    public bool AppendThrowsAfterLanding { get; set; }
    public Exception? ReconnectFailure { get; set; }
    public Exception? FindFailure { get; set; }
    public int ReconnectCount { get; private set; }
    /// <summary>Shared call trace; when set, every method appends an entry.</summary>
    public List<string>? Trace { get; set; }
    private uint _nextUid = 1;

    public Task ConnectAsync(CancellationToken ct)
    {
        Trace?.Add("target.connect");
        return Task.CompletedTask;
    }

    public Task ReconnectAsync(CancellationToken ct)
    {
        Trace?.Add("target.reconnect");
        ReconnectCount++;
        if (ReconnectFailure is not null) throw ReconnectFailure;
        return Task.CompletedTask;
    }

    public Task EnsureFoldersAsync(CancellationToken ct)
    {
        Trace?.Add("target.ensure");
        return Task.CompletedTask;
    }

    public Task<uint?> AppendAsync(TargetFolder folder, MimeMessage message, DateTimeOffset internalDate, bool seen, bool flagged, CancellationToken ct)
    {
        Trace?.Add($"target.append:{folder}");
        if (AppendFailure is not null) throw AppendFailure;
        Appended.Add((folder, message, internalDate, seen, flagged));
        var info = new TargetMessageInfo(
            folder,
            _nextUid++,
            message.MessageId,
            message.Date,
            message.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            message.Subject ?? "",
            message.ToString().Length);
        if (AppendTransform is not null) info = AppendTransform(info);
        if (!AppendIsInvisible) Folders[folder].Add(info);
        if (AppendThrowsAfterLanding)
        {
            AppendThrowsAfterLanding = false;
            throw new IOException("connection dropped after append");
        }
        return Task.FromResult<uint?>(info.Uid);
    }

    public Task<IReadOnlyList<TargetMessageInfo>> FindByMessageIdAsync(TargetFolder folder, string messageId, CancellationToken ct)
    {
        Trace?.Add("target.findById");
        if (FindFailure is not null) throw FindFailure;
        return Task.FromResult<IReadOnlyList<TargetMessageInfo>>(
            Folders[folder].Where(m => MessageKeyBuilder.NormalizeMessageId(m.MessageId) == messageId).ToList());
    }

    public Task<IReadOnlyList<TargetMessageInfo>> FindByFallbackAsync(TargetFolder folder, DateTimeOffset? headerDate, string from, string subject, CancellationToken ct)
    {
        Trace?.Add("target.findFallback");
        if (FindFailure is not null) throw FindFailure;
        return Task.FromResult<IReadOnlyList<TargetMessageInfo>>(
            Folders[folder].Where(m =>
                m.MessageId is null
                && HeaderNormalizer.DatesMatch(m.HeaderDate, headerDate)
                && HeaderNormalizer.NormalizeAddress(m.From) == HeaderNormalizer.NormalizeAddress(from)
                && HeaderNormalizer.NormalizeSubject(m.Subject) == HeaderNormalizer.NormalizeSubject(subject)).ToList());
    }

    public Task<IReadOnlyList<TargetMessageInfo>> ListAllAsync(TargetFolder folder, CancellationToken ct)
    {
        Trace?.Add("target.listAll");
        return Task.FromResult<IReadOnlyList<TargetMessageInfo>>(Folders[folder].ToList());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
