using EmailArchive.Core.Model;
using MimeKit;
using MimeKit.Utils;

namespace EmailArchive.Tests;

public static class TestData
{
    public static SourceMessageInfo Msg(
        uint uid,
        string date,
        string? messageId = "default",
        string from = "sender@example.com",
        string subject = "Hello",
        long size = 1000,
        bool sent = false,
        bool chat = false,
        bool seen = true,
        bool flagged = false)
    {
        var d = DateTimeOffset.Parse(date, null, System.Globalization.DateTimeStyles.AssumeUniversal);
        var id = messageId == "default" ? $"id-{uid}@example.com" : messageId;
        return new SourceMessageInfo(uid, id, d, d, from, subject, size, sent, chat, seen, flagged);
    }

    public static MimeMessage MakeMime(SourceMessageInfo m)
    {
        var mime = new MimeMessage();
        mime.MessageId = m.MessageId ?? MimeUtils.GenerateMessageId();
        mime.Date = m.HeaderDate ?? m.InternalDate;
        if (!string.IsNullOrEmpty(m.From)) mime.From.Add(MailboxAddress.Parse(m.From));
        mime.Subject = m.Subject;
        mime.Body = new TextPart("plain") { Text = "body" };
        return mime;
    }

    public static TargetMessageInfo Target(SourceMessageInfo m, TargetFolder folder = TargetFolder.Archive, uint uid = 500, long? size = null) =>
        new(folder, uid, m.MessageId, m.HeaderDate, m.From, m.Subject, size ?? m.Size);
}
