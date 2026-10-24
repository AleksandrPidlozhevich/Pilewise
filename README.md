<div align="center">

<img src="Pilewise/Assets/Pilewise.png" alt="Pilewise logo" width="128" height="128">

# Pilewise

**Your whole game library, in one backlog.**

Pull your owned games from **Steam, Epic, GOG and Xbox** and sync them into **Notion**,
**Google Sheets** or a local **CSV**, so you can see what you've completed, what's in
progress, and what you never even started.

[![Latest release](https://img.shields.io/github/v/release/AleksandrPidlozhevich/Pilewise?style=for-the-badge&logo=github&label=Release)](https://github.com/AleksandrPidlozhevich/Pilewise/releases)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia 12](https://img.shields.io/badge/Avalonia-12-8B44AC?style=for-the-badge)](https://avaloniaui.net/)
[![No telemetry](https://img.shields.io/badge/No%20telemetry-brightgreen?style=for-the-badge)](#privacy)
[![Platforms](https://img.shields.io/badge/Windows%20%7C%20macOS%20%7C%20Linux-informational?style=for-the-badge&label=Platforms)](#download)
[![License: MIT](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)](LICENSE.txt)

[**Download**](#download) · [**Features**](#features) · [**Connect stores**](#connecting-your-stores) · [**Export**](#export-targets) · [**Privacy**](#privacy)

</div>

<br>

<div align="center">

| Light | Dark |
| :---: | :---: |
| ![Main window: light theme](docs/images/main-window.png) | ![Main window: dark theme](docs/images/main-window-dark.png) |

</div>

---

## Why Pilewise?

You buy games during sales, and then you forget what you already own and where you bought it.
Pilewise collects every library into **one merged list** and keeps your tracker up to date. A
game you own on two stores becomes one row, tagged with both platforms.

---

## Download

<div align="center">

[![Windows](https://img.shields.io/badge/Download-Windows-0078D4?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/AleksandrPidlozhevich/Pilewise/releases)
[![macOS](https://img.shields.io/badge/Download-macOS-000000?style=for-the-badge&logo=apple&logoColor=white)](https://github.com/AleksandrPidlozhevich/Pilewise/releases)
[![Linux](https://img.shields.io/badge/Download-Linux-FCC624?style=for-the-badge&logo=linux&logoColor=black)](https://github.com/AleksandrPidlozhevich/Pilewise/releases)

</div>

From the [latest release](https://github.com/AleksandrPidlozhevich/Pilewise/releases/latest), pick
the file for your system:

| System | File |
| :--- | :--- |
| Windows | `Pilewise-win-x64-Setup.exe` (per-user installer), `Pilewise-win-x64.msi` (Windows Installer, per-user or per-machine), or `Pilewise-win-x64-Portable.zip` |
| macOS (Apple Silicon, M1 and later) | `Pilewise-osx-arm64.dmg` (drag to Applications) or `Pilewise-osx-arm64-Portable.zip` |
| macOS (Intel) | `Pilewise-osx-x64.dmg` (drag to Applications) or `Pilewise-osx-x64-Portable.zip` |
| Linux | `Pilewise-linux-x64.AppImage` |

The macOS app is signed with a Developer ID and notarized by Apple, so it opens without Gatekeeper
warnings.

> [!TIP]
> **Pilewise updates itself.** When a new version is out, a banner appears at the top of the
> window. Click **Update & restart** and you're done.

---

## Features

| | |
| --- | --- |
| 🎮 **Four stores, one list** | Fetches owned games from Steam, Epic, GOG and Xbox and merges duplicates across stores. Untick a store to leave it out of exports. |
| 📤 **Three export targets** | A Notion database, Google Sheets, or a local CSV file. |
| 📚 **Library viewer** | Browse the merged library before exporting: search by name, filter by store, sort by playtime or last played, and spot games you **never played** or own on two stores. **Hide** demos, test servers or tools to leave them out of every export. A footer sums it up: share of games played, total hours, most played. Double-click a game to open its store page. |
| 🆕 **Recently added** | Pilewise remembers when it first saw each game, so the library viewer marks new games **NEW** and can filter / sort by *recently added* (stores don't report purchase dates; games you owned before Pilewise tracked a store don't count). |
| 🎲 **Pick for me** | Can't decide? The library viewer suggests a game from the list shown, skipping completed and hidden ones and preferring games you've barely touched. |
| ✅ **Backlog progress** | After an export to Notion or Google Sheets, the card shows *Completed 120 of 640 (19%)*, and the library viewer marks completed games ✓ with **Completed / Not completed** filters. **Refresh** in the library re-reads the ✓ from Notion or Google Sheets right away (read-only). |
| 📋 **Copy list** | Copies the games shown in the library (search and filter apply) — pastes into a spreadsheet as columns. |
| ⬇️ **Updates** | A new version shows up in the sidebar and under *Settings → Updates* (**Check for updates**, **Download & restart**); a long-running app re-checks every 12 hours. |
| 🖼️ **Covers & store links** | New Notion pages get the game's cover image and a `Store link`; existing pages without one are filled in. |
| 🧹 **Duplicate finder** | **Find duplicates…** spots Notion pages or Google Sheets rows of the same game and merges them (you confirm first; extra Notion pages go to the trash, extra sheet rows are deleted — Sheets' version history can bring them back). |
| ⌨️ **Shortcuts** | Ctrl+1–4 (⌘ on macOS) switch pages, Ctrl+L opens the library; in the library Ctrl+F searches and Esc clears the search or closes it. |
| 🗑️ **No longer owned** | Spots games in your tracker that left your library (refunds, delistings). They're never deleted. An optional `Owned` checkbox column gets unticked for them. |
| ⏰ **Scheduled sync** | **Auto-sync** keeps Notion, Google Sheets and your CSV up to date (hourly, every 6 hours or daily) while the app runs; with **Close to tray** and **Start at login** it keeps going in the background on Windows, macOS and Linux. `Pilewise --sync notion,sheets` syncs without opening the window, ready for Task Scheduler or cron. **History…** (also in the tray menu) lists every recent sync and its result. |
| ⏱️ **Playtime** | Optional `Playtime` and `Last played` columns in Notion and Google Sheets are filled from Steam (last played from Xbox too). |
| 🔁 **Incremental sync** | Re-running an export reconciles with what's already there. It adds new games and updates platform tags without creating duplicates. |
| 👀 **Preview before Notion** | See exactly which games will be added or updated, and confirm before anything is written. |
| 🪟 **Remembers the window** | Size, position and maximized state are restored on the next start. |
| 🖱️ **Drag & drop** | Drop a Pilewise `.csv` onto the window to update it with your current library, or a saved settings file to import it. |
| ⏹️ **Cancellable** | Every export shows a progress bar with a **Cancel** button. |
| 🔒 **Secure by default** | Store tokens and remembered credentials are cached **encrypted** (DPAPI on Windows, Keychain on macOS, libsecret on Linux) and refreshed automatically. |
| 🌗 **Light & dark themes** | Switch with one click. |
| 💻 **Cross-platform** | One app for Windows, macOS and Linux. |

---

## Getting started

```
 1. Download  ──▶  2. Connect your stores  ──▶  3. Pick a target & export
```

1. **Download** and run the app for your OS (see [Download](#download)).
2. **Connect** at least one store (see [Connecting your stores](#connecting-your-stores)).
3. **Pick an export target** and run the export (see [Export targets](#export-targets)).

---

## Connecting your stores

| Store | How you connect | Login window |
| :--- | :--- | :--- |
| <img src="Pilewise/Assets/steam.svg" width="20" align="center"> **Steam** | Steam ID + Web API key | None. Fill in two fields. |
| <picture><source media="(prefers-color-scheme: dark)" srcset="Pilewise/Assets/epic-games-dark.svg"><img src="Pilewise/Assets/epic-games-light.svg" width="20" align="center"></picture> **Epic Games** | Authorization code | Your **system browser** |
| <picture><source media="(prefers-color-scheme: dark)" srcset="Pilewise/Assets/gog-com-dark.svg"><img src="Pilewise/Assets/gog-com-light.svg" width="20" align="center"></picture> **GOG** | Automatic | **Embedded** in the app |
| <picture><source media="(prefers-color-scheme: dark)" srcset="Pilewise/Assets/xbox-dark.svg"><img src="Pilewise/Assets/xbox-light.svg" width="20" align="center"></picture> **Xbox** | Automatic | **Embedded** in the app |

> [!TIP]
> Once connected, each store's session is cached **encrypted** and refreshed automatically,
> so you won't need to log in again every time.

### Steam

Steam needs your **Steam ID** and a **Steam Web API key**. You don't log in through the app.

<details>
<summary><b>1. Find your Steam ID</b></summary>

<br>

The quickest way: **paste your Steam profile link** into the Steam ID field, e.g.
`https://steamcommunity.com/id/yourname` or `https://steamcommunity.com/profiles/7656119…`.
Just the custom name (`yourname`) works too, and so do `[U:1:…]` / `STEAM_0:…` ids.

Or look up the number:

In the Steam desktop app:

1. Click your **username** in the top-right corner.
2. Select **Account details** from the dropdown.
3. Below your display name you'll see your **Steam ID**, a long number (usually 17 digits).
4. Copy and save this number.

</details>

<details>
<summary><b>2. Get your Steam Web API key</b></summary>

<br>

1. Go to the [Steam Web API key page](https://steamcommunity.com/dev/apikey).
2. Sign in to your Steam account if prompted.
3. Accept the **Steam Web API Terms of Use**.
4. Enter a domain name (any name works, e.g. `pilewise-local`) and click **Register**.
5. Your **API key** is displayed as a long alphanumeric string. Copy and save it.

📘 Reference: [Official Steam Web API documentation](https://developer.valvesoftware.com/wiki/Steam_Web_API)

</details>

> [!WARNING]
> **Keep your Steam ID and API key private.** Never share them publicly or commit them to
> version control.

### Epic Games

Epic's login blocks embedded webviews (CAPTCHA), so you log in in your **system browser**:

1. Click **Connect** on the Epic card. Your default browser opens Epic's login page.
2. Sign in. Epic then shows a page with an **authorization code**.
3. Copy the code and paste it into the app. When possible, the app picks it up from the
   clipboard automatically.

### GOG

GOG logs in through an **embedded WebView** inside the app:

1. Click **Connect** on the GOG card. A login window opens.
2. Sign in. The app reads the authorization code from the redirect and closes the window.

### Xbox

Xbox also logs in through an **embedded WebView**:

1. Click **Connect** on the Xbox card. A Microsoft login window opens.
2. Sign in with your Microsoft account. The app reads the token from the redirect and closes
   the window.
3. Game Pass titles are flagged on a best-effort basis.

---

## Export targets

| Target | Needs | Best for |
| :--- | :--- | :--- |
| <picture><source media="(prefers-color-scheme: dark)" srcset="Pilewise/Assets/file-csv-dark.svg"><img src="Pilewise/Assets/file-csv-light.svg" width="20" align="center"></picture> **CSV** | Nothing | Excel, quick offline lists |
| <picture><source media="(prefers-color-scheme: dark)" srcset="Pilewise/Assets/notion-dark.svg"><img src="Pilewise/Assets/notion-light.svg" width="20" align="center"></picture> **Notion** | Integration token + database ID | A rich, filterable backlog tracker |
| <img src="Pilewise/Assets/google-sheets-2026.svg" width="20" align="center"> **Google Sheets** *(beta)* | A Google account | A shared spreadsheet |

### CSV

The simplest target. No account or token needed.

- **Export to CSV** writes the full merged library to a CSV file you pick.
- **Update CSV** merges the current library into an existing CSV. It keeps your completion
  status and doesn't create duplicates. The previous version is saved next to it as
  `<name>.csv.bak`, and **Undo** in the result banner puts it back (by hand: delete the CSV and
  rename the `.bak`).

The file is written as UTF-8 with a BOM so it opens cleanly in Excel.

### Notion

To sync into Notion you need a Notion **integration token**, a **database ID**, and a database
with the **right properties**.

<details>
<summary><b>1. Create an integration and get the token</b></summary>

<br>

1. Go to the [Notion integrations page](https://www.notion.so/profile/integrations).
2. Click **New integration**.
3. Fill in the name and select the workspace where the integration will be used.
4. Copy the **Internal Integration Token** (a long string starting with `ntn_...`).
5. Save this token somewhere safe. You'll need it in the app settings.

🛡️ Grant the integration these capabilities:

- ✅ `Read content`
- ✅ `Update content`
- ✅ `Insert content`

| | | |
| :---: | :---: | :---: |
| ![Notion token, step 1](docs/images/notion-token.png) | ![Notion token, step 2](docs/images/notion-token2.png) | ![Notion token, step 3](docs/images/notion-token3.png) |

</details>

<details>
<summary><b>2. Share the database with the integration</b></summary>

<br>

**Share the target Notion database** with the integration, the same way you'd share it with a
person. Otherwise the app can't see or write to it.

</details>

<details>
<summary><b>3. Find your database ID</b></summary>

<br>

1. Open your Notion workspace and go to the target database.
2. Look at the URL in your browser.
3. The **database ID** is the part after the last slash and before the question mark `?`.
   It's a 32-character alphanumeric string.

![Database ID example](docs/images/notion-database-id.png)

> [!NOTE]
> You copy the **database ID** from the URL, as before. Internally the app uses Notion API
> version `2025-09-03`, which finds the database's data source automatically, so you don't
> need any extra setup.

📘 Reference: [Retrieve a database](https://developers.notion.com/reference/retrieve-database)
· [Retrieve a data source](https://developers.notion.com/reference/retrieve-a-data-source)
· [Upgrading to 2025-09-03](https://developers.notion.com/guides/get-started/upgrade-guide-2025-09-03)

</details>

<details>
<summary><b>4. Required database properties</b></summary>

<br>

The target database **must** have these properties (names are case-sensitive):

| Property | Type | Holds |
| :--- | :--- | :--- |
| `Name` | Title | The game's title |
| `Platform's` | Multi-select | The store(s) the game is owned on (Steam, Epic, GOG, Xbox) |
| `GameID` | Text | Platform-prefixed id(s), e.g. `Steam:236870; GOG:123` |
| `Store link` | URL | The game's store page (filled only while empty, so your own links stay) |

Each page also gets the game's **cover image** (Steam's header image; the GOG, Epic or Xbox art for
games not on Steam). A cover you set yourself is never replaced.

Optional: add any of these and Pilewise keeps them up to date too:

| Property | Type | Holds |
| :--- | :--- | :--- |
| `Playtime` | Number | Hours played (Steam), e.g. `12.5` |
| `Last played` | Date | Date of the last session (Steam / Xbox) |
| `Owned` | Checkbox | Ticked while the game is in your library, unticked once it's gone |

A game only counts as "no longer owned" when every store it's tagged with was loaded in that run,
so an expired Epic or GOG session never marks those games as gone.

**Backlog progress** is read from your own completion column, which Pilewise never changes: a
checkbox named `Completed`, `Done`, `Finished` or `Beaten`, or a `Status` (status or select)
property set to `Done`, `Completed`, `Finished`, `Beaten` or `Played`.

![Required Notion properties](docs/images/notion-properties.png)

</details>

When you click **Export to Notion**, Pilewise first checks the database. A missing `Platform's`,
`GameID` or `Store link` property is added for you (the preview says so). The first export after
updating Pilewise also adds covers and links to your existing pages, so it updates many pages once. A misnamed title column or a wrong
property type is reported with what to change. Then it shows a **preview**: the games it will add
and the rows it will update (before → after). Nothing is written until you click **Export**.

**Find duplicates…** looks for pages of the same game (same store id, or the same name without a
conflicting id). For each, the oldest page is kept and gets every store tag and id; the others are
moved to Notion's trash (restorable for 30 days). Other columns of the kept page aren't touched, so
copy any notes from the extra pages first.

### Google Sheets (beta)

Authorization with Google Sheets uses standard Google OAuth.

1. Enter a **table name** and click **Export to Google Sheets**. On first use, sign in to Google in
   the browser that opens.
2. Pilewise finds the spreadsheet with that name in your Google Drive, or creates it, and opens it
   when the sync is done.

The first tab gets the same columns as the CSV, plus a checkbox to mark games as done:

| Platform | Name | ID | Completed |
| :--- | :--- | :--- | :---: |
| Steam/GOG | The Witcher 3 | Steam:292030; GOG:1207664643 | ☑ |

Want playtime too? Add a `Playtime`, `Last played` and/or `Owned` header to the first row (any free
column). Pilewise fills them with hours played (Steam), the date of the last session
(Steam / Xbox) and an ownership checkbox (unticked for games that left your library).
A `Store link` header gets the game's store page and a `Cover` header its cover image (an
`=IMAGE(…)` formula); both are filled only while the cell is empty, so your own values stay.

Re-running the export adds new games and updates platform tags in place. Your ticks, row order
and any extra columns you added are kept. Sheets created by older versions (Steam-only
`Name | Game ID | Completed`) are upgraded automatically.

**Find duplicates…** under the Google Sheets export looks for rows of the same game. For each, the
top row is kept and gets every store tag and id (plus the Completed tick, if any copy had it); the
other rows are deleted after you confirm. If the sheet changed since the search, nothing is
written. *File → Version history* in Google Sheets brings deleted rows back.

> [!CAUTION]
> This integration is in **beta**. The authentication flow may sometimes fail, and some
> features may be unstable or unavailable. We're working on it. Thank you for your patience!

---

## Sync from the command line

Once the app is set up (credentials filled in, stores connected), Pilewise can sync without opening
its window, e.g. on a schedule:

```
Pilewise --sync notion
Pilewise --sync notion,sheets --sheet "My games"
Pilewise --sync csv --file "D:\Games\backlog.csv"
Pilewise --sync notion,sheets --dry-run   # show what would change, write nothing
Pilewise --help
```

- Credentials, the Google Sheets table name, the ticked stores and store logins are the ones saved
  by the app.
  Settings are saved when you start an export and when you close the window.
- `--file` can be left out: `--sync csv` then updates the CSV you last exported or updated in the app.
- Notion changes are applied **without** the preview window. Run with `--dry-run` first to check.
- Each run (not `--dry-run`) also shows up in the app's **History…** window.
- Output goes to the console and to the log. Exit code: `0` success, `1` a sync failed, `2` bad arguments.
- On Windows the app installs to `%LocalAppData%\Pilewise\current\Pilewise.exe`. It's a GUI app, so
  from a terminal start it with `Start-Process … -Wait -NoNewWindow` (PowerShell) or
  `start /wait …` (cmd) to see its output. Task Scheduler waits for it on its own.

**Windows Task Scheduler**: *Create Basic Task* → trigger *Daily* → action *Start a program* →
program `%LocalAppData%\Pilewise\current\Pilewise.exe`, arguments `--sync notion`.

---

## Privacy

🔐 Pilewise does **not** collect, store or send any personal data to its developer. Logs and
cached credentials stay on your device, and credentials are encrypted.
See [PRIVACY_POLICY.md](PRIVACY_POLICY.md).

---

## Support the project

If Pilewise saved your backlog, you can buy me a coffee:

<a href="https://ko-fi.com/aliaksandrpidlazhevich"><img src="https://img.shields.io/badge/Support%20on-Ko--fi-FF5E5B?style=for-the-badge&logo=ko-fi&logoColor=white" alt="Support on Ko-fi"></a>

---

## Feedback and issues

Found a bug or have an idea? [Open an issue](https://github.com/AleksandrPidlozhevich/Pilewise/issues)
(the **Logs** link in the bottom-left corner of the app opens the log files to attach)
or write to **aleksandr.pidlozhevich@gmail.com**.

---

<div align="center">

Released under the [MIT License](LICENSE.txt) · Built with [.NET](https://dotnet.microsoft.com/) and [Avalonia UI](https://avaloniaui.net/)

</div>
