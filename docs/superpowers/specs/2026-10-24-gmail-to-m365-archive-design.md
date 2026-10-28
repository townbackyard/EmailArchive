# Gmail to Microsoft 365 Archive Mover: Design Spec

Date: 2026-10-24
Status: Draft for review

## 1. Purpose

A self-hosted command-line tool that moves the oldest messages out of a personal
Gmail account into a Microsoft 365 mailbox, verifies each message arrived, and
then trashes the Gmail copy. Run monthly, it keeps Gmail trimmed to a rolling
window (default 5 years) without paying for extra Google storage.

The tool is intended to be published as an open-source project. Nothing in the
repository may contain a real email address, tenant ID, client ID, folder
contents, or connection string. All user-specific values live in a git-ignored
local configuration file.

### Why not an existing tool

- Microsoft's built-in IMAP migration copies everything once and never deletes
  from Gmail.
- imapsync is close but its paid binary, Perl runtime, and Gmail label
  semantics make "delete from source" unreliable.
- MailStore Home archives locally and does not delete from Gmail or verify per
  message.
- Manual drag-and-drop in a mail client has no verification and, because Gmail
  folders are labels, "moving" a message out of a label leaves it in All Mail
  still consuming storage.

### Gmail storage semantics (the core problem)

Over IMAP, Gmail exposes labels as folders. Removing a message from a label
folder only removes the label; the message remains in `[Gmail]/All Mail` and
still counts toward storage. The only way to free space is to move the message
into `[Gmail]/Trash` and either expunge it or wait for Gmail's 30-day Trash
auto-purge. Trash counts toward storage until emptied.

## 2. Decisions already made

| Topic | Decision |
|---|---|
| Language / runtime | C#, .NET 10 console app |
| Mail protocol | IMAP on both sides via MailKit |
| Gmail auth | App password (2-Step Verification required), stored in Windows Credential Manager |
| Microsoft 365 auth | OAuth2 delegated `IMAP.AccessAsUser.All` via MSAL, public client, interactive browser sign-in once, encrypted local token cache |
| Ledger | SQL Server (local instance), EF Core, one row per message |
| Logging | Serilog, one log file per run plus console |
| Target folders | `Gmail Archive` for received mail, `Gmail Archive/Sent` for sent mail. Flat otherwise. |
| Selection | Rolling window: internal date older than (run date minus N years, default 5). Oldest first. |
| Exclusions | `[Gmail]/Spam` and `[Gmail]/Trash`. Chats are included. |
| Delete behavior | Move to `[Gmail]/Trash`. Expunge only with `--purge`. Default off. |
| Per-run caps | `--max-messages` and `--max-bytes`, both optional |
| Prior attempts | Reconcile step scans target folders and records existing Message-IDs so previously copied messages are not duplicated |
| Scheduling | Windows Task Scheduler, monthly |
| Reporting | Log file and database only. No email summary. |
| Why not Microsoft Graph | Graph's MIME upload is capped around 4 MB, which rejects messages with large attachments. IMAP APPEND has no such limit. |

## 3. Architecture

```
EmailArchive.sln
  src/EmailArchive.Cli        console entry point, argument parsing, mode dispatch
  src/EmailArchive.Core       all logic; no console I/O
  tests/EmailArchive.Tests    xUnit tests; IMAP behind interfaces, no network
  docs/                       this spec, README setup guides
```

### Core components

| Component | Responsibility | Depends on |
|---|---|---|
| `IMailSource` | Enumerate eligible Gmail messages, fetch raw message, trash, expunge | MailKit (Gmail impl) |
| `IMailTarget` | Ensure folders exist, append message, search by Message-ID, list folder headers | MailKit (M365 impl) |
| `MessageSelector` | Apply window, exclusions, ordering, caps to a header list | none |
| `FolderRouter` | Decide target folder (Sent vs Archive) from Gmail labels/flags | none |
| `Verifier` | Compare source headers to target search result | none |
| `MoveWorkflow` | Per-message state machine, ledger updates, failure policy | source, target, ledger, verifier |
| `ReconcileWorkflow` | Scan target folders into ledger | target, ledger |
| `ReportWorkflow` | Dry run: selection plus counts and sizes | source, ledger |
| `Ledger` (EF Core) | Persistence for messages and runs | SQL Server |
| `CredentialStore` | Read/write Gmail app password in Windows Credential Manager | Win32 CredRead/CredWrite |
| `MicrosoftAuth` | MSAL public client, token cache, acquire IMAP token | MSAL + cache extension |

The CLI project contains no business logic. Every workflow is constructible with
fake source/target/ledger implementations for testing.

### Data flow (move mode)

1. Load config, open ledger, open Gmail and M365 IMAP sessions.
2. Ensure target folders exist.
3. Fetch envelope headers from `[Gmail]/All Mail` for messages older than the cutoff.
4. `MessageSelector` orders oldest first and applies caps, skipping messages the ledger marks `Trashed` or `Purged`.
5. For each message run the per-message pipeline (section 5).
6. Write run summary row, print summary, exit code 0 on success, non-zero otherwise.

## 4. Configuration

`appsettings.json` (committed) holds defaults and placeholders only.
`appsettings.local.json` (git-ignored) overrides with real values.

```json
{
  "Gmail": {
    "Address": "you@gmail.com",
    "ImapHost": "imap.gmail.com",
    "ImapPort": 993
  },
  "Microsoft365": {
    "Address": "you@yourdomain.com",
    "TenantId": "00000000-0000-0000-0000-000000000000",
    "ClientId": "00000000-0000-0000-0000-000000000000",
    "ImapHost": "outlook.office365.com",
    "ImapPort": 993,
    "ArchiveFolder": "Gmail Archive",
    "SentFolder": "Gmail Archive/Sent"
  },
  "Selection": {
    "KeepYears": 5,
    "ExcludeChats": false
  },
  "Ledger": {
    "ConnectionString": "Server=localhost;Database=EmailArchive;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "Logging": {
    "Directory": "logs"
  }
}
```

The Gmail app password is never in any file. On first use the app prompts for
it and stores it in Windows Credential Manager under a target name derived from
the Gmail address. `EmailArchive credentials set-gmail` and
`credentials clear` manage it explicitly.

The MSAL token cache is written by `Microsoft.Identity.Client.Extensions.Msal`
to the user's local app data folder, encrypted with DPAPI.

## 5. Per-message pipeline

Each message moves through these ledger states. Every transition is written to
the database before the next network call so a crash leaves a resumable record.

```
Pending -> AlreadyInTarget -> Verified -> Trashed -> Purged
Pending -> Copied          -> Verified -> Trashed -> Purged
any     -> Failed (with reason)
```

1. **Lookup.** Find or create the ledger row by Gmail Message-ID (fallback key
   described below). If state is `Trashed` or `Purged`, skip.
2. **Reconcile check.** If the ledger says the Message-ID already exists in the
   target folder (from a prior `reconcile` run or an earlier move), mark
   `AlreadyInTarget` and go to step 5.
3. **Fetch.** Download the raw RFC 2822 message, Gmail labels, flags, and
   internal date from Gmail.
4. **Append.** `FolderRouter` picks the folder (`\Sent` label or
   `[Gmail]/Sent Mail` membership means the Sent folder). Append to M365
   preserving internal date, `\Seen`, and `\Flagged`. Mark `Copied`.
5. **Verify.** Search the target folder for the Message-ID header. Require
   at least one match whose Date, From, and Subject match the source within
   normalization (whitespace, encoding); several matches verify against the
   lowest UID with a warning. Record both sizes; log a warning if
   they differ by more than 10 percent, but do not fail on size alone because
   Exchange re-encodes MIME on storage. On success mark `Verified`; on failure
   mark `Failed` and leave Gmail untouched.
   With `--dedupe`, extra matching copies whose body (everything below the
   header block) is byte-identical to the survivor are moved to Deleted Items
   in the target mailbox before step 6. A removal failure is logged and the
   message still proceeds, since it is verified either way.
6. **Trash.** Move the Gmail message to `[Gmail]/Trash`. Mark `Trashed`.
7. **Purge.** Only if `--purge`: expunge from Trash. Mark `Purged`.

### Message-ID fallback

Some old messages lack a Message-ID header. For these the ledger key is a hash
of normalized Date, From, and Subject, and verification matches on those three
fields only. The ledger records which key type was used.

### Failure policy

- A verify failure skips that message only. Verification needs at least one copy in
  the target whose headers match; several matching copies (a manual attempt that
  copied twice, or a lagging search after our own append) still verify, against the
  lowest UID, with a warning naming the copy count.
- A network or IMAP error triggers one reconnect and retry of the current step.
- Three consecutive message failures abort the run with a non-zero exit code.
  Only unexpected errors (exceptions) count toward the abort; a verification failure,
  a purge failure after a successful trash, or a skipped fallback-key duplicate is
  recorded and the run continues.
- No Gmail message is trashed unless its ledger row is `Verified` in the same
  or an earlier run.

## 6. Modes and command line

```
EmailArchive report      [--keep-years N] [--max-messages N] [--max-bytes N]
EmailArchive reconcile
EmailArchive move        [--keep-years N] [--max-messages N] [--max-bytes N] [--purge] [--dedupe]
EmailArchive dedupe      [--apply]
EmailArchive credentials set-gmail | clear
EmailArchive login       (interactive Microsoft sign-in, refreshes token cache)
```

- `report`: read-only on both sides. Prints count, total bytes, date range,
  and how many are already in target per the ledger.
- `reconcile`: reads target folders only. Upserts ledger rows as
  `AlreadyInTarget` with target UID and size. Never touches Gmail.
- `move`: the pipeline above.
- `dedupe`: reads target folders only. Groups messages by Message-ID; within a
  group, copies whose Date, From and Subject match the survivor (the ledger's
  TargetUid if it is in the group, else the lowest UID) and whose body hash
  equals the survivor's are moved to Deleted Items. Dry run unless `--apply`.
  Messages without a Message-ID are never grouped. Never touches Gmail.
- Exit codes: 0 success, 1 partial (some failures), 2 aborted, 3 auth required.

When the Microsoft token cannot be refreshed silently, every mode exits with
code 3 and a message telling the user to run `EmailArchive login`. Nothing is
modified.

## 7. Ledger schema

**Messages**

| Column | Notes |
|---|---|
| Id | PK |
| KeyType | `MessageId` or `Fallback` |
| Key | Message-ID or fallback hash, unique |
| GmailUid | last known UID in All Mail |
| InternalDate | from Gmail |
| FromAddress, Subject | for matching and auditing |
| IsSent | routing result |
| SourceSize, TargetSize | bytes |
| TargetFolder, TargetUid | after verify |
| State | enum from section 5 |
| FailureReason | nullable |
| CopiedAt, VerifiedAt, TrashedAt, PurgedAt | nullable timestamps |
| LastRunId | FK |

**Runs**

| Column | Notes |
|---|---|
| Id | PK |
| Mode | report / reconcile / move / dedupe |
| StartedAt, FinishedAt | |
| CutoffDate | computed from KeepYears |
| Selected, Copied, AlreadyInTarget, Verified, Trashed, Purged, Failed | counts |
| DuplicatesRemoved | target copies moved to Deleted Items (or that would be, in a dedupe dry run) |
| ExitCode | |

Schema is created by EF Core migrations; `dotnet ef database update` or the
app's `--migrate` flag on first run.

## 8. Testing

Unit tests, no network:
- `MessageSelector`: cutoff boundary, oldest-first ordering, both caps,
  exclusion of ledger-completed rows.
- `FolderRouter`: Sent detection from labels and flags.
- `Verifier`: exact match, normalization cases, duplicate copies verify
  against the lowest UID, size warning threshold, fallback key matching.
- `DuplicatePlan` / `DuplicateRemover`: survivor selection (ledger UID, else
  lowest), header mismatches excluded, body-hash mismatch kept, dry run moves
  nothing, removal failures counted.
- `DedupeWorkflow`: grouping by Message-ID, both folders, ledger-referenced
  survivor, no-Message-ID messages ignored, exit codes.
- `MoveWorkflow`: every state transition with fake source/target/ledger,
  including crash-resume from each intermediate state and the three-failure
  abort.
- `ReconcileWorkflow`: upsert behavior and no-source-contact guarantee.

Supervised first use against real accounts, in order:
1. `report` to confirm selection and counts.
2. `reconcile` to load existing target contents.
3. `move --max-messages 20` and inspect results in Outlook and Gmail Trash.
4. `move` with a larger cap, repeated until the backlog is done.
5. Empty Gmail Trash manually, or run with `--purge` once trusted.

## 9. One-time setup (documented in README)

1. **Gmail**: enable 2-Step Verification, create an app password, run
   `EmailArchive credentials set-gmail`.
2. **Microsoft Entra**: register a public client app in the user's own tenant
   with delegated permission `IMAP.AccessAsUser.All` (Office 365 Exchange
   Online), redirect URI `http://localhost`, "Allow public client flows"
   enabled. Copy Tenant ID and Client ID into `appsettings.local.json`.
   Click-through steps go in the README.
3. **SQL Server**: run migrations to create the database.
4. **Task Scheduler**: register a monthly task running
   `EmailArchive move --purge`, documented with a ready-to-paste `schtasks`
   command.

## 10. Out of scope

- Calendar, contacts, Google Drive.
- Recreating Gmail labels as folders.
- Moving mail in the reverse direction.
- Email summaries or notifications after a run.
- Any GUI.

## 11. Open risks

- Exchange MIME re-encoding: verification deliberately ignores size. If testing
  shows size is stable, tighten to a size match.
- Gmail IMAP rate limits and the 2,500 MB per day IMAP download bandwidth
  limit: large backlogs may need several runs; the caps make this routine.
- MSAL token expiry after 90 days of inactivity: handled by exit code 3 and
  `login`.
- Exchange Online's receive size limit (36 MB by default) rejects very large messages;
  they fail verification every run and stay in Gmail.
- A Gmail UIDVALIDITY reset makes earlier fallback-key rows refuse their messages
  until the ledger is cleaned.
