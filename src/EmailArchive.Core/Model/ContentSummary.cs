namespace EmailArchive.Core.Model;

/// <summary>
/// What a stored message contains, independent of how the server chose to serialize it. Exchange stamps every stored
/// copy with its own part boundaries and Content-ID headers, so raw bytes never compare equal across copies, and a
/// copy that passed through Outlook on its way in has its text re-serialized and its text/plain alternative dropped.
/// </summary>
/// <param name="Fingerprint">Hash over the exact MIME part tree: each part's position, content type, file name and decoded content.</param>
/// <param name="Parts">
/// One key per leaf part, sorted: content type, file name and a hash of the decoded content, with text parts hashed
/// after removing whitespace and links. Position in the tree is not part of the key, so a bare text/html body and the
/// same HTML inside a multipart/alternative yield the same key.
/// </param>
/// <param name="DecodedBytes">Total decoded size of all leaf parts. Zero means the copy has no content at all.</param>
public sealed record ContentSummary(string Fingerprint, IReadOnlyList<string> Parts, long DecodedBytes)
{
    /// <summary>A copy with no text and no attachments: typically an upload that failed partway.</summary>
    public bool IsEmpty => DecodedBytes == 0;

    /// <summary>True when both copies carry the same leaf parts, however they were arranged or serialized.</summary>
    public bool IsEquivalentTo(ContentSummary other) => Parts.Count == other.Parts.Count && IsSubsumedBy(other);

    /// <summary>True when every leaf part of this copy also appears in the other, which may carry more.</summary>
    public bool IsSubsumedBy(ContentSummary other)
    {
        var remaining = other.Parts.ToList();
        foreach (var part in Parts)
            if (!remaining.Remove(part))
                return false;
        return true;
    }
}
