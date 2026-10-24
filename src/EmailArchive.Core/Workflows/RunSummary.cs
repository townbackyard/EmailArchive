using EmailArchive.Core.Selection;

namespace EmailArchive.Core.Workflows;

public sealed record MoveOptions(SelectionOptions Selection, bool Purge, int MaxConsecutiveFailures = 3);

public sealed record RunSummary(
    int Selected,
    int Copied,
    int AlreadyInTarget,
    int Verified,
    int Trashed,
    int Purged,
    int Failed,
    bool Aborted)
{
    public int ExitCode => Aborted ? 2 : Failed > 0 ? 1 : 0;

    public override string ToString() =>
        $"selected={Selected} copied={Copied} alreadyInTarget={AlreadyInTarget} verified={Verified} " +
        $"trashed={Trashed} purged={Purged} failed={Failed}{(Aborted ? " ABORTED" : "")}";
}
