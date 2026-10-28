# EmailArchive

A self-hosted command-line tool that moves the oldest messages out of a Gmail
account into a Microsoft 365 mailbox, verifies each one arrived, and then
trashes the Gmail copy. Run it monthly to keep Gmail trimmed to a rolling
window (default 5 years) without paying for extra Google storage.

**Status: working.** See the setup guides before the first run.

## Setup

1. [Gmail app password](docs/setup-gmail.md)
2. [Microsoft Entra app registration](docs/setup-entra.md) and `appsettings.local.json`
3. A local SQL Server instance. The `Ledger:ConnectionString` default points at `localhost` with
   Windows authentication and database `EmailArchive`, created automatically on first run.

## Usage

    EmailArchive report                      # dry run: what would move
    EmailArchive reconcile                   # index what is already in the target folders
    EmailArchive move --max-messages 20      # first supervised batch
    EmailArchive move                        # full backlog; trashed messages stay in Gmail Trash 30 days unless you empty it or use --purge
    EmailArchive move --purge                # same, and expunge from Trash immediately
    EmailArchive move --dedupe               # also remove identical extra copies met during verification
    EmailArchive dedupe                      # dry run: find messages stored more than once in the target folders
    EmailArchive dedupe --apply              # move the identical extras to Deleted Items in Microsoft 365
    EmailArchive login                       # redo the Microsoft sign-in (exit code 3 asks for this)

Options for `report` and `move`: `--keep-years N`, `--max-messages N`, `--max-bytes N`.

Duplicates: an earlier manual archive attempt can leave the same message in the target folder twice. Two
copies count as duplicates only when they share a Message-ID, match on Date, From and Subject, and have
byte-identical bodies below the header block. The copy the ledger points at survives (otherwise the lowest
UID); extras go to Deleted Items in Microsoft 365, never straight to expunge. `move --dedupe` handles the
duplicates it meets while verifying; `dedupe` scans the whole target folder, which also covers messages
that are no longer in Gmail. Neither touches Gmail.

`--max-bytes` is a soft cap: the first selected message always moves even if it is larger, so every run makes progress.

Exit codes: 0 success, 1 some messages failed (see the log) or a command-line usage error, 2 aborted or configuration error, 3 Microsoft sign-in required.

Messages that have no Message-ID header and share the same date, sender and subject as a message that was already archived are left in Gmail and logged as a warning for manual review. They count as failures (exit code 1) but never abort the run. Messages larger than your Microsoft 365 mailbox's receive limit (36 MB by default) fail verification every run and stay in Gmail.

Do not run two instances at once (for example a manual run while the scheduled task is running); they would both copy the same messages.

Schedule it monthly with [Task Scheduler](docs/setup-schedule.md).

## Why this exists

Gmail folders are labels. Moving a message out of a label in a mail client only
removes the label; the message stays in All Mail and still counts toward your
storage. Freeing space means moving the message to Gmail Trash, and nothing
should go there until it is verified to exist in the destination mailbox. None
of the free tools do that per message, oldest first, on a schedule.

## How it works

- C# / .NET 10 console app using MailKit for IMAP on both sides.
- Gmail: app password, stored in Windows Credential Manager.
- Microsoft 365: OAuth2 through an app registration in your own tenant. No
  third-party service ever sees either account.
- Each message: copy to the `Gmail Archive` folder (or `Gmail Archive/Sent`),
  verify by Message-ID, then move the Gmail copy to Trash. A local SQL Server
  ledger records every step so runs are resumable and auditable.
- Modes: `report` (dry run), `reconcile` (index what is already in the target),
  `move` (the real thing, with per-run caps and optional `--purge` and `--dedupe`),
  `dedupe` (remove identical duplicate copies from the target folders).

## Configuration and privacy

All personal values (addresses, tenant ID, client ID, connection string) go in
`appsettings.local.json`, which is git-ignored. Nothing in this repository
should ever contain real account details.

## License

MIT, see [LICENSE](LICENSE).
