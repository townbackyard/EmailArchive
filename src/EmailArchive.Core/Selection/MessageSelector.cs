using EmailArchive.Core.Model;

namespace EmailArchive.Core.Selection;

public sealed record SelectionOptions(DateTimeOffset Cutoff, int? MaxMessages, long? MaxBytes, bool ExcludeChats);

public sealed record SelectedMessage(SourceMessageInfo Message, MessageKey Key);

public static class MessageSelector
{
    /// <param name="completed">Ledger key value to the Gmail UID already trashed or purged for that key.</param>
    public static IReadOnlyList<SelectedMessage> Select(
        IEnumerable<SourceMessageInfo> candidates,
        SelectionOptions options,
        IReadOnlyDictionary<string, uint> completed)
    {
        var result = new List<SelectedMessage>();
        long bytes = 0;

        var ordered = candidates
            .Where(m => m.InternalDate < options.Cutoff)
            .Where(m => !(options.ExcludeChats && m.IsChat))
            .OrderBy(m => m.InternalDate)
            .ThenBy(m => m.Uid);

        foreach (var message in ordered)
        {
            var key = MessageKeyBuilder.For(message);
            if (completed.TryGetValue(key.Value, out var doneUid) && doneUid == message.Uid)
                continue;

            if (options.MaxMessages is int max && result.Count >= max)
                break;
            if (options.MaxBytes is long maxBytes && result.Count > 0 && bytes + message.Size > maxBytes)
                break;

            result.Add(new SelectedMessage(message, key));
            bytes += message.Size;
        }

        return result;
    }
}
