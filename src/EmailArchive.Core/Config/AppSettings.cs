namespace EmailArchive.Core.Config;

public sealed class AppSettings
{
    public GmailSettings Gmail { get; set; } = new();
    public Microsoft365Settings Microsoft365 { get; set; } = new();
    public SelectionSettings Selection { get; set; } = new();
    public LedgerSettings Ledger { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();

    private const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

    public void Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Gmail.Address) || Gmail.Address == "you@gmail.com")
            problems.Add("Gmail:Address must be your real Gmail address");
        if (string.IsNullOrWhiteSpace(Microsoft365.Address) || Microsoft365.Address == "you@yourdomain.com")
            problems.Add("Microsoft365:Address must be your real Microsoft 365 address");
        if (!Guid.TryParse(Microsoft365.TenantId, out _) || Microsoft365.TenantId == ZeroGuid)
            problems.Add("Microsoft365:TenantId must be the Directory (tenant) ID from your app registration");
        if (!Guid.TryParse(Microsoft365.ClientId, out _) || Microsoft365.ClientId == ZeroGuid)
            problems.Add("Microsoft365:ClientId must be the Application (client) ID from your app registration");
        if (string.IsNullOrWhiteSpace(Microsoft365.ArchiveFolder))
            problems.Add("Microsoft365:ArchiveFolder must not be empty");
        if (string.IsNullOrWhiteSpace(Microsoft365.SentFolder))
            problems.Add("Microsoft365:SentFolder must not be empty");
        if (Selection.KeepYears < 0)
            problems.Add("Selection:KeepYears must be zero or more");
        if (string.IsNullOrWhiteSpace(Ledger.ConnectionString))
            problems.Add("Ledger:ConnectionString must not be empty");

        if (problems.Count > 0)
            throw new ConfigurationException(
                "Configuration problems (fix them in appsettings.local.json):\n  " + string.Join("\n  ", problems));
    }
}

public sealed class GmailSettings
{
    public string Address { get; set; } = "you@gmail.com";
    public string ImapHost { get; set; } = "imap.gmail.com";
    public int ImapPort { get; set; } = 993;
}

public sealed class Microsoft365Settings
{
    public string Address { get; set; } = "you@yourdomain.com";
    public string TenantId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string ClientId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string ImapHost { get; set; } = "outlook.office365.com";
    public int ImapPort { get; set; } = 993;
    public string ArchiveFolder { get; set; } = "Gmail Archive";
    public string SentFolder { get; set; } = "Gmail Archive/Sent";
}

public sealed class SelectionSettings
{
    public int KeepYears { get; set; } = 5;
    public bool ExcludeChats { get; set; }
}

public sealed class LedgerSettings
{
    public string ConnectionString { get; set; } =
        "Server=localhost;Database=EmailArchive;Trusted_Connection=True;TrustServerCertificate=True";
}

public sealed class LoggingSettings
{
    public string Directory { get; set; } = "logs";
}
