using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Tests.Fakes;

public sealed class FakeSource : IMailSource
{
    public List<SourceMessageInfo> Messages { get; } = new();
    public List<uint> Trashed { get; } = new();
    public List<uint> Purged { get; } = new();
    public Func<uint, Exception?>? FetchFailure { get; set; }
    public Exception? ReconnectFailure { get; set; }
    public int ReconnectCount { get; private set; }
    public uint NextTrashUid { get; set; } = 1000;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ReconnectAsync(CancellationToken ct)
    {
        ReconnectCount++;
        if (ReconnectFailure is not null) throw ReconnectFailure;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SourceMessageInfo>> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceMessageInfo>>(Messages.Where(m => m.InternalDate < cutoff).ToList());

    public Task<MimeMessage> FetchAsync(uint uid, CancellationToken ct)
    {
        if (FetchFailure?.Invoke(uid) is Exception ex) throw ex;
        var info = Messages.First(m => m.Uid == uid);
        return Task.FromResult(TestData.MakeMime(info));
    }

    public Task<uint?> TrashAsync(uint uid, CancellationToken ct)
    {
        Trashed.Add(uid);
        return Task.FromResult<uint?>(NextTrashUid++);
    }

    public Task PurgeAsync(uint trashUid, CancellationToken ct)
    {
        Purged.Add(trashUid);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
