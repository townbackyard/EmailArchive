using EmailArchive.Core.Config;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace EmailArchive.Core.Auth;

/// <summary>
/// MSAL public client for delegated IMAP access. The token cache is encrypted with DPAPI in
/// %LOCALAPPDATA%\EmailArchive by the MSAL cache extension. Refresh tokens stay valid while used
/// at least every 90 days; a monthly run keeps them alive.
/// </summary>
public sealed class MicrosoftAuth(Microsoft365Settings settings)
{
    private static readonly string[] Scopes =
    [
        "email",
        "offline_access",
        "https://outlook.office.com/IMAP.AccessAsUser.All",
    ];

    private IPublicClientApplication? _app;

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        if (_app is not null) return _app;

        var app = PublicClientApplicationBuilder
            .Create(settings.ClientId)
            .WithTenantId(settings.TenantId)
            .WithRedirectUri("http://localhost")
            .Build();

        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmailArchive");
        var storage = new StorageCreationPropertiesBuilder("msal_token_cache.bin", cacheDir).Build();
        var cacheHelper = await MsalCacheHelper.CreateAsync(storage);
        cacheHelper.RegisterCache(app.UserTokenCache);

        return _app = app;
    }

    public async Task<(string Username, string AccessToken)> AcquireTokenSilentAsync(CancellationToken ct)
    {
        var app = await GetAppAsync();
        var accounts = await app.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a => string.Equals(a.Username, settings.Address, StringComparison.OrdinalIgnoreCase))
                      ?? accounts.FirstOrDefault();
        if (account is null)
            throw new AuthRequiredException("No cached Microsoft sign-in. Run: EmailArchive login");

        try
        {
            var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            return (result.Account.Username, result.AccessToken);
        }
        catch (MsalUiRequiredException ex)
        {
            throw new AuthRequiredException("Microsoft sign-in has expired. Run: EmailArchive login", ex);
        }
    }

    public async Task<(string Username, string AccessToken)> AcquireTokenInteractiveAsync(CancellationToken ct)
    {
        var app = await GetAppAsync();
        var result = await app.AcquireTokenInteractive(Scopes)
            .WithLoginHint(settings.Address)
            .ExecuteAsync(ct);
        return (result.Account.Username, result.AccessToken);
    }
}
