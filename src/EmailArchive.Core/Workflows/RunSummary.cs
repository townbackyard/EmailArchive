using EmailArchive.Core.Selection;

namespace EmailArchive.Core.Workflows;

/// <param name="Dedupe">When a verified message has several matching copies in the target, remove the byte-identical extras (to Deleted Items).</param>
public sealed record MoveOptions(SelectionOptions Selection, bool Purge, int MaxConsecutiveFailures = 3, bool Dedupe = false);

public sealed record RunSummary(
    int Selected,
    int Copied,
    int AlreadyInTarget,
    int Verified,
    int Trashed,
    int Purged,
    int Failed,
    bool Aborted,
    int DuplicatesRemoved = 0)
{
    public int ExitCode => Aborted ? 2 : Failed > 0 ? 1 : 0;

    public override string ToString() =>
        $"selected={Selected} copied={Copied} alreadyInTarget={AlreadyInTarget} verified={Verified} " +
        $"trashed={Trashed} purged={Purged} failed={Failed}" +
        $"{(DuplicatesRemoved > 0 ? $" duplicatesRemoved={DuplicatesRemoved}" : "")}{(Aborted ? " ABORTED" : "")}";
}
