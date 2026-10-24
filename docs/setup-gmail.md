# Gmail setup

EmailArchive signs in to Gmail over IMAP with an app password. App passwords require 2-Step Verification.

1. Open https://myaccount.google.com/security and turn on **2-Step Verification** if it is off.
2. Open https://myaccount.google.com/apppasswords.
3. Enter the name `EmailArchive` and click **Create**.
4. Copy the 16-character password shown. Google shows it only once.
5. In a terminal, from the folder containing `EmailArchive.exe`, run:

       EmailArchive credentials set-gmail

   Paste the password when prompted. Spaces are removed automatically. It is stored in
   Windows Credential Manager under `EmailArchive:Gmail:<your address>`, not in any file.

6. Confirm IMAP access: in Gmail, open **Settings > See all settings > Forwarding and POP/IMAP**.
   On current Gmail accounts IMAP is always on. If you see an "Enable IMAP" option, enable it.
7. Under **Settings > Labels**, confirm **All Mail** and **Trash** have **Show in IMAP** checked.

The "Auto-Expunge" and "When a message is marked as deleted" options on the IMAP settings page do not
affect EmailArchive: it moves messages to Trash explicitly rather than relying on the delete flag.

To revoke access later, delete the app password at https://myaccount.google.com/apppasswords and run
`EmailArchive credentials clear`.
