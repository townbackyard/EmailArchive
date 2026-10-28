using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Core.Dedupe;

/// <summary>Per-plan outcome. In a dry run, Removed counts the copies that would have been removed.</summary>
/// <param name="Removed">Copies moved to Deleted Items (or that would be).</param>
/// <param name="ContentDiffers">Copies kept because their decoded content differs from the survivor's.</param>
/// <param name="WhitespaceOnly">Of those kept, copies whose text differs only in whitespace.</param>
/// <param name="Failed">Copies that could not be read or moved.</param>
/// <param name="Kept">The copy that survived; differs from the plan's Keep when that copy turned out to be empty.</param>
public sealed record DedupeOutcome(int Removed, int ContentDiffers, int WhitespaceOnly, int Failed, TargetMessageInfo Kept);

/// <summary>
/// Removes extra copies of a message from a target folder, but only after proving each extra carries the same decoded
/// content as the survivor, or no content at all. Extras go to Deleted Items, never straight to expunge.
/// </summary>
public sealed class DuplicateRemover(IMailTarget target, ILogger logger)
{
    public async Task<DedupeOutcome> ApplyAsync(DuplicatePlan plan, bool apply, CancellationToken ct)
    {
        int removed = 0, differ = 0, whitespace = 0, failed = 0;
        var folder = plan.Keep.Folder;

        ContentSummary? keepContent;
        try
        {
            keepContent = await target.GetContentAsync(folder, plan.Keep.Uid, ct);
        }
        catch (Exception ex) when (IsRecoverable(ex, ct))
        {
            logger.LogWarning(ex, "Target UID {Uid}: could not read the surviving copy; leaving its duplicates in place", plan.Keep.Uid);
            return new DedupeOutcome(0, 0, 0, plan.Extras.Count, plan.Keep);
        }
        if (keepContent is null)
        {
            logger.LogWarning("Target UID {Uid}: surviving copy no longer exists; leaving its duplicates in place", plan.Keep.Uid);
            return new DedupeOutcome(0, 0, 0, plan.Extras.Count, plan.Keep);
        }

        // Read every extra before deciding anything: the survivor is only final once we know which copies have content.
        var contents = new Dictionary<uint, ContentSummary> { [plan.Keep.Uid] = keepContent };
        var extras = new List<TargetMessageInfo>();
        foreach (var extra in plan.Extras)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await target.GetContentAsync(folder, extra.Uid, ct);
                if (content is null)
                {
                    logger.LogDebug("Target UID {Uid}: duplicate already gone", extra.Uid);
                    continue;
                }
                contents[extra.Uid] = content;
                extras.Add(extra);
            }
            catch (Exception ex) when (IsRecoverable(ex, ct))
            {
                failed++;
                logger.LogWarning(ex, "Target UID {Uid}: could not read duplicate of UID {Keep}", extra.Uid, plan.Keep.Uid);
            }
        }

        // An empty copy is a failed upload, not a version of the message. Never let it be the one that survives.
        var keep = plan.Keep;
        if (keepContent.IsEmpty && extras.FirstOrDefault(e => !contents[e.Uid].IsEmpty) is { } withContent)
        {
            logger.LogInformation("Target UID {Uid} has no content; keeping UID {Survivor} instead", keep.Uid, withContent.Uid);
            extras = extras.Where(e => e.Uid != withContent.Uid).Append(keep).OrderBy(e => e.Uid).ToList();
            keep = withContent;
            keepContent = contents[keep.Uid];
        }

        foreach (var extra in extras)
        {
            ct.ThrowIfCancellationRequested();
            var content = contents[extra.Uid];
            string what;
            if (content.IsEmpty && !keepContent.IsEmpty)
                what = "empty copy";
            else if (content.Fingerprint == keepContent.Fingerprint)
                what = "duplicate";
            else if (content.NormalizedFingerprint == keepContent.NormalizedFingerprint)
            {
                whitespace++;
                differ++;
                logger.LogWarning("Target UID {Uid}: same headers as UID {Keep} but the text differs in whitespace only; keeping both", extra.Uid, keep.Uid);
                continue;
            }
            else
            {
                differ++;
                logger.LogWarning("Target UID {Uid}: same headers as UID {Keep} but the content differs; keeping both", extra.Uid, keep.Uid);
                continue;
            }

            try
            {
                if (apply)
                {
                    await target.MoveToDeletedItemsAsync(folder, extra.Uid, ct);
                    logger.LogInformation("Target UID {Uid}: {What} of UID {Keep} ({Size} bytes) moved to Deleted Items", extra.Uid, what, keep.Uid, extra.Size);
                }
                else
                {
                    logger.LogInformation("Target UID {Uid}: {What} of UID {Keep} ({Size} bytes) would be moved to Deleted Items", extra.Uid, what, keep.Uid, extra.Size);
                }
                removed++;
            }
            catch (Exception ex) when (IsRecoverable(ex, ct))
            {
                failed++;
                logger.LogWarning(ex, "Target UID {Uid}: could not remove {What} of UID {Keep}", extra.Uid, what, keep.Uid);
            }
        }

        return new DedupeOutcome(removed, differ, whitespace, failed, keep);
    }

    private static bool IsRecoverable(Exception ex, CancellationToken ct) =>
        ex is not Auth.AuthRequiredException && !(ex is OperationCanceledException && ct.IsCancellationRequested);
}
