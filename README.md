# EmailArchive

A self-hosted command-line tool that moves the oldest messages out of a Gmail
account into a Microsoft 365 mailbox, verifies each one arrived, and then
trashes the Gmail copy. Run it monthly to keep Gmail trimmed to a rolling
window (default 5 years) without paying for extra Google storage.

**Status: in design. No runnable code yet.** The full design is in
[docs/superpowers/specs/2026-10-24-gmail-to-m365-archive-design.md](docs/superpowers/specs/2026-10-24-gmail-to-m365-archive-design.md).

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

MIT (to be added).
