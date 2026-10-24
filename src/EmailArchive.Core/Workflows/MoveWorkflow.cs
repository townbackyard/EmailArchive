using EmailArchive.Core.Auth;
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Verification;
using Microsoft.Extensions.Logging;

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
        row.LastRunId = run.Id;

        try
        {
            if (row.State == MessageState.Failed)
            {
                row.State = MessageState.Pending;
                row.FailureReason = null;
            }

            if (row.State is MessageState.Trashed or MessageState.Purged)
            {
                // Same key, different Gmail UID: a duplicate copy. The content is already in the
                // target, so re-verify against it and trash this copy as well.
                logger.LogInformation("UID {Uid} duplicates already-trashed key {Key}", msg.Uid, row.Key);
                row.State = MessageState.AlreadyInTarget;
                run.AlreadyInTarget++;
            }

            row.GmailUid = msg.Uid;

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
                    var mime = await WithRetryAsync(() => source.FetchAsync(msg.Uid, ct), ct);
                    var uid = await WithRetryAsync(() => target.AppendAsync(folder, mime, msg.InternalDate, msg.IsSeen, msg.IsFlagged, ct), ct);
                    row.TargetFolder = folder;
                    row.TargetUid = uid;
                    row.CopiedAt = DateTimeOffset.UtcNow;
                    row.State = MessageState.Copied;
                    run.Copied++;
                }
                await ledger.SaveAsync(row, ct);
            }

            if (row.State is MessageState.AlreadyInTarget or MessageState.Copied)
            {
                var folder = row.TargetFolder ?? FolderRouter.Route(msg);
                var candidates = await WithRetryAsync(() => FindCandidatesAsync(folder, item, ct), ct);
                var result = Verifier.Verify(msg, candidates);
                if (!result.Success)
                    return await FailAsync(row, run, result.Reason!, ct);

                if (result.SizeWarning)
                    logger.LogWarning("UID {Uid}: size differs source={Source} target={Target}", msg.Uid, msg.Size, result.Match!.Size);

                row.TargetFolder = folder;
                row.TargetUid = result.Match!.Uid;
                row.TargetSize = result.Match.Size;
                row.VerifiedAt = DateTimeOffset.UtcNow;
                row.State = MessageState.Verified;
                run.Verified++;
                await ledger.SaveAsync(row, ct);
            }

            if (row.State == MessageState.Verified)
            {
                var trashUid = await WithRetryAsync(() => source.TrashAsync(msg.Uid, ct), ct);
                row.TrashUid = trashUid;
                row.TrashedAt = DateTimeOffset.UtcNow;
                row.State = MessageState.Trashed;
                run.Trashed++;
                await ledger.SaveAsync(row, ct);
            }

            if (row.State == MessageState.Trashed && purge && row.TrashUid is long trashUidValue)
            {
                await WithRetryVoidAsync(() => source.PurgeAsync((uint)trashUidValue, ct), ct);
                row.PurgedAt = DateTimeOffset.UtcNow;
                row.State = MessageState.Purged;
                run.Purged++;
                await ledger.SaveAsync(row, ct);
            }

            logger.LogInformation("UID {Uid} {Date:yyyy-MM-dd} \"{Subject}\" -> {State}", msg.Uid, msg.InternalDate, msg.Subject, row.State);
            return true;
        }
        catch (AuthRequiredException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return await FailAsync(row, run, ex.Message, ct);
        }
    }

    private Task<IReadOnlyList<TargetMessageInfo>> FindCandidatesAsync(TargetFolder folder, SelectedMessage item, CancellationToken ct) =>
        item.Key.Type == KeyType.MessageId
            ? target.FindByMessageIdAsync(folder, item.Key.Value, ct)
            : target.FindByFallbackAsync(folder, item.Message.HeaderDate, item.Message.From, item.Message.Subject, ct);

    /// <summary>Runs the operation; on a non-auth failure reconnects both sides and tries exactly once more.</summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is not AuthRequiredException and not OperationCanceledException)
        {
            logger.LogWarning(ex, "Operation failed, reconnecting and retrying once");
            await source.ReconnectAsync(ct);
            await target.ReconnectAsync(ct);
            return await operation();
        }
    }

    private Task WithRetryVoidAsync(Func<Task> operation, CancellationToken ct) =>
        WithRetryAsync(async () => { await operation(); return true; }, ct);

    private async Task<bool> FailAsync(LedgerMessage row, LedgerRun run, string reason, CancellationToken ct)
    {
        logger.LogError("UID {Uid} failed: {Reason}", row.GmailUid, reason);
        // A purge failure after a successful trash keeps the Trashed state so the message is not re-processed.
        if (row.State != MessageState.Trashed)
            row.State = MessageState.Failed;
        row.FailureReason = reason;
        run.Failed++;
        await ledger.SaveAsync(row, CancellationToken.None);
        return false;
    }
}
