using EmailArchive.Core.Config;
using EmailArchive.Core.Credentials;
using EmailArchive.Core.Dedupe;
using EmailArchive.Core.Model;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Workflows;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace EmailArchive.Cli;

public static class Commands
{
    public static async Task<int> ReportAsync(AppHost host, int? keepYears, int? maxMessages, long? maxBytes, CancellationToken ct)
    {
        var options = Selection(host, keepYears, maxMessages, maxBytes);
        var ledger = await host.CreateLedgerAsync(ct);
        await using var source = host.CreateSource();

        var workflow = new ReportWorkflow(source, ledger, host.LoggerFactory.CreateLogger<ReportWorkflow>());
        var summary = await workflow.RunAsync(options, ct);

        Console.WriteLine();
        Console.WriteLine($"Cutoff:            {options.Cutoff:yyyy-MM-dd} (keep {keepYears ?? host.Settings.Selection.KeepYears} years)");
        Console.WriteLine($"Would move:        {summary.Count:N0} messages, {summary.TotalBytes / 1_048_576.0:N1} MB");
        Console.WriteLine($"Of which sent:     {summary.SentCount:N0}");
        Console.WriteLine($"Already in target: {summary.AlreadyInTarget:N0} (per ledger; run reconcile to refresh)");
        Console.WriteLine($"Date range:        {summary.Oldest:yyyy-MM-dd} to {summary.Newest:yyyy-MM-dd}");
        Console.WriteLine("Nothing was changed.");
        return 0;
    }

    public static async Task<int> ReconcileAsync(AppHost host, CancellationToken ct)
    {
        var ledger = await host.CreateLedgerAsync(ct);
        await using var target = host.CreateTarget();

        var workflow = new ReconcileWorkflow(target, ledger, host.LoggerFactory.CreateLogger<ReconcileWorkflow>());
        var summary = await workflow.RunAsync(ct);

        Console.WriteLine();
        Console.WriteLine($"Scanned {summary.Scanned:N0} messages in the target folders, recorded {summary.Recorded:N0} in the ledger.");
        return 0;
    }

    public static async Task<int> DedupeAsync(AppHost host, bool apply, CancellationToken ct)
    {
        var ledger = await host.CreateLedgerAsync(ct);
        await using var target = host.CreateTarget();

        var workflow = new DedupeWorkflow(target, ledger, host.LoggerFactory.CreateLogger<DedupeWorkflow>());
        var summary = await workflow.RunAsync(apply, ct);

        Console.WriteLine();
        Console.WriteLine($"Messages stored more than once: {summary.Groups:N0}");
        Console.WriteLine($"{(apply ? "Moved to Deleted Items:" : "Would move to Deleted Items:")}  {summary.Removed:N0}");
        if (summary.Equivalent > 0)
            Console.WriteLine($"  of which not byte-identical:  {summary.Equivalent:N0} (re-wrapped text, re-tokenized links, or missing parts)");
        Console.WriteLine($"Kept (content differs):         {summary.ContentDiffers:N0}");
        Console.WriteLine($"Failed:                         {summary.Failed:N0}");
        Console.WriteLine(apply ? "Duplicates are in Deleted Items in Microsoft 365 until that folder is emptied." : "Nothing was changed. Pass --apply to move the duplicates to Deleted Items.");
        return summary.ExitCode;
    }

    /// <summary>Diagnostic: downloads two copies from a target folder and explains how they differ. Read-only.</summary>
    public static async Task<int> InspectAsync(AppHost host, TargetFolder folder, uint uidA, uint uidB, CancellationToken ct)
    {
        await using var target = host.CreateTarget();
        await target.ConnectAsync(ct);
        await target.EnsureFoldersAsync(ct);

        var reports = new List<IReadOnlyList<PartInfo>>();
        var bodies = new List<byte[]>();
        foreach (var uid in new[] { uidA, uidB })
        {
            var message = await target.DownloadAsync(folder, uid, ct);
            var body = await target.GetBodyBytesAsync(folder, uid, ct);
            if (message is null || body is null)
            {
                Console.Error.WriteLine($"UID {uid} not found in {folder}.");
                return 1;
            }

            var parts = MessageInspector.Describe(message);
            reports.Add(parts);
            bodies.Add(body);

            Console.WriteLine();
            Console.WriteLine($"UID {uid}: raw body {body.Length:N0} bytes, Date {message.Date:yyyy-MM-dd HH:mm:ss zzz}, Message-ID <{message.MessageId}>");
            var received = message.Headers.Count(h => h.Id == HeaderId.Received);
            var msHeaders = message.Headers.Count(h => h.Field.StartsWith("X-MS-", StringComparison.OrdinalIgnoreCase));
            var googleHeaders = message.Headers.Count(h => h.Field.StartsWith("X-Google-", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"  Headers: {message.Headers.Count} total, Received {received}, X-MS-* {msHeaders}, X-Google-* {googleHeaders}");
            foreach (var part in parts)
            {
                var indent = new string(' ', 2 * part.Path.Count(c => c == '.') + 2);
                var detail = part.DecodedHash is null
                    ? (part.Boundary is null ? "" : $" boundary=\"{part.Boundary}\"")
                    : $" {part.Encoding}, decoded {part.DecodedSize:N0} bytes, sha256 {part.DecodedHash[..12]}" + (part.NormalizedTextHash is null ? "" : $", no-whitespace {part.NormalizedTextHash[..12]}");
                var name = part.FileName is null ? "" : $" \"{part.FileName}\"";
                Console.WriteLine($"{indent}{part.Path} {part.ContentType}{name}{detail}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Comparison of UID {uidA} and UID {uidB}:");
        foreach (var line in MessageInspector.Compare(reports[0], reports[1]))
            Console.WriteLine($"  {line}");

        var diff = MessageInspector.FirstDifference(bodies[0], bodies[1]);
        if (diff is null)
            Console.WriteLine("  Raw bodies are byte-identical.");
        else
        {
            Console.WriteLine($"  Raw bodies first differ at byte {diff.Value.Offset:N0} of {bodies[0].Length:N0} / {bodies[1].Length:N0}:");
            Console.WriteLine($"    {uidA}: {diff.Value.Left}");
            Console.WriteLine($"    {uidB}: {diff.Value.Right}");
        }
        Console.WriteLine("Nothing was changed.");
        return 0;
    }

    public static async Task<int> MoveAsync(AppHost host, int? keepYears, int? maxMessages, long? maxBytes, bool purge, bool dedupe, CancellationToken ct)
    {
        var options = new MoveOptions(Selection(host, keepYears, maxMessages, maxBytes), purge, Dedupe: dedupe);
        var ledger = await host.CreateLedgerAsync(ct);
        await using var source = host.CreateSource();
        await using var target = host.CreateTarget();

        var workflow = new MoveWorkflow(source, target, ledger, host.LoggerFactory.CreateLogger<MoveWorkflow>());
        var summary = await workflow.RunAsync(options, ct);

        Console.WriteLine();
        Console.WriteLine($"Result: {summary}");
        if (!purge && summary.Trashed > 0)
            Console.WriteLine("Trashed messages stay in Gmail Trash for 30 days and still count toward storage. Empty Trash in Gmail by hand, or pass --purge on future runs to expunge as you go.");
        return summary.ExitCode;
    }

    public static async Task<int> LoginAsync(AppHost host, CancellationToken ct)
    {
        Console.WriteLine("A browser window will open for Microsoft sign-in...");
        var (username, _) = await host.Auth.AcquireTokenInteractiveAsync(ct);
        Console.WriteLine($"Signed in as {username}. The token is cached for future runs.");
        return 0;
    }

    public static Task<int> SetGmailCredentialAsync(AppHost host)
    {
        var address = host.Settings.Gmail.Address;
        if (string.IsNullOrWhiteSpace(address) || address == "you@gmail.com")
            throw new ConfigurationException("Gmail:Address must be set in appsettings.local.json before storing a password");
        Console.Write($"Enter the Gmail app password for {address} (input hidden): ");
        var password = ReadHidden();
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("No password entered.");
            return Task.FromResult(2);
        }
        CredentialStore.WriteGmailAppPassword(address, password.Replace(" ", ""));
        Console.WriteLine("Stored in Windows Credential Manager.");
        return Task.FromResult(0);
    }

    public static Task<int> ClearCredentialAsync(AppHost host)
    {
        CredentialStore.ClearGmailAppPassword(host.Settings.Gmail.Address);
        Console.WriteLine("Gmail app password removed from Windows Credential Manager.");
        Console.WriteLine("To forget the Microsoft sign-in, delete %LOCALAPPDATA%\\EmailArchive\\msal_token_cache.bin.");
        return Task.FromResult(0);
    }

    private static SelectionOptions Selection(AppHost host, int? keepYears, int? maxMessages, long? maxBytes)
    {
        var years = keepYears ?? host.Settings.Selection.KeepYears;
        var cutoff = CutoffCalculator.Compute(DateTimeOffset.UtcNow, years);
        return new SelectionOptions(cutoff, maxMessages, maxBytes, host.Settings.Selection.ExcludeChats);
    }

    private static string ReadHidden()
    {
        var chars = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Length > 0) chars.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) chars.Append(key.KeyChar);
        }
        Console.WriteLine();
        return chars.ToString();
    }
}
