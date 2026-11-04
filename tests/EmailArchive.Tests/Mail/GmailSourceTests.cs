using EmailArchive.Core.Mail;
using MailKit;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace EmailArchive.Tests.Mail;

public class GmailSourceTests
{
    private static MessageSummary Summary(uint uid, bool internalDate = true, bool envelope = true) =>
        new(0)
        {
            UniqueId = new UniqueId(uid),
            InternalDate = internalDate ? new DateTimeOffset(2017, 1, 8, 10, 50, 30, TimeSpan.Zero) : null,
            Envelope = envelope ? new Envelope { Subject = "hi" } : null,
        };

    private static readonly UniqueId[] Requested = { new(10), new(11), new(12) };

    [Fact]
    public void Complete_records_for_requested_uids_pass_through()
    {
        var kept = GmailSource.Complete(new[] { Summary(10), Summary(11), Summary(12) }, Requested, NullLogger.Instance);

        Assert.Equal(new uint[] { 10, 11, 12 }, kept.Select(s => s.UniqueId.Id));
    }

    [Fact]
    public void Unsolicited_record_for_another_message_is_ignored()
    {
        var kept = GmailSource.Complete(new[] { Summary(10), Summary(355034, internalDate: false, envelope: false), Summary(11) }, Requested, NullLogger.Instance);

        Assert.Equal(new uint[] { 10, 11 }, kept.Select(s => s.UniqueId.Id));
    }

    [Fact]
    public void Requested_record_missing_internal_date_or_envelope_is_skipped_not_thrown()
    {
        var kept = GmailSource.Complete(new[] { Summary(10, internalDate: false), Summary(11, envelope: false), Summary(12) }, Requested, NullLogger.Instance);

        Assert.Equal(new uint[] { 12 }, kept.Select(s => s.UniqueId.Id));
    }
}
