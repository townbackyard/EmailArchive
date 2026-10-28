namespace EmailArchive.Core.Model;

/// <summary>
/// What a stored message contains, independent of how the server chose to serialize it. Exchange stamps every stored
/// copy with its own part boundaries and Content-ID headers, so raw bytes never compare equal across copies.
/// </summary>
/// <param name="Fingerprint">Hash over the MIME part tree: each part's content type, file name and decoded content.</param>
/// <param name="NormalizedFingerprint">The same, with text parts hashed after removing all whitespace.</param>
/// <param name="DecodedBytes">Total decoded size of all leaf parts. Zero means the copy has no content at all.</param>
public sealed record ContentSummary(string Fingerprint, string NormalizedFingerprint, long DecodedBytes)
{
    /// <summary>A copy with no text and no attachments: typically an upload that failed partway.</summary>
    public bool IsEmpty => DecodedBytes == 0;
}
