using EmailArchive.Core.Model;

namespace EmailArchive.Core.Selection;

public static class FolderRouter
{
    public static TargetFolder Route(SourceMessageInfo message) =>
        message.IsSent ? TargetFolder.Sent : TargetFolder.Archive;
}
