using EmailArchive.Core.Auth;
using EmailArchive.Core.Dedupe;
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Core.Workflows;

/// <param name="Groups">Message-IDs that appear more than once in a target folder.</param>
/// <param name="Removed">Copies moved to Deleted Items, or that would be in a dry run.</param>
/// <param name="BodiesDiffer">Copies left in place because their body differs from the survivor's.</param>
/// <param name="Failed">Copies that could not be read or moved.</param>
public sealed record DedupeSummary(int Groups, int Removed, int BodiesDiffer, int Failed, bool Applied)
{
    public int ExitCode => Failed > 0 ? 1 : 0;
}

/// <summary>
/// Finds messages stored more than once in the target folders and removes the byte-identical extras.
/// Takes no IMailSource on purpose: it can never touch Gmail. Dry run unless apply is set.
/// </summary>
public sealed class DedupeWorkflow(IMailTarget target, ILedger ledger, ILogger<DedupeWorkflow> logger)
{
    public async Task<DedupeSummary> RunAsync(bool apply, CancellationToken ct)
    {
        var run = await ledger.StartRunAsync(RunMode.Dedupe, null, ct);
        int groups = 0, removed = 0, differ = 0, failed = 0;
        var exitCode = 2;

        try
        {
            await target.ConnectAsync(ct);
            await target.EnsureFoldersAsync(ct);
            var remover = new DuplicateRemover(target, logger);

            foreach (var folder in new[] { TargetFolder.Archive, TargetFolder.Sent })
            {
                var messages = await target.ListAllAsync(folder, ct);
                var duplicated = messages
                    .Select(m => (Id: MessageKeyBuilder.NormalizeMessageId(m.MessageId), Message: m))
                    .Where(x => x.Id is not null && x.Id.Length <= MessageKeyBuilder.MaxMessageIdLength)
                    .GroupBy(x => x.Id!, x => x.Message, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .ToList();
                logger.LogInformation("{Folder}: {Count} messages, {Groups} Message-IDs stored more than once", folder, messages.Count, duplicated.Count);

                foreach (var group in duplicated)
                {
                    ct.ThrowIfCancellationRequested();
                    var row = await ledger.FindAsync(new MessageKey(KeyType.MessageId, group.Key), ct);
                    var ledgerUid = row?.TargetFolder == folder ? row.TargetUid : null;
                    var plan = DuplicatePlan.Create(group.ToList(), ledgerUid);
                    if (plan is null)
                    {
                        logger.LogWarning("{Folder}: Message-ID {MessageId} appears {Count} times but the copies' headers differ; keeping all", folder, group.Key, group.Count());
                        continue;
                    }

                    groups++;
                    var outcome = await remover.ApplyAsync(plan, apply, ct);
                    removed += outcome.Removed;
                    differ += outcome.BodiesDiffer;
                    failed += outcome.Failed;
                    if (outcome.Failed > 0)
                    {
                        // A failure is most often a dropped IMAP connection; reconnect so one drop costs one group, not the rest of the run.
                        logger.LogInformation("Reconnecting to Microsoft 365 after a failed group");
                        await target.ReconnectAsync(ct);
                    }
                }
            }

            exitCode = failed > 0 ? 1 : 0;
        }
        catch (AuthRequiredException)
        {
            exitCode = 3;
            throw;
        }
        finally
        {
            run.Selected = groups;
            run.DuplicatesRemoved = removed;
            run.Failed = failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            try
            {
                await ledger.FinishRunAsync(run, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Must never replace the original exception (an AuthRequiredException still has to reach exit 3).
                logger.LogError(ex, "Could not record the run result in the ledger");
            }
        }

        return new DedupeSummary(groups, removed, differ, failed, apply);
    }
}
