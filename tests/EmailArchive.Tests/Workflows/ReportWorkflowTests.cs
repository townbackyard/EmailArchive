using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Workflows;
using EmailArchive.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using static EmailArchive.Tests.TestData;

namespace EmailArchive.Tests.Workflows;

public class ReportWorkflowTests
{
    private static readonly DateTimeOffset Cutoff = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly FakeSource _source = new();
    private readonly InMemoryLedger _ledger = new();

    private ReportWorkflow Workflow() => new(_source, _ledger, NullLogger<ReportWorkflow>.Instance);

    [Fact]
    public async Task Summarizes_selection_without_touching_messages()
    {
        var a = Msg(1, "2010-01-01", size: 100);
        var b = Msg(2, "2012-06-01", size: 250, sent: true);
        var c = Msg(3, "2023-01-01", size: 999);
        _source.Messages.AddRange(new[] { a, b, c });
        _ledger.Seed(b, MessageState.Pending, r => r.TargetUid = 5);

        var summary = await Workflow().RunAsync(new SelectionOptions(Cutoff, null, null, false), CancellationToken.None);

        Assert.Equal(2, summary.Count);
        Assert.Equal(350, summary.TotalBytes);
        Assert.Equal(a.InternalDate, summary.Oldest);
        Assert.Equal(b.InternalDate, summary.Newest);
        Assert.Equal(1, summary.AlreadyInTarget);
        Assert.Equal(1, summary.SentCount);
        Assert.Empty(_source.Trashed);
        Assert.Single(_ledger.Rows); // only the seeded row, nothing created
        Assert.Equal(RunMode.Report, _ledger.Runs.Single().Mode);
    }

    [Fact]
    public async Task Empty_selection_reports_zero()
    {
        var summary = await Workflow().RunAsync(new SelectionOptions(Cutoff, null, null, false), CancellationToken.None);
        Assert.Equal(0, summary.Count);
        Assert.Null(summary.Oldest);
    }
}
