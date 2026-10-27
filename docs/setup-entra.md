# Microsoft Entra app registration

Exchange Online no longer accepts passwords over IMAP, so EmailArchive signs in with OAuth2 through an
app registration in your own tenant. You need to be a global administrator (or application administrator)
of the Microsoft 365 tenant that owns the destination mailbox. No one outside your tenant is involved.

## Register the application

1. Open https://entra.microsoft.com and sign in as the tenant administrator.
2. Go to **Identity > Applications > App registrations** and click **New registration**.
3. Name: `EmailArchive`.
4. Supported account types: **Accounts in this organizational directory only**.
5. Redirect URI: choose platform **Public client/native (mobile & desktop)** and enter `http://localhost`.
6. Click **Register**.
7. On the **Overview** page, copy the **Application (client) ID** and the **Directory (tenant) ID**.

## Allow the desktop sign-in flow

1. In the registration, open **Authentication**.
2. Under **Advanced settings**, set **Allow public client flows** to **Yes**.
3. Click **Save**.

## Grant IMAP permission

1. Open **API permissions** and click **Add a permission**.
2. On the **Microsoft APIs** tab choose **Microsoft Graph**, then **Delegated permissions**.
3. Type `IMAP` in the search box, expand the **IMAP** group, tick **IMAP.AccessAsUser.All** and click
   **Add permissions**.
4. Click **Grant admin consent for <your tenant>** and confirm.

The permission lives under Microsoft Graph in the portal even though EmailArchive requests it as the
Exchange scope `https://outlook.office.com/IMAP.AccessAsUser.All`; that is how Microsoft lists it. The
**Office 365 Exchange Online** API only shows the application-level `IMAP.AccessAsApp` permission, which
EmailArchive does not use.

The permission list should now show `IMAP.AccessAsUser.All` with a green "Granted" status, alongside
the default `User.Read`.

## Configure EmailArchive

Create `appsettings.local.json` next to `appsettings.json` (this file is git-ignored):

    {
      "Gmail": { "Address": "you@gmail.com" },
      "Microsoft365": {
        "Address": "you@yourdomain.com",
        "TenantId": "<Directory (tenant) ID>",
        "ClientId": "<Application (client) ID>"
      }
    }

Replace the two addresses with your real ones. Then run:

    EmailArchive login

A browser opens for Microsoft sign-in. Sign in as the destination mailbox owner and accept the consent
prompt. The token is cached encrypted in `%LOCALAPPDATA%\EmailArchive\msal_token_cache.bin` and renews
itself as long as EmailArchive runs at least every 90 days.
