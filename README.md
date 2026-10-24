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
    EmailArchive login                       # redo the Microsoft sign-in (exit code 3 asks for this)

Options for `report` and `move`: `--keep-years N`, `--max-messages N`, `--max-bytes N`.

`--max-bytes` is a soft cap: the first selected message always moves even if it is larger, so every run makes progress.

Exit codes: 0 success, 1 some messages failed (see the log) or a command-line usage error, 2 aborted or configuration error, 3 Microsoft sign-in required.

Messages that have no Message-ID header and share the same date, sender and subject as a message that was already archived are left in Gmail and logged as a warning for manual review. They count as failures (exit code 1) but never abort the run.

Schedule it monthly with [Task Scheduler](docs/setup-schedule.md).

## Why this exists

Gmail folders are labels. Moving a message out of a label in a mail client only
removes the label; the message stays in All Mail and still counts toward your
storage. Freeing space means moving the message to Gmail Trash, and nothing
should go there until it is verified to exist in the destination mailbox. None
of the free tools do that per message, oldest first, on a schedule.

## How it will work

- C# / .NET 10 console app using MailKit for IMAP on both sides.
- Gmail: app password, stored in Windows Credential Manager.
- Microsoft 365: OAuth2 through an app registration in your own tenant. No
  third-party service ever sees either account.
- Each message: copy to the `Gmail Archive` folder (or `Gmail Archive/Sent`),
  verify by Message-ID, then move the Gmail copy to Trash. A local SQL Server
  ledger records every step so runs are resumable and auditable.
- Modes: `report` (dry run), `reconcile` (index what is already in the target),
  `move` (the real thing, with per-run caps and an optional `--purge`).

## Configuration and privacy

All personal values (addresses, tenant ID, client ID, connection string) go in
`appsettings.local.json`, which is git-ignored. Nothing in this repository
should ever contain real account details.

## License

MIT, see [LICENSE](LICENSE).
