using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Core.Workflows;

public sealed record ReportSummary(int Count, long TotalBytes, DateTimeOffset? Oldest, DateTimeOffset? Newest, int AlreadyInTarget, int SentCount);

/// <summary>Dry run. Reads Gmail headers and the ledger, writes only a run row.</summary>
public sealed class ReportWorkflow(IMailSource source, ILedger ledger, ILogger<ReportWorkflow> logger)
{
    public async Task<ReportSummary> RunAsync(SelectionOptions options, CancellationToken ct)
    {
        var run = await ledger.StartRunAsync(RunMode.Report, options.Cutoff, ct);
        ReportSummary summary;
        var exitCode = 2;

        try
        {
            await source.ConnectAsync(ct);
            var completed = await ledger.CompletedAsync(ct);
            var candidates = await source.ListOlderThanAsync(options.Cutoff, ct);
            var selected = MessageSelector.Select(candidates, options, completed);

            var alreadyInTarget = 0;
            foreach (var item in selected)
            {
                var row = await ledger.FindAsync(item.Key, ct);
                if (row?.TargetUid is not null) alreadyInTarget++;
            }

            summary = new ReportSummary(
                selected.Count,
                selected.Sum(s => s.Message.Size),
                selected.Count == 0 ? null : selected[0].Message.InternalDate,
                selected.Count == 0 ? null : selected[^1].Message.InternalDate,
                alreadyInTarget,
                selected.Count(s => s.Message.IsSent));

            run.Selected = summary.Count;
            run.AlreadyInTarget = alreadyInTarget;
            logger.LogInformation("Report: {Summary}", summary);
            exitCode = 0;
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            await ledger.FinishRunAsync(run, CancellationToken.None);
        }

        return summary;
    }
}
