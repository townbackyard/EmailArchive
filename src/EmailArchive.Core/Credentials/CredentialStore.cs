using System.Runtime.Versioning;
using Meziantou.Framework.Win32;

namespace EmailArchive.Core.Credentials;

/// <summary>
/// Stores the Gmail app password in Windows Credential Manager (Control Panel > Credential Manager > Windows Credentials).
/// Entries are per Windows user, so a scheduled task running as the same user can read them.
/// </summary>
[SupportedOSPlatform("windows5.1.2600")]
public static class CredentialStore
{
    private static string TargetName(string address) => $"EmailArchive:Gmail:{address}";

    public static string? ReadGmailAppPassword(string address) =>
        CredentialManager.ReadCredential(TargetName(address))?.Password;

    public static void WriteGmailAppPassword(string address, string password) =>
        CredentialManager.WriteCredential(TargetName(address), address, password, CredentialPersistence.LocalMachine);

    public static void ClearGmailAppPassword(string address)
    {
        if (CredentialManager.ReadCredential(TargetName(address)) is not null)
            CredentialManager.DeleteCredential(TargetName(address));
    }
}
