using EmailArchive.Core.Config;

namespace EmailArchive.Tests.Config;

public class AppSettingsTests
{
    private static AppSettings Valid() => new()
    {
        Gmail = new GmailSettings { Address = "someone@gmail.com" },
        Microsoft365 = new Microsoft365Settings
        {
            Address = "someone@example.org",
            TenantId = "11111111-2222-3333-4444-555555555555",
            ClientId = "66666666-7777-8888-9999-000000000000",
        },
        Ledger = new LedgerSettings { ConnectionString = "Server=localhost;Database=X;Trusted_Connection=True" },
    };

    [Fact]
    public void Valid_settings_pass()
    {
        Valid().Validate();
    }

    [Fact]
    public void Placeholder_tenant_id_is_rejected()
    {
        var s = Valid();
        s.Microsoft365.TenantId = "00000000-0000-0000-0000-000000000000";
        var ex = Assert.Throws<ConfigurationException>(() => s.Validate());
        Assert.Contains("TenantId", ex.Message);
    }

    [Fact]
    public void Placeholder_addresses_are_rejected()
    {
        var s = Valid();
        s.Gmail.Address = "you@gmail.com";
        s.Microsoft365.Address = "you@yourdomain.com";
        var ex = Assert.Throws<ConfigurationException>(() => s.Validate());
        Assert.Contains("Gmail:Address", ex.Message);
        Assert.Contains("Microsoft365:Address", ex.Message);
    }

    [Fact]
    public void Negative_keep_years_is_rejected()
    {
        var s = Valid();
        s.Selection.KeepYears = -1;
        Assert.Throws<ConfigurationException>(() => s.Validate());
    }

    [Fact]
    public void Defaults_are_sensible()
    {
        var s = new AppSettings();
        Assert.Equal("imap.gmail.com", s.Gmail.ImapHost);
        Assert.Equal(993, s.Gmail.ImapPort);
        Assert.Equal("outlook.office365.com", s.Microsoft365.ImapHost);
        Assert.Equal("Gmail Archive", s.Microsoft365.ArchiveFolder);
        Assert.Equal("Gmail Archive/Sent", s.Microsoft365.SentFolder);
        Assert.Equal(5, s.Selection.KeepYears);
        Assert.False(s.Selection.ExcludeChats);
        Assert.Equal("logs", s.Logging.Directory);
    }
}
