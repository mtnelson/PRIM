# PRIM — Physical Records Inventory Manager

PRIM is a metadata-only records inventory system (Blazor Server / .NET 8 /
MudBlazor): records, containers, locations, and users with a Content
Manager–style desktop shell. SQLite by default; SQL Server path documented.

This repo contains the full prototype source, integration tests, and
packaging docs (`SMOKE-TEST.md`, `Start PRIM.bat`).

## Run on your PC

No installs needed — the portable package is self-contained (no .NET SDK,
no network dependencies).

1. **Download** the portable zip (54 MB):
   <https://github.com/mtnelson/PRIM/releases/download/prim-v0.12.0/PRIM-portable-win-x64.zip>
2. **Unzip** to any folder, e.g. `C:\PRIM\` (avoid `C:\Program Files\` unless
   running as administrator — the app writes its database next to the exe).
3. **Double-click `Start PRIM.bat`**. Your browser opens to
   `http://localhost:5000/`.
4. Sign in on the login screen. Dev credentials (password = username):
   `admin01` (Admin), `recordsmgr01` (Records Manager), `mtnelson` (Staff).
5. Run the 5-minute smoke test in `SMOKE-TEST.md`.

Your data lives in `data\prim.db` next to `Prim.exe` — back it up by copying
that file while the app is stopped. To stop the app, close the "PRIM Server"
console window. Default port is 5000 (change via `Prim:HttpPort` in
`appsettings.json`, and update the URL in `Start PRIM.bat`).

## Build from source

Requires the .NET 8 SDK:

```bash
dotnet build -c Release
dotnet test
```

See the source `README.md` in the repo root for details.
