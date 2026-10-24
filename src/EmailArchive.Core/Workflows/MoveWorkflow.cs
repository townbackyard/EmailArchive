using EmailArchive.Core.Auth;
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Verification;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EmailArchive.Core.Workflows;

public sealed class MoveWorkflow(IMailSource source, IMailTarget target, ILedger ledger, ILogger<MoveWorkflow> logger)
{
    public async Task<RunSummary> RunAsync(MoveOptions options, CancellationToken ct)
    {
        var run = await ledger.StartRunAsync(RunMode.Move, options.Selection.Cutoff, ct);
        var aborted = false;
        var exitCode = 2;

        try
        {
            await source.ConnectAsync(ct);
            await target.ConnectAsync(ct);
            await target.EnsureFoldersAsync(ct);

            var completed = await ledger.CompletedAsync(ct);
            var candidates = await source.ListOlderThanAsync(options.Selection.Cutoff, ct);
            var selected = MessageSelector.Select(candidates, options.Selection, completed);
            run.Selected = selected.Count;
            logger.LogInformation("Selected {Count} messages older than {Cutoff:yyyy-MM-dd}", selected.Count, options.Selection.Cutoff);

            var consecutiveFailures = 0;
            foreach (var item in selected)
            {
                ct.ThrowIfCancellationRequested();
                var ok = await ProcessAsync(item, run, options.Purge, ct);
                if (ok)
                {
                    consecutiveFailures = 0;
                }
                else if (++consecutiveFailures >= options.MaxConsecutiveFailures)
                {
                    logger.LogError("{Count} consecutive failures, aborting run", consecutiveFailures);
                    aborted = true;
                    break;
                }
            }

            exitCode = aborted ? 2 : run.Failed > 0 ? 1 : 0;
        }
        catch (AuthRequiredException)
        {
            exitCode = 3;
            throw;
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            await ledger.FinishRunAsync(run, CancellationToken.None);
        }

        return new RunSummary(run.Selected, run.Copied, run.AlreadyInTarget, run.Verified, run.Trashed, run.Purged, run.Failed, aborted);
    }

    private async Task<bool> ProcessAsync(SelectedMessage item, LedgerRun run, bool purge, CancellationToken ct)
    {
        var msg = item.Message;
        var row = await ledger.GetOrCreateAsync(item.Key, msg, run.Id, ct);

        // For a Fallback key the verifier compares exactly the fields the key hashes, so re-verifying a different
        // Gmail UID against an existing copy can never fail. Never reuse such a row; leave the message for manual
        // review. This runs before any mutation so the original row is untouched, even under a tracked ledger.
        // It returns true so it does not feed the consecutive-failure abort (it recurs every run); run.Failed++
        // still makes the run exit with code 1.
        if (item.Key.Type == KeyType.Fallback && row.GmailUid is not null && row.GmailUid != msg.Uid)
        {
            logger.LogWarning(
                "UID {Uid}: duplicate fallback key (same date, sender, subject, no Message-ID) of UID {OldUid} in state {State}; leaving it in Gmail for manual review",
                msg.Uid, row.GmailUid, row.State);
            run.Failed++;
            return true;
        }

        // Rows reused within this run (not Pending) must also look for an existing copy before appending.
        var seenBefore = row.LastRunId != run.Id || row.State != MessageState.Pending;
        row.LastRunId = run.Id;
        var changed = false;

        try
        {
            if (row.State == MessageState.Failed)
            {
                row.State = MessageState.Pending;
                row.FailureReason = null;
                changed = true;
            }

            if (row.State == MessageState.Verified && row.GmailUid != msg.Uid)
            {
                // Verified was established for a different Gmail UID; this copy has not been compared yet.
                logger.LogInformation("UID {Uid} resumes Verified row recorded for UID {OldUid}; re-verifying", msg.Uid, row.GmailUid);
                row.State = MessageState.AlreadyInTarget;
                run.AlreadyInTarget++;
                changed = true;
            }

            if (row.State is MessageState.Trashed or MessageState.Purged)
            {
                // Same key, different Gmail UID: a duplicate copy. The content is already in the
                // target, so re-verify against it and trash this copy as well.
                logger.LogInformation("UID {Uid} duplicates already-trashed key {Key} (original UID {OldUid}, trash UID {OldTrashUid})",
                    msg.Uid, row.Key, row.GmailUid, row.TrashUid);
                row.State = MessageState.AlreadyInTarget;
                run.AlreadyInTarget++;
                changed = true;
            }

            if (row.GmailUid != msg.Uid)
            {
                row.GmailUid = msg.Uid;
                changed = true;
            }

            // Every transition is persisted before the next network call.
            if (changed)
                await ledger.SaveAsync(row, CancellationToken.None);

            if (row.State == MessageState.Pending)
            {
                if (row.TargetUid is not null)
                {
                    row.State = MessageState.AlreadyInTarget;
                    run.AlreadyInTarget++;
                }
                else
                {
                    var folder = FolderRouter.Route(msg);
                    var alreadyThere = false;

                    if (seenBefore)
                    {
                        // An earlier run may have appended without recording it; look before appending again.
                        var found = await WithRetryAsync(() => FindCandidatesAsync(folder, item, ct), ct);
                        var existing = found.Where(c => Verifier.Matches(msg, c)).ToList();
                        if (existing.Count > 0)
                        {
                            logger.LogInformation("UID {Uid}: already present in target ({Count} copies), skipping append", msg.Uid, existing.Count);
                            row.TargetFolder = folder;
                            row.State = MessageState.AlreadyInTarget;
                            run.AlreadyInTarget++;
                            alreadyThere = true;
                        }
                    }

                    if (!alreadyThere)
                    {
                        var mime = await WithRetryAsync(() => source.FetchAsync(msg.Uid, ct), ct);
                        var uid = await AppendIdempotentAsync(folder, item, mime, msg, ct);
                        row.TargetFolder = folder;
                        row.TargetUid = uid;
                        row.CopiedAt = DateTimeOffset.UtcNow;
                        row.State = MessageState.Copied;
                        run.Copied++;
                    }
                }
                await ledger.SaveAsync(row, CancellationToken.None);
            }

            if (row.State is MessageState.AlreadyInTarget or MessageState.Copied)
            {
                var folder = row.TargetFolder ?? FolderRouter.Route(msg);
                var candidates = await WithRetryAsync(() => FindCandidatesAsync(folder, item, ct), ct);
                var result = Verifier.Verify(msg, candidates);
                if (!result.Success)
                    return await FailAsync(row, run, result.Reason!);

                if (result.SizeWarning)
                    logger.LogWarning("UID {Uid}: size differs source={Source} target={Target}", msg.Uid, msg.Size, result.Match!.Size);

                row.TargetFolder = folder;
                row.TargetUid = result.Match!.Uid;
                row.TargetSize = result.Match.Size;
                row.VerifiedAt = DateTimeOffset.UtcNow;
                row.State = MessageState.Verified;
                run.Verified++;
                await ledger.SaveAsync(row, CancellationToken.None);
            }

            if (row.State == MessageState.Verified)
            {
                var trashUid = await WithRetryAsync(() => source.TrashAsync(msg.Uid, ct), ct);
                row.TrashUid = trashUid;
                row.TrashedAt = DateTimeOffset.UtcNow;
                row.State = MessageState.Trashed;
                run.Trashed++;
                await ledger.SaveAsync(row, CancellationToken.None);
            }

            if (row.State == MessageState.Trashed && purge)
            {
                if (row.TrashUid is long trashUidValue)
                {
                    await WithRetryVoidAsync(() => source.PurgeAsync((uint)trashUidValue, ct), ct);
                    row.PurgedAt = DateTimeOffset.UtcNow;
                    row.State = MessageState.Purged;
                    run.Purged++;
                    await ledger.SaveAsync(row, CancellationToken.None);
                }
                else
                {
                    logger.LogWarning("UID {Uid}: trashed but the server did not report a Trash UID; cannot purge", msg.Uid);
                }
            }

            logger.LogInformation("UID {Uid} {Date:yyyy-MM-dd} \"{Subject}\" -> {State}", msg.Uid, msg.InternalDate, msg.Subject, row.State);
            return true;
        }
        catch (AuthRequiredException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return await FailAsync(row, run, ex.Message, ex);
        }
    }

    private Task<IReadOnlyList<TargetMessageInfo>> FindCandidatesAsync(TargetFolder folder, SelectedMessage item, CancellationToken ct) =>
        item.Key.Type == KeyType.MessageId
            ? target.FindByMessageIdAsync(folder, item.Key.Value, ct)
            : target.FindByFallbackAsync(folder, item.Message.HeaderDate, item.Message.From, item.Message.Subject, ct);

    private static bool IsRetryable(Exception ex, CancellationToken ct) =>
        ex is not AuthRequiredException && !(ex is OperationCanceledException && ct.IsCancellationRequested);

    /// <summary>
    /// APPEND is not idempotent: if the response is lost the message may already be in the folder.
    /// On failure, reconnect and look for it before appending a second time.
    /// </summary>
    private async Task<uint?> AppendIdempotentAsync(TargetFolder folder, SelectedMessage item, MimeMessage mime, SourceMessageInfo msg, CancellationToken ct)
    {
        try
        {
            return await target.AppendAsync(folder, mime, msg.InternalDate, msg.IsSeen, msg.IsFlagged, ct);
        }
        catch (Exception ex) when (IsRetryable(ex, ct))
        {
            logger.LogWarning(ex, "Append failed, reconnecting and checking whether it landed");
            await source.ReconnectAsync(ct);
            await target.ReconnectAsync(ct);
            var landed = (await FindCandidatesAsync(folder, item, ct)).Where(c => Verifier.Matches(msg, c)).ToList();
            if (landed.Count > 0)
            {
                logger.LogInformation("UID {Uid}: append landed before the connection dropped", msg.Uid);
                return landed[0].Uid;
            }
            return await target.AppendAsync(folder, mime, msg.InternalDate, msg.IsSeen, msg.IsFlagged, ct);
        }
    }

    /// <summary>Runs the operation; on a non-auth failure reconnects both sides and tries exactly once more.</summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (IsRetryable(ex, ct))
        {
            logger.LogWarning(ex, "Operation failed, reconnecting and retrying once");
            await source.ReconnectAsync(ct);
            await target.ReconnectAsync(ct);
            return await operation();
        }
    }

    private Task WithRetryVoidAsync(Func<Task> operation, CancellationToken ct) =>
        WithRetryAsync(async () => { await operation(); return true; }, ct);

    private async Task<bool> FailAsync(LedgerMessage row, LedgerRun run, string reason, Exception? exception = null)
    {
        logger.LogError(exception, "UID {Uid} failed: {Reason}", row.GmailUid, reason);
        // A purge failure after a successful trash keeps the Trashed state so the message is not re-processed.
        if (row.State != MessageState.Trashed)
            row.State = MessageState.Failed;
        row.FailureReason = reason;
        run.Failed++;
        await ledger.SaveAsync(row, CancellationToken.None);
        return false;
    }
}
