using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EmailArchive.Core.Model;
using MimeKit;

namespace EmailArchive.Core.Dedupe;

/// <summary>One MIME part of a message, with hashes of its decoded content.</summary>
/// <param name="Path">Position in the part tree, e.g. "1.2".</param>
/// <param name="ContentType">Media type, e.g. text/html.</param>
/// <param name="Boundary">Multipart boundary, null for leaf parts.</param>
/// <param name="FileName">Attachment file name, if any.</param>
/// <param name="Encoding">Content-Transfer-Encoding as stored.</param>
/// <param name="DecodedSize">Size of the decoded content in bytes.</param>
/// <param name="DecodedHash">SHA-256 of the decoded content; for text parts, of the UTF-8 text.</param>
/// <param name="NormalizedTextHash">For text parts, SHA-256 of the text with all whitespace removed; null otherwise.</param>
public sealed record PartInfo(
    string Path,
    string ContentType,
    string? Boundary,
    string? FileName,
    string Encoding,
    long DecodedSize,
    string? DecodedHash,
    string? NormalizedTextHash);

/// <summary>
/// Diagnostic support for the inspect command: describes a message's MIME structure so two stored copies can be
/// compared below the raw-byte level. Pure functions over already-downloaded data; never talks to a server.
/// </summary>
public static class MessageInspector
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static IReadOnlyList<PartInfo> Describe(MimeMessage message)
    {
        var parts = new List<PartInfo>();
        if (message.Body is not null)
            Walk(message.Body, "1", parts);
        return parts;
    }

    /// <summary>Compares two part lists position by position and explains each difference in one line.</summary>
    public static IReadOnlyList<string> Compare(IReadOnlyList<PartInfo> a, IReadOnlyList<PartInfo> b)
    {
        var lines = new List<string>();
        if (a.Count != b.Count)
            lines.Add($"Part count differs: {a.Count} vs {b.Count}");

        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var (x, y) = (a[i], b[i]);
            var label = $"Part {x.Path} {x.ContentType}";
            if (x.ContentType != y.ContentType)
            {
                lines.Add($"{label}: content type differs ({x.ContentType} vs {y.ContentType})");
                continue;
            }
            if (x.Boundary is not null && x.Boundary != y.Boundary)
                lines.Add($"{label}: boundary differs");
            if (x.Encoding != y.Encoding)
                lines.Add($"{label}: transfer encoding differs ({x.Encoding} vs {y.Encoding})");
            if (x.FileName != y.FileName)
                lines.Add($"{label}: file name differs ({x.FileName ?? "-"} vs {y.FileName ?? "-"})");

            if (x.DecodedHash is null)
                continue;
            if (x.DecodedHash == y.DecodedHash)
                lines.Add($"{label}: decoded content identical");
            else if (x.NormalizedTextHash is not null && x.NormalizedTextHash == y.NormalizedTextHash)
                lines.Add($"{label}: text identical ignoring whitespace ({x.DecodedSize} vs {y.DecodedSize} bytes)");
            else
                lines.Add($"{label}: decoded content DIFFERS ({x.DecodedSize} vs {y.DecodedSize} bytes)");
        }
        return lines;
    }

    /// <summary>Offset of the first differing byte and a short printable context from each side; null when identical.</summary>
    public static (long Offset, string Left, string Right)? FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int context = 60)
    {
        var common = a.CommonPrefixLength(b);
        if (common == a.Length && common == b.Length)
            return null;
        var start = Math.Max(0, common - context / 2);
        return (common, Excerpt(a, start, context), Excerpt(b, start, context));
    }

    private static string Excerpt(ReadOnlySpan<byte> bytes, int start, int length)
    {
        var slice = bytes.Slice(start, Math.Min(length, bytes.Length - start));
        var sb = new StringBuilder(slice.Length);
        foreach (var c in slice)
            sb.Append(c switch
            {
                (byte)'\r' => "\\r",
                (byte)'\n' => "\\n",
                (byte)'\t' => "\\t",
                < 0x20 or > 0x7e => ".",
                _ => ((char)c).ToString(),
            });
        return sb.ToString();
    }

    private static void Walk(MimeEntity entity, string path, List<PartInfo> parts)
    {
        var encoding = entity is MimePart mp ? mp.ContentTransferEncoding.ToString().ToLowerInvariant() : "-";
        switch (entity)
        {
            case Multipart multipart:
                parts.Add(new PartInfo(path, entity.ContentType.MimeType, multipart.Boundary, null, encoding, 0, null, null));
                for (var i = 0; i < multipart.Count; i++)
                    Walk(multipart[i], $"{path}.{i + 1}", parts);
                break;

            case MessagePart messagePart:
                parts.Add(new PartInfo(path, entity.ContentType.MimeType, null, null, encoding, 0, null, null));
                if (messagePart.Message?.Body is { } inner)
                    Walk(inner, $"{path}.1", parts);
                break;

            case TextPart text when !text.IsAttachment:
            {
                var content = text.Text ?? "";
                var bytes = Encoding.UTF8.GetBytes(content);
                var normalized = Encoding.UTF8.GetBytes(Whitespace.Replace(content, ""));
                parts.Add(new PartInfo(path, entity.ContentType.MimeType, null, text.FileName, encoding, bytes.Length, Hash(bytes), Hash(normalized)));
                break;
            }

            case MimePart part:
            {
                using var stream = new MemoryStream();
                part.Content?.DecodeTo(stream);
                parts.Add(new PartInfo(path, entity.ContentType.MimeType, null, part.FileName, encoding, stream.Length, Hash(stream.ToArray()), null));
                break;
            }

            default:
                parts.Add(new PartInfo(path, entity.ContentType.MimeType, null, null, encoding, 0, null, null));
                break;
        }
    }

    /// <summary>Fingerprints the decoded content so two stored copies compare equal however the server serialized them.</summary>
    public static ContentSummary Summarize(MimeMessage message)
    {
        var parts = Describe(message);
        string Fingerprint(Func<PartInfo, string?> hashOf) =>
            Hash(Encoding.UTF8.GetBytes(string.Join("\n", parts.Select(p => $"{p.Path}|{p.ContentType}|{p.FileName}|{hashOf(p)}"))));
        return new ContentSummary(
            Fingerprint(p => p.DecodedHash),
            Fingerprint(p => p.NormalizedTextHash ?? p.DecodedHash),
            parts.Sum(p => p.DecodedSize));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
