using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Core.Workflows;

public sealed record ReconcileSummary(int Scanned, int Recorded);

/// <summary>Indexes what already exists in the target folders. Takes no IMailSource on purpose: it can never touch Gmail.</summary>
public sealed class ReconcileWorkflow(IMailTarget target, ILedger ledger, ILogger<ReconcileWorkflow> logger)
{
    public async Task<ReconcileSummary> RunAsync(CancellationToken ct)
    {
        var run = await ledger.StartRunAsync(RunMode.Reconcile, null, ct);
        int scanned = 0, recorded = 0;
        var exitCode = 2;

        try
        {
            await target.ConnectAsync(ct);
            await target.EnsureFoldersAsync(ct);

            foreach (var folder in new[] { TargetFolder.Archive, TargetFolder.Sent })
            {
                var messages = await target.ListAllAsync(folder, ct);
                logger.LogInformation("{Folder}: {Count} messages", folder, messages.Count);
                foreach (var message in messages)
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;
                    if (await ledger.RecordTargetAsync(MessageKeyBuilder.For(message), message, run.Id, ct))
                        recorded++;
                }
            }

            exitCode = 0;
        }
        finally
        {
            run.AlreadyInTarget = recorded;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            await ledger.FinishRunAsync(run, CancellationToken.None);
        }

        return new ReconcileSummary(scanned, recorded);
    }
}
