using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmailArchive.Core.Dedupe;

/// <summary>Per-plan outcome. In a dry run, Removed counts the copies that would have been removed.</summary>
/// <param name="Removed">Copies moved to Deleted Items (or that would be).</param>
/// <param name="ContentDiffers">Copies kept because they carry content the survivor does not.</param>
/// <param name="Equivalent">Of those removed, copies that were not byte-identical: text re-wrapped, links re-tokenized, or parts missing.</param>
/// <param name="Failed">Copies that could not be read or moved.</param>
/// <param name="Kept">The copy that survived; differs from the plan's Keep when another copy carried more content.</param>
public sealed record DedupeOutcome(int Removed, int ContentDiffers, int Equivalent, int Failed, TargetMessageInfo Kept);

/// <summary>
/// Removes extra copies of a message from a target folder, but only after proving each extra carries nothing the
/// survivor lacks. Extras go to Deleted Items, never straight to expunge.
/// </summary>
public sealed class DuplicateRemover(IMailTarget target, ILogger logger)
{
    public async Task<DedupeOutcome> ApplyAsync(DuplicatePlan plan, bool apply, CancellationToken ct)
    {
        int removed = 0, differ = 0, equivalent = 0, failed = 0;
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

        // Read every extra before deciding anything: the survivor is only final once every copy's content is known.
        var contents = new Dictionary<uint, ContentSummary> { [plan.Keep.Uid] = keepContent };
        var copies = new List<TargetMessageInfo> { plan.Keep };
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
                copies.Add(extra);
            }
            catch (Exception ex) when (IsRecoverable(ex, ct))
            {
                failed++;
                logger.LogWarning(ex, "Target UID {Uid}: could not read duplicate of UID {Keep}", extra.Uid, plan.Keep.Uid);
            }
        }

        var keep = ChooseSurvivor(plan.Keep, copies, contents);
        keepContent = contents[keep.Uid];
        var extras = copies.Where(c => c.Uid != keep.Uid).OrderBy(c => c.Uid).ToList();

        foreach (var extra in extras)
        {
            ct.ThrowIfCancellationRequested();
            var content = contents[extra.Uid];
            string what;
            if (content.IsEmpty && !keepContent.IsEmpty)
                what = "empty copy";
            else if (content.Fingerprint == keepContent.Fingerprint)
                what = "duplicate";
            else if (content.IsEquivalentTo(keepContent))
            {
                equivalent++;
                what = "equivalent copy";
            }
            else if (content.IsSubsumedBy(keepContent))
            {
                equivalent++;
                what = "partial copy";
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

        return new DedupeOutcome(removed, differ, equivalent, failed, keep);
    }

    /// <summary>
    /// The copy with the most content survives: an empty copy is a failed upload, and a copy that lost its text/plain
    /// alternative or an inline image on the way through Outlook is a lesser rendering of the same message. Among copies
    /// with the same number of parts the planned survivor (the ledger's copy, else the lowest UID) keeps its place.
    /// </summary>
    private TargetMessageInfo ChooseSurvivor(TargetMessageInfo planned, List<TargetMessageInfo> copies, Dictionary<uint, ContentSummary> contents)
    {
        var mostParts = copies.Max(c => contents[c.Uid].Parts.Count);
        var richest = copies.Where(c => !contents[c.Uid].IsEmpty && contents[c.Uid].Parts.Count == mostParts).ToList();
        if (richest.Count == 0)
            return planned;
        var survivor = richest.FirstOrDefault(c => c.Uid == planned.Uid) ?? richest.MinBy(c => c.Uid)!;
        if (survivor.Uid != planned.Uid)
        {
            var reason = contents[planned.Uid].IsEmpty ? "has no content" : "carries fewer parts";
            logger.LogInformation("Target UID {Uid} {Reason}; keeping UID {Survivor} instead", planned.Uid, reason, survivor.Uid);
        }
        return survivor;
    }

    private static bool IsRecoverable(Exception ex, CancellationToken ct) =>
        ex is not Auth.AuthRequiredException && !(ex is OperationCanceledException && ct.IsCancellationRequested);
}
