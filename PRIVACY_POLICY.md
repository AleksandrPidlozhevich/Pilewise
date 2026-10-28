# Privacy Policy

Last updated: 28 October 2026

Pilewise is a desktop app for Windows, macOS and Linux. It reads the games you own on Steam,
Epic, GOG and Xbox and writes the list to a place you choose: a Notion database, a Google Sheets
spreadsheet or a CSV file. This policy explains what happens to your data.

## Short version

Pilewise has no server of its own. The developer does not receive, collect, store or share any
of your data. Everything described below happens on your computer, between it and the services
you connect.

## What the app accesses

- **Game libraries.** Using the Steam API key and Steam ID you enter, and the logins you
  complete for Epic, GOG and Xbox, the app reads the list of games you own (titles, store IDs,
  playtime and last played date where the store provides them).
- **Notion.** If you use the Notion export, the app reads and writes the database you point it
  to, using the integration token you enter.
- **Google.** If you use the Google Sheets export, the app asks for one permission:
  `https://www.googleapis.com/auth/drive.file`. It lets Pilewise create a spreadsheet in your
  Google Drive and read and update that spreadsheet. Pilewise cannot see any other file in
  your Drive, including spreadsheets you created yourself.

## What is stored, and where

Everything is stored on your device, in the app's data folder:

- Refresh tokens (including the Google token), API keys and the settings you chose to
  remember are encrypted with the operating system's secure storage (DPAPI on Windows,
  Keychain on macOS, libsecret on Linux).
- Logs and app state (window position, sync history, hidden games) are kept in plain files in
  the same folder.
- The optional "Save settings" feature writes a plain-text file to a location you choose.

## Who receives your data

Nobody besides the services you connect. The app sends requests only to Steam, Epic, GOG,
Xbox / Microsoft, Notion and Google, and only to read your library or write your backlog to the
place you selected. It also asks GitHub for new app releases. The developer has no access to
any of this.

There is no analytics, advertising or telemetry. Data is not sold, shared or transferred to any
third party.

## Google user data

Pilewise's use and transfer of information received from Google APIs adheres to the
[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements.

- Data from your Google account is used only to create and update the spreadsheet you asked
  for.
- It is not transferred to anyone, not used for advertising, and not read by people.

## Deleting your data and revoking access

- To revoke Pilewise's access to Google, open
  [myaccount.google.com/permissions](https://myaccount.google.com/permissions) and remove
  Pilewise.
- To remove everything the app stored, uninstall it and delete its data folder
  (`%AppData%\Pilewise` on Windows, `~/Library/Application Support/Pilewise` on macOS,
  `~/.config/Pilewise` on Linux).
- Spreadsheets and Notion pages the app wrote are yours; delete them in Google Drive or Notion.

## Changes

If this policy changes, the new version is published here with a new date.

## Contact

Questions about this policy: aleksandr.pidlozhevich@gmail.com
