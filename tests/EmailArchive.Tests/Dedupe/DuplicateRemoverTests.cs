using EmailArchive.Core.Dedupe;
using EmailArchive.Core.Model;
using EmailArchive.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Dedupe;

public class DuplicateRemoverTests
{
    private readonly FakeTarget _target = new();
    private DuplicateRemover Remover() => new(_target, NullLogger.Instance);

    private DuplicatePlan TwoCopies(SourceMessageInfo m)
    {
        _target.Folders[TargetFolder.Archive].Add(Target(m, uid: 1));
        _target.Folders[TargetFolder.Archive].Add(Target(m, uid: 2));
        return DuplicatePlan.Create(_target.Folders[TargetFolder.Archive].ToList(), null)!;
    }

    [Fact]
    public async Task Identical_extra_is_moved_to_deleted_items_when_applied()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        Assert.Equal(new DedupeOutcome(1, 0, 0), outcome);
        Assert.Equal(new[] { (TargetFolder.Archive, 2u) }, _target.DeletedItems);
        Assert.Single(_target.Folders[TargetFolder.Archive]);
        Assert.Equal(1u, _target.Folders[TargetFolder.Archive][0].Uid);
    }

    [Fact]
    public async Task Dry_run_counts_but_moves_nothing()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));

        var outcome = await Remover().ApplyAsync(plan, apply: false, CancellationToken.None);

        Assert.Equal(new DedupeOutcome(1, 0, 0), outcome);
        Assert.Empty(_target.DeletedItems);
        Assert.Equal(2, _target.Folders[TargetFolder.Archive].Count);
    }

    [Fact]
    public async Task Extra_with_different_body_is_kept()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.Bodies[2] = "different";

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        Assert.Equal(new DedupeOutcome(0, 1, 0), outcome);
        Assert.Empty(_target.DeletedItems);
    }

    [Fact]
    public async Task Survivor_is_never_moved()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        var trace = new List<string>();
        _target.Trace = trace;

        await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        Assert.DoesNotContain("target.moveToDeleted:1", trace);
        Assert.Contains("target.moveToDeleted:2", trace);
    }

    [Fact]
    public async Task Move_failure_is_counted_not_thrown()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.MoveToDeletedFailure = new IOException("boom");

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        Assert.Equal(new DedupeOutcome(0, 0, 1), outcome);
    }

    [Fact]
    public async Task Unreadable_survivor_leaves_everything_in_place()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.BodyHashFailure = new IOException("boom");

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        Assert.Equal(new DedupeOutcome(0, 0, 1), outcome);
        Assert.Empty(_target.DeletedItems);
    }

    [Fact]
    public async Task Vanished_survivor_leaves_everything_in_place()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.Folders[TargetFolder.Archive].RemoveAll(m => m.Uid == 1);

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        Assert.Equal(new DedupeOutcome(0, 0, 1), outcome);
        Assert.Empty(_target.DeletedItems);
    }
}
