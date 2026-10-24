namespace EmailArchive.Core.Selection;

/// <summary>Interprets Gmail X-GM-LABELS values. System labels arrive as "\Sent", "\Inbox", "\Chat".</summary>
public static class GmailLabels
{
    public static bool IsSent(IEnumerable<string> labels) => labels.Any(l => Canonical(l) == "sent");

    public static bool IsChat(IEnumerable<string> labels) => labels.Any(l => Canonical(l) == "chat");

    private static string Canonical(string label) => label.Trim().TrimStart('\\').ToLowerInvariant();
}
