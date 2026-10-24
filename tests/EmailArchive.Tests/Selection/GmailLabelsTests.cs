using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Selection;

public class GmailLabelsTests
{
    [Theory]
    [InlineData(new[] { "\\Sent" }, true)]
    [InlineData(new[] { "\\\\Sent" }, true)]
    [InlineData(new[] { "\\Inbox", "\\Sent" }, true)]
    [InlineData(new[] { "\\Inbox" }, false)]
    [InlineData(new string[0], false)]
    public void IsSent_detects_sent_label(string[] labels, bool expected)
    {
        Assert.Equal(expected, GmailLabels.IsSent(labels));
    }

    [Fact]
    public void IsChat_detects_chat_label()
    {
        Assert.True(GmailLabels.IsChat(new[] { "\\Chat" }));
        Assert.False(GmailLabels.IsChat(new[] { "\\Inbox" }));
    }

    [Fact]
    public void Router_sends_sent_mail_to_sent_folder()
    {
        Assert.Equal(TargetFolder.Sent, FolderRouter.Route(Msg(1, "2010-01-01", sent: true)));
        Assert.Equal(TargetFolder.Archive, FolderRouter.Route(Msg(2, "2010-01-01")));
    }
}
