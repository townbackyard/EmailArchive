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
    public Exception? TrashFailure { get; set; }
    public Exception? PurgeFailure { get; set; }
    public int ReconnectCount { get; private set; }
    public uint NextTrashUid { get; set; } = 1000;
    /// <summary>Shared call trace; when set, every method appends an entry.</summary>
    public List<string>? Trace { get; set; }

    public Task ConnectAsync(CancellationToken ct)
    {
        Trace?.Add("source.connect");
        return Task.CompletedTask;
    }

    public Task ReconnectAsync(CancellationToken ct)
    {
        Trace?.Add("source.reconnect");
        ReconnectCount++;
        if (ReconnectFailure is not null) throw ReconnectFailure;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SourceMessageInfo>> ListOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        Trace?.Add("source.list");
        return Task.FromResult<IReadOnlyList<SourceMessageInfo>>(Messages.Where(m => m.InternalDate < cutoff).ToList());
    }

    public Task<MimeMessage> FetchAsync(uint uid, CancellationToken ct)
    {
        Trace?.Add($"source.fetch:{uid}");
        if (FetchFailure?.Invoke(uid) is Exception ex) throw ex;
        var info = Messages.First(m => m.Uid == uid);
        return Task.FromResult(TestData.MakeMime(info));
    }

    public Task<uint?> TrashAsync(uint uid, CancellationToken ct)
    {
        Trace?.Add($"source.trash:{uid}");
        if (TrashFailure is not null) throw TrashFailure;
        Trashed.Add(uid);
        return Task.FromResult<uint?>(NextTrashUid++);
    }

    public Task PurgeAsync(uint trashUid, CancellationToken ct)
    {
        Trace?.Add($"source.purge:{trashUid}");
        if (PurgeFailure is not null) throw PurgeFailure;
        Purged.Add(trashUid);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
