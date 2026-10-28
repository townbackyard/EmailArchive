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

    private static void AssertCounts(DedupeOutcome outcome, int removed, int differ, int whitespace, int failed)
    {
        Assert.Equal((removed, differ, whitespace, failed), (outcome.Removed, outcome.ContentDiffers, outcome.WhitespaceOnly, outcome.Failed));
    }

    [Fact]
    public async Task Identical_extra_is_moved_to_deleted_items_when_applied()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        AssertCounts(outcome, 1, 0, 0, 0);
        Assert.Equal(1u, outcome.Kept.Uid);
        Assert.Equal(new[] { (TargetFolder.Archive, 2u) }, _target.DeletedItems);
        Assert.Equal(1u, Assert.Single(_target.Folders[TargetFolder.Archive]).Uid);
    }

    [Fact]
    public async Task Dry_run_counts_but_moves_nothing()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));

        var outcome = await Remover().ApplyAsync(plan, apply: false, CancellationToken.None);

        AssertCounts(outcome, 1, 0, 0, 0);
        Assert.Empty(_target.DeletedItems);
        Assert.Equal(2, _target.Folders[TargetFolder.Archive].Count);
    }

    [Fact]
    public async Task Extra_with_different_content_is_kept()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.Bodies[2] = "different";

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        AssertCounts(outcome, 0, 1, 0, 0);
        Assert.Empty(_target.DeletedItems);
    }

    [Fact]
    public async Task Extra_differing_only_in_whitespace_is_kept_and_counted_separately()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.Bodies[1] = "hello world";
        _target.Bodies[2] = "hello\r\n  world";

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        AssertCounts(outcome, 0, 1, 1, 0);
        Assert.Empty(_target.DeletedItems);
    }

    [Fact]
    public async Task Empty_extra_is_removed()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.Bodies[2] = "";

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        AssertCounts(outcome, 1, 0, 0, 0);
        Assert.Equal(1u, outcome.Kept.Uid);
        Assert.Equal(new[] { (TargetFolder.Archive, 2u) }, _target.DeletedItems);
    }

    [Fact]
    public async Task Empty_copy_is_removed_even_when_it_was_the_planned_survivor()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.Bodies[1] = "";

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        AssertCounts(outcome, 1, 0, 0, 0);
        Assert.Equal(2u, outcome.Kept.Uid);
        Assert.Equal(new[] { (TargetFolder.Archive, 1u) }, _target.DeletedItems);
        Assert.Equal(2u, Assert.Single(_target.Folders[TargetFolder.Archive]).Uid);
    }

    [Fact]
    public async Task Empty_planned_survivor_with_differing_extras_keeps_the_lowest_copy_with_content()
    {
        var m = Msg(1, "2010-01-01");
        _target.Folders[TargetFolder.Archive].Add(Target(m, uid: 1));
        _target.Folders[TargetFolder.Archive].Add(Target(m, uid: 2));
        _target.Folders[TargetFolder.Archive].Add(Target(m, uid: 3));
        _target.Bodies[1] = "";
        _target.Bodies[2] = "version a";
        _target.Bodies[3] = "version b";
        var plan = DuplicatePlan.Create(_target.Folders[TargetFolder.Archive].ToList(), null)!;

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        AssertCounts(outcome, 1, 1, 0, 0);
        Assert.Equal(2u, outcome.Kept.Uid);
        Assert.Equal(new[] { (TargetFolder.Archive, 1u) }, _target.DeletedItems);
    }

    [Fact]
    public async Task Two_empty_copies_are_identical_and_one_is_removed()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.Bodies[1] = "";
        _target.Bodies[2] = "";

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        AssertCounts(outcome, 1, 0, 0, 0);
        Assert.Equal(1u, outcome.Kept.Uid);
        Assert.Equal(new[] { (TargetFolder.Archive, 2u) }, _target.DeletedItems);
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

        AssertCounts(outcome, 0, 0, 0, 1);
    }

    [Fact]
    public async Task Unreadable_survivor_leaves_everything_in_place()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.ContentFailure = new IOException("boom");

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        AssertCounts(outcome, 0, 0, 0, 1);
        Assert.Empty(_target.DeletedItems);
    }

    [Fact]
    public async Task Vanished_survivor_leaves_everything_in_place()
    {
        var plan = TwoCopies(Msg(1, "2010-01-01"));
        _target.Folders[TargetFolder.Archive].RemoveAll(m => m.Uid == 1);

        var outcome = await Remover().ApplyAsync(plan, apply: true, CancellationToken.None);

        AssertCounts(outcome, 0, 0, 0, 1);
        Assert.Empty(_target.DeletedItems);
    }
}
