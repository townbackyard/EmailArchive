using System.Security.Cryptography;
using System.Text;

namespace EmailArchive.Core.Model;

public sealed record MessageKey(KeyType Type, string Value);

public static class MessageKeyBuilder
{
    /// <summary>Longer Message-IDs do not fit the ledger key column; they use the Fallback hash instead.</summary>
    public const int MaxMessageIdLength = 512;

    public static MessageKey For(SourceMessageInfo m) => Build(m.MessageId, m.HeaderDate, m.From, m.Subject);

    public static MessageKey For(TargetMessageInfo m) => Build(m.MessageId, m.HeaderDate, m.From, m.Subject);

    public static MessageKey Build(string? messageId, DateTimeOffset? headerDate, string from, string subject)
    {
        var id = NormalizeMessageId(messageId);
        // An over-long Message-ID is ignored here (not in NormalizeMessageId) so source and target agree on the Fallback key.
        if (id is not null && id.Length <= MaxMessageIdLength) return new MessageKey(KeyType.MessageId, id);

        var material = string.Join('|',
            headerDate?.ToUniversalTime().ToString("O") ?? "",
            HeaderNormalizer.NormalizeAddress(from),
            HeaderNormalizer.NormalizeSubject(subject));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new MessageKey(KeyType.Fallback, Convert.ToHexString(hash));
    }

    public static string? NormalizeMessageId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().TrimStart('<').TrimEnd('>').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
