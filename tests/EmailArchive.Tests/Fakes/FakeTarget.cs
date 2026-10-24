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
    public int ReconnectCount { get; private set; }
    private uint _nextUid = 1;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ReconnectAsync(CancellationToken ct)
    {
        ReconnectCount++;
        return Task.CompletedTask;
    }

    public Task EnsureFoldersAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<uint?> AppendAsync(TargetFolder folder, MimeMessage message, DateTimeOffset internalDate, bool seen, bool flagged, CancellationToken ct)
    {
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
        return Task.FromResult<uint?>(info.Uid);
    }

    public Task<IReadOnlyList<TargetMessageInfo>> FindByMessageIdAsync(TargetFolder folder, string messageId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TargetMessageInfo>>(
            Folders[folder].Where(m => MessageKeyBuilder.NormalizeMessageId(m.MessageId) == messageId).ToList());

    public Task<IReadOnlyList<TargetMessageInfo>> FindByFallbackAsync(TargetFolder folder, DateTimeOffset? headerDate, string from, string subject, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TargetMessageInfo>>(
            Folders[folder].Where(m => m.MessageId is null).ToList());

    public Task<IReadOnlyList<TargetMessageInfo>> ListAllAsync(TargetFolder folder, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TargetMessageInfo>>(Folders[folder].ToList());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
