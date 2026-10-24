# Running monthly with Task Scheduler

## Publish the app to a fixed folder

From the repository root:

    dotnet publish src/EmailArchive.Cli -c Release -o C:\Tools\EmailArchive

Copy your `appsettings.local.json` into `C:\Tools\EmailArchive` as well. Logs go to
`C:\Tools\EmailArchive\logs`.

## Create the task

Run in a PowerShell window, after a successful manual `move` run:

    schtasks /Create /TN "EmailArchive Monthly" /SC MONTHLY /D 1 /ST 03:00 /TR "\"C:\Tools\EmailArchive\EmailArchive.exe\" move --purge" /RL LIMITED /F

`--purge` expunges each message from Gmail Trash immediately after it is verified in Microsoft 365, with no recovery window. Leave it off if you want the 30-day Trash safety net.

This runs on the first of every month at 03:00 as the current user, with "Run only when user is logged
on" semantics. The Gmail app password in Credential Manager and the Microsoft token cache both belong
to that user, so no secrets need to be entered into the task.

If you prefer the task to run while logged off, open Task Scheduler, edit the task, choose
**Run whether user is logged on or not**, and enter your Windows password. Credential Manager and the
token cache remain readable because the task still runs as you.

## Checking results

- Each run writes `logs\run-<timestamp>.log`.
- The `Runs` table in the `EmailArchive` database has one row per run with counts and the exit code.
- Exit code 3 means the Microsoft sign-in expired. Run `EmailArchive login` once by hand.
- Exit code 1 with a `duplicate fallback key` warning in the log means a message was skipped for manual review; see the README.

To remove the task:

    schtasks /Delete /TN "EmailArchive Monthly" /F
