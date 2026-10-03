# RIM — Records Inventory Manager

RIM is a metadata-only records inventory system (Blazor Server / .NET 8 /
MudBlazor): records, containers, locations, and users with a Content
Manager–style desktop shell. SQLite by default; SQL Server path documented.

This repo contains the full prototype source, integration tests, and
packaging docs (`packaging/`, `SMOKE-TEST.md`, `Start RIM.bat`).

## Run on your PC

No installs needed — the portable package is self-contained (no .NET SDK,
no network dependencies).

1. **Download** the portable zip (54 MB):
   <https://github.com/mtnelson/PRIM/releases/download/rim-v0.13.0/RIM-portable-win-x64.zip>
2. **Unzip** to any folder, e.g. `C:\RIM\` (avoid `C:\Program Files\` unless
   running as administrator — the app writes its database next to the exe).
   Extract into a fresh folder; do not unzip over an old install.
3. **Double-click `Start RIM.bat`**. Your browser opens to
   `http://localhost:5000/`.
4. Sign in on the login screen. Dev credentials (password = username):
   `admin01` (Admin), `recordsmgr01` (Records Manager), `mtnelson` (Staff).
   (Dev password login is enabled by `Auth:AllowDevPasswords` in
   `appsettings.json`; the app logs a warning while it is on. Set it to
   `false` to disable password login entirely.)
5. Run the 5-minute smoke test in `SMOKE-TEST.md`.

Your data lives in `data\prim.db` next to `Rim.exe` — back it up by copying
that file while the app is stopped. To stop the app, close the "RIM Server"
console window. Default port is 5000 (change via `Prim:HttpPort` in
`appsettings.json`, and update the URL in `Start RIM.bat`).

## Build from source

Requires the .NET 8 SDK:

```bash
dotnet build -c Release
dotnet test
```

See `packaging/` for the portable-zip build notes and `SMOKE-TEST.md` for
the Windows verification checklist.

## Customizing label templates

Labels are defined by a FastReport template, not by code:
`Reports\Label4x2.frx` next to `Rim.exe`. To change the layout (positions,
fonts, which fields print, new label sizes), edit the template — no rebuild
needed; the app picks it up on the next label print.

1. **Install FastReport Designer Community Edition** (Windows only,
   free): download it from the FastReport GitHub releases page —
   <https://github.com/FastReports/FastReport/releases> — look for the
   "FastReport Designer Community Edition" asset. (The report engine is
   MIT-licensed; the designer app itself is a free closed-source binary
   from the FastReport team.)
2. **Open** `C:\RIM\Reports\Label4x2.frx` in the designer (use your real
   install folder).
3. Edit the layout and **save**. Keep the page size at 4"x2" unless you
   are making a new stock size, and keep the barcode object's symbology
   on Code 128.
4. Print a test label from RIM to verify.

Notes for template authors: the barcode object binds its data through its
`Expression` property (`[Labels.Barcode]`), not `Text`; the footer reads
the `PrintedBy`/`PrintedAt` report parameters. Barcodes render as vector
graphics in the PDF, so they stay sharp at any print DPI. Back up the
`.frx` before experimenting — a fresh copy ships in every release zip.
