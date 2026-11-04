using System.CommandLine;
using EmailArchive.Cli;
using EmailArchive.Core.Model;

var keepYears = new Option<int?>("--keep-years") { Description = "Keep this many years in Gmail (overrides Selection:KeepYears)" };
keepYears.Validators.Add(r => { if (r.GetValueOrDefault<int?>() is < 0) r.AddError("--keep-years must be at least 0"); });
var maxMessages = new Option<int?>("--max-messages") { Description = "Stop after this many messages" };
maxMessages.Validators.Add(r => { if (r.GetValueOrDefault<int?>() is < 1) r.AddError("--max-messages must be at least 1"); });
var maxBytes = new Option<long?>("--max-bytes") { Description = "Stop before exceeding this many bytes (the first selected message always moves, even if larger)" };
maxBytes.Validators.Add(r => { if (r.GetValueOrDefault<long?>() is < 1) r.AddError("--max-bytes must be at least 1"); });
var purge = new Option<bool>("--purge") { Description = "Expunge trashed messages from Gmail immediately (default: leave in Trash)" };
var dedupe = new Option<bool>("--dedupe") { Description = "When a message already has several identical copies in Microsoft 365, move the extras to Deleted Items" };
var apply = new Option<bool>("--apply") { Description = "Actually move duplicates to Deleted Items (default: dry run)" };

var report = new Command("report", "Dry run: show what would move. Changes nothing.") { keepYears, maxMessages, maxBytes };
report.SetAction((parse, ct) => AppHost.RunAsync(validate: true,
    (host, token) => Commands.ReportAsync(host, parse.GetValue(keepYears), parse.GetValue(maxMessages), parse.GetValue(maxBytes), token), ct));

var reconcile = new Command("reconcile", "Index messages already in the Microsoft 365 target folders. Never touches Gmail.");
reconcile.SetAction((parse, ct) => AppHost.RunAsync(validate: true, Commands.ReconcileAsync, ct));

var move = new Command("move", "Copy, verify, then trash the oldest Gmail messages.") { keepYears, maxMessages, maxBytes, purge, dedupe };
move.SetAction((parse, ct) => AppHost.RunAsync(validate: true,
    (host, token) => Commands.MoveAsync(host, parse.GetValue(keepYears), parse.GetValue(maxMessages), parse.GetValue(maxBytes), parse.GetValue(purge), parse.GetValue(dedupe), token), ct));

var dedupeCommand = new Command("dedupe", "Find messages stored more than once in the Microsoft 365 target folders and move identical extras to Deleted Items. Never touches Gmail.") { apply };
dedupeCommand.SetAction((parse, ct) => AppHost.RunAsync(validate: true, (host, token) => Commands.DedupeAsync(host, parse.GetValue(apply), token), ct));

var inspectUidA = new Argument<uint>("uid-a") { Description = "UID of the first copy in the target folder" };
var inspectUidB = new Argument<uint>("uid-b") { Description = "UID of the second copy" };
var inspectFolder = new Option<TargetFolder>("--folder") { Description = "Which target folder holds the UIDs", DefaultValueFactory = _ => TargetFolder.Archive };
var inspect = new Command("inspect", "Diagnostic: download two copies from a Microsoft 365 target folder and show how their MIME structure and bodies differ. Read-only; never touches Gmail.")
    { inspectUidA, inspectUidB, inspectFolder };
inspect.SetAction((parse, ct) => AppHost.RunAsync(validate: true,
    (host, token) => Commands.InspectAsync(host, parse.GetValue(inspectFolder), parse.GetValue(inspectUidA), parse.GetValue(inspectUidB), token), ct));

var login = new Command("login", "Sign in to Microsoft interactively and cache the token.");
login.SetAction((parse, ct) => AppHost.RunAsync(validate: true, Commands.LoginAsync, ct));

var setGmail = new Command("set-gmail", "Store the Gmail app password in Windows Credential Manager.");
setGmail.SetAction((parse, ct) => AppHost.RunAsync(validate: false, (host, _) => Commands.SetGmailCredentialAsync(host), ct));

var clear = new Command("clear", "Remove the stored Gmail app password.");
clear.SetAction((parse, ct) => AppHost.RunAsync(validate: false, (host, _) => Commands.ClearCredentialAsync(host), ct));

var credentials = new Command("credentials", "Manage stored secrets.") { setGmail, clear };

var root = new RootCommand("Move the oldest Gmail messages into a Microsoft 365 mailbox, verify, then trash them in Gmail.")
{
    report, reconcile, move, dedupeCommand, inspect, login, credentials,
};

return await root.Parse(args).InvokeAsync();
