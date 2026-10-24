using EmailArchive.Core.Auth;
using EmailArchive.Core.Config;
using EmailArchive.Core.Credentials;
using EmailArchive.Core.Ledger;
using EmailArchive.Core.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace EmailArchive.Cli;

/// <summary>Loads configuration, sets up logging, builds the pieces a command needs, and maps exceptions to exit codes.</summary>
public sealed class AppHost : IAsyncDisposable
{
    public AppSettings Settings { get; }
    public ILoggerFactory LoggerFactory { get; }
    public MicrosoftAuth Auth { get; }

    private EmailArchiveDbContext? _db;

    private AppHost(AppSettings settings, ILoggerFactory loggerFactory)
    {
        Settings = settings;
        LoggerFactory = loggerFactory;
        Auth = new MicrosoftAuth(settings.Microsoft365);
    }

    public static AppHost Create(bool validate)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.local.json", optional: true)
            .Build();

        var settings = configuration.Get<AppSettings>() ?? new AppSettings();
        if (validate) settings.Validate();

        var logDir = Path.IsPathRooted(settings.Logging.Directory)
            ? settings.Logging.Directory
            : Path.Combine(AppContext.BaseDirectory, settings.Logging.Directory);
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logFile, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return new AppHost(settings, new SerilogLoggerFactory(Log.Logger));
    }

    /// <summary>Runs a command with exit-code mapping. Pass validate=false for commands that only need settings.</summary>
    public static async Task<int> RunAsync(bool validate, Func<AppHost, CancellationToken, Task<int>> command, CancellationToken ct)
    {
        try
        {
            await using var host = Create(validate);
            try
            {
                return await command(host, ct);
            }
            catch (AuthRequiredException ex)
            {
                Log.Error(ex.Message);
                return 3;
            }
            catch (ConfigurationException ex)
            {
                Log.Error(ex.Message);
                return 2;
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Cancelled");
                return 2;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Run aborted");
                return 2;
            }
        }
        catch (ConfigurationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    public async Task<ILedger> CreateLedgerAsync(CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<EmailArchiveDbContext>()
            .UseSqlServer(Settings.Ledger.ConnectionString)
            .Options;
        _db = new EmailArchiveDbContext(options);
        await _db.Database.MigrateAsync(ct);
        return new SqlLedger(_db);
    }

    public IMailSource CreateSource()
    {
        var password = CredentialStore.ReadGmailAppPassword(Settings.Gmail.Address)
            ?? throw new ConfigurationException(
                $"No Gmail app password stored for {Settings.Gmail.Address}. Run: EmailArchive credentials set-gmail");
        return new GmailSource(Settings.Gmail, password, LoggerFactory.CreateLogger<GmailSource>());
    }

    public IMailTarget CreateTarget() =>
        new M365Target(Settings.Microsoft365, Auth, LoggerFactory.CreateLogger<M365Target>());

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
    }
}
