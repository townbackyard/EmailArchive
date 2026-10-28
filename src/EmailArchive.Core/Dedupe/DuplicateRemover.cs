using EmailArchive.Core.Mail;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Core.Dedupe;

/// <summary>Per-plan outcome. In a dry run, Removed counts the copies that would have been removed.</summary>
public sealed record DedupeOutcome(int Removed, int BodiesDiffer, int Failed);

/// <summary>
/// Removes extra copies of a message from a target folder, but only after proving each extra is byte-identical
/// to the survivor below the header block. Extras go to Deleted Items, never straight to expunge.
/// </summary>
public sealed class DuplicateRemover(IMailTarget target, ILogger logger)
{
    public async Task<DedupeOutcome> ApplyAsync(DuplicatePlan plan, bool apply, CancellationToken ct)
    {
        int removed = 0, differ = 0, failed = 0;
        var folder = plan.Keep.Folder;

        string? keepHash;
        try
        {
            keepHash = await target.GetBodyHashAsync(folder, plan.Keep.Uid, ct);
        }
        catch (Exception ex) when (IsRecoverable(ex, ct))
        {
            logger.LogWarning(ex, "Target UID {Uid}: could not read the surviving copy; leaving its duplicates in place", plan.Keep.Uid);
            return new DedupeOutcome(0, 0, plan.Extras.Count);
        }
        if (keepHash is null)
        {
            logger.LogWarning("Target UID {Uid}: surviving copy no longer exists; leaving its duplicates in place", plan.Keep.Uid);
            return new DedupeOutcome(0, 0, plan.Extras.Count);
        }

        foreach (var extra in plan.Extras)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var extraHash = await target.GetBodyHashAsync(folder, extra.Uid, ct);
                if (extraHash is null)
                {
                    logger.LogDebug("Target UID {Uid}: duplicate already gone", extra.Uid);
                    continue;
                }
                if (extraHash != keepHash)
                {
                    differ++;
                    logger.LogWarning("Target UID {Uid}: same headers as UID {Keep} but the body differs; keeping both", extra.Uid, plan.Keep.Uid);
                    continue;
                }

                if (apply)
                {
                    await target.MoveToDeletedItemsAsync(folder, extra.Uid, ct);
                    logger.LogInformation("Target UID {Uid}: duplicate of UID {Keep} ({Size} bytes) moved to Deleted Items", extra.Uid, plan.Keep.Uid, extra.Size);
                }
                else
                {
                    logger.LogInformation("Target UID {Uid}: duplicate of UID {Keep} ({Size} bytes) would be moved to Deleted Items", extra.Uid, plan.Keep.Uid, extra.Size);
                }
                removed++;
            }
            catch (Exception ex) when (IsRecoverable(ex, ct))
            {
                failed++;
                logger.LogWarning(ex, "Target UID {Uid}: could not remove duplicate of UID {Keep}", extra.Uid, plan.Keep.Uid);
            }
        }

        return new DedupeOutcome(removed, differ, failed);
    }

    private static bool IsRecoverable(Exception ex, CancellationToken ct) =>
        ex is not Auth.AuthRequiredException && !(ex is OperationCanceledException && ct.IsCancellationRequested);
}
