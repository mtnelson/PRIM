# RIM — Setup and Run Guide

**RIM — Records Inventory Manager.** A Content Manager–style
records-management prototype: Blazor Server on .NET 8 with a MudBlazor
desktop interface. It tracks **metadata and physical locations only** —
no electronic documents are attached or stored.

## Requirements

- Windows 10/11, 64-bit (x64)
- No .NET install needed — this package is self-contained
- About 130 MB disk space, plus room for the database as it grows

## Install

1. Unzip the package to any folder, e.g. `C:\RIM\`.
   (Avoid `C:\Program Files\` unless you run as administrator —
   the app writes its database next to the executable.)
2. Double-click **`Start RIM.bat`**.
3. Your browser opens to `http://localhost:5000/`.
4. Sign in on the login screen. Dev credentials (password = username):
   `admin01` (Admin), `recordsmgr01` (Records Manager), `mtnelson` (Staff).

To stop the app, close the "RIM Server" console window.

## First-run verification

After the browser opens, run the smoke test in `SMOKE-TEST.md`
(about 5 minutes). It confirms the interactive UI — navigation,
right-click menus, dialogs, search, and exports — works in your browser.

## Your data

- The SQLite database is created automatically on first run at
  `data\prim.db` next to `Rim.exe`, pre-loaded with sample
  records, containers, locations, and users.
- **Back up** by copying `data\prim.db` while the app is stopped.
- To start over with a fresh database, stop the app and delete
  `data\prim.db` (it is recreated and re-seeded on next start).

## Changing the port

Edit `appsettings.json` and set `Prim:HttpPort` (default `5000`),
then restart. Update the URL in `Start RIM.bat` to match.

## Switching to SQL Server (optional)

1. In `appsettings.json`, set
   `ConnectionStrings:Prim` to your SQL Server connection string.
2. In `Program.cs`, swap the commented `UseSqlServer` lines for the
   `UseSqlite` lines (instructions are in the comments), rebuild, republish.

## What this prototype does not include

Out of scope for this build: document attachments, check-in/out,
finalize-document functions, review schedules, alerts, customizable
workflows, consignment lists, document queues. Record requests
(the eventual File Request System replacement) are post-go-live.

## Troubleshooting

- **Browser shows "can't reach this site":** the server is still starting;
  wait a few seconds and refresh. If it persists, check the
  "RIM Server" window for error messages.
- **Port already in use:** change `Prim:HttpPort` in `appsettings.json`.
- **Windows SmartScreen warning:** this is expected for an unsigned
  self-published app — choose "More info" then "Run anyway".
- **Antivirus flags the download:** the package is built from audited
  source with no network calls except serving the local UI; allow it
  or build from source yourself (`dotnet publish -c Release -r win-x64`).
