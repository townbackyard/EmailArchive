using EmailArchive.Core.Config;
using EmailArchive.Core.Credentials;
using EmailArchive.Core.Selection;
using EmailArchive.Core.Workflows;
using Microsoft.Extensions.Logging;

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
        Console.WriteLine($"Kept (body differs):            {summary.BodiesDiffer:N0}");
        Console.WriteLine($"Failed:                         {summary.Failed:N0}");
        Console.WriteLine(apply ? "Duplicates are in Deleted Items in Microsoft 365 until that folder is emptied." : "Nothing was changed. Pass --apply to move the duplicates to Deleted Items.");
        return summary.ExitCode;
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
