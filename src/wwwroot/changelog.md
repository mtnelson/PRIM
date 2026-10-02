# RIM Changelog

## v0.14.1 — 2026-10-02 — label PDF fixes, grid range-select
- Fixed the label PDF button crashing the app: PDFsharp 6.x ships with no font resolver, so every label PDF threw on click (the v0.14.0 test masked it with a test-only resolver). The app now resolves fonts from the OS font directories (Arial on Windows), so the PDF renders on any machine.
- Fixed the label PDF download producing a corrupt file: the browser helper now decodes the base64 payload back to bytes before saving (CSV text downloads are unchanged).
- The barcode preview in the Label dialog now scales to fit the label instead of rendering at a fixed 600px width that overflowed the label box.
- Removed the "Decode barcodes from image" upload from the Barcode Scanning page (and the SkiaSharp dependency that powered it) — keyboard-wedge scanning remains.
- All data grids: Shift+click now selects the contiguous range of rows from the last clicked row (the anchor) to the clicked row. The anchor is set by plain click and Ctrl+click and does not move on Shift+click, so repeated Shift+clicks re-extend from one point. In virtualized grids the range covers loaded rows; if the anchor has scrolled out of the loaded window it falls back to a single-select.

## v0.14.0 — 2026-10-02 — Content Manager third-party integrations
- Label printing now downloads a real PDF: the Label dialog gained a PDF button that renders each selected label as a 4"x2" page via PDFsharp — the same PDF library Content Manager 24.3 uses. Barcodes on the PDF are real, scannable Code 128 symbols drawn as vector graphics (sharp at any print DPI), not images.
- The label preview in the dialog now shows the actual barcode symbol (ZXing-encoded SVG) instead of placeholder glyphs.
- Barcode Scanning page: new "Decode barcodes from image" upload — photograph a barcode (or screenshot one) and ZXing decodes it into the scan field, as an alternative to keyboard-wedge scanners. Decoded codes append without duplicating existing lines.
- Persistent server log: NLog now writes `logs/rim-YYYY-MM-DD.log` next to the executable (14-day rotation) plus console output, and the in-app activity feed is mirrored there — so there is an on-disk trail for support and diagnosis on the Windows host. Startup, shutdown, and the dev-password security warning are all logged.
- Libraries added (all free/open source, from Content Manager 24.3's own third-party list): PDFsharp 6.2.4 (MIT), ZXing.Net 0.16.11 (Apache-2.0), NLog 6.2.1 (BSD-3-Clause), SkiaSharp 4.153.1 (MIT, decodes uploaded images into pixels for ZXing).
- Note: CM lists PDFsharp 1.5; v6.2.4 is the current release of the same library and the one compatible with .NET 8.

## v0.13.2 — 2026-10-02
- Branding text updated throughout the app (login screen, header, README): "Physical Records Inventory Manager" is now "Records Inventory Manager".

## v0.13.1 — 2026-10-01 — code-review hardening
- Fixed a startup crash when upgrading from an older database: schema columns are now all added before any backfill runs.
- The automatic schema upgrade now works on SQL Server as well as SQLite (provider-specific upgrade steps); previously it used SQLite-only statements and SQL Server could not start.
- Security: the Users and Admin pages now require the Administrator role — non-admins see "not authorized" and the nav links are hidden for them. Admin write paths pass the caller's role (`actorRole`) for server-side enforcement.
- Development password login is now opt-in via `Auth:AllowDevPasswords` (default off — fail closed), with login-attempt throttling (5 failures/minute per username, then a 5-minute lockout), minimum password length raised to 8, and a loud startup warning logged whenever it is enabled.
- The compressed-parent dropdown, the home/assignee picker, and the Admin recycle bin now use server-side filtered, take-capped queries (500-row pages with search) instead of loading whole tables; picker tree nodes load their children lazily on expand.
- The per-item audit log now consults an in-memory manifest mapping export files to the items they contain, so opening it reads only files that can hold that item's rows instead of line-scanning every archive file.
- Record/Container/Location/User/Delete dialog save handlers (and Move) now surface database errors in the dialog via Snackbar instead of tearing the circuit.
- Packaged README install steps fixed to the RIM names (`Start RIM.bat`, `Rim.exe`, `C:\RIM\`, "RIM Server" window); `prim.db`, `ConnectionStrings:Prim`, and `Prim:HttpPort` keep their legacy names.
- Barcode uniqueness is now enforced for containers, locations, and users (was records-only); database save errors surface as messages in the dialog instead of crashing the page; numbering a new item survives concurrent creates and ignores legacy non-conforming numbers.
- Ancestor breadcrumb paths are now resolved with small batched queries instead of loading entire tables per grid page — this was the single biggest 20M-record scalability blocker.
- Added the missing database indexes the barcode tool and tree grids need (barcodes on all four tables, parent/home references).
- Compressed-record rules tightened: cannot file under a deleted parent, cannot delete a parent that still has children, leaving the Compressed type unfiles the record (other types keep their parent link), and the parent link is only cleared when actually leaving the Compressed type.
- Audit retention now runs in the background instead of blocking every save; a failed audit export (e.g. disk full) no longer breaks all writes.
- Grid paging keeps working with explicit sort orders at scale (keyset resume instead of degrading OFFSET); Reports grouping and Dashboard quick search are bounded server-side queries.
- Barcode moves resolve all barcodes in batched queries (no more one-query-per-barcode) and stay under SQL Server's parameter limit.
- Search wildcards now behave identically on SQLite and SQL Server.
- Upgrade note: existing databases gain the new indexes automatically on startup (barcode indexes are non-unique on upgraded databases so legacy duplicates can't block startup); unique enforcement applies to fresh databases.

## v0.13.0 — 2026-10-01
- Renamed: PRIM is now RIM — new name and new logo throughout the app (titles, header, login screen, help, release notes).
- Compatibility note: your existing data is untouched. The database file stays `prim.db`, the `ConnectionStrings:Prim` key and `Prim:HttpPort` setting keep their names, and all database tables are unchanged — just upgrade in place.

## v0.12.0 — 2026-10-01
- Dark / light mode: new toggle in the app header (next to the + button). Your choice is remembered per user; with no stored choice the app follows your OS setting.
- Barcode Scanning: new Tools section in the left navigation. Pick an action first — Add to Workspace 1–5 / Favorites, Add to Home or Container, Add to Assignee, or Add to Home & Assignee — then scan barcodes (Enter advances to the next field). Failed barcodes stay in the scan field for retry while successful ones clear, and a popup reports per-barcode results.
- Users now get a system-assigned barcode (USRnnnnnn), shown read-only in the user dialog and as a grid column, so assignees can be scanned.
- Compressed records: selecting the Compressed type now requires choosing Parent or Child. A Child must select a Compressed Parent — the parent record itself becomes the child's Home and Assignee, so the child moves with the parent automatically (no copy, no cascade). Any record type can be filed under a Compressed Parent and keeps its type. Rules enforced in the service: only a Compressed Parent can accept children (no nesting), a Child cannot have children of its own, and a parent with children cannot change to another type until the children are moved out.
- Upgrade note: existing Compressed records that already have children (or are already filed) are backfilled to Parent/Child automatically; other Compressed records will ask you to choose Parent or Child the next time they are edited.

## v0.11.2 — 2026-10-01
- New "+" menu in the app header: create a record, container, location, user, or label from any screen, using the same dialogs the object pages use. Ctrl+N now opens this menu globally (it no longer creates an item for the current page); the per-page "New" buttons still work as before.
- Fixed Ctrl+N opening a browser window when focus was in a filter/search box: registered app shortcuts with no text-editing role (Ctrl+N, Ctrl+S, F9, …) are now intercepted even inside text inputs, while true editing shortcuts (Ctrl+C/X/V/Z/Y/A, Delete) still go to the browser.

## v0.11.1 — 2026-10-01
- Advanced Search results now sit in tabs: Results, SQL, and Activity.
- The SQL tab shows the exact SQL of the most recent search run — count, ID-list, and page queries (page queries appear as you scroll) — each with its timestamp, duration in milliseconds, and a copy button.
- The Activity tab is a persistent per-user log of every search run on the page: time, object type, AND/OR logic, criteria summary, result count, and duration (last 200 kept per user).

## v0.11.0 — 2026-10-01
- Label detail pages now show full data grids per object type (records, containers, locations, users) with all columns and the column selector, instead of single-column link tables.
- Advanced Search now searches records, containers, locations, or users — radio buttons at the top select the object type, each with its own field list and full data grid (edit/delete work from the results, as on the object pages).
- Search tab titles carry the full search-string logic (including the object type on Advanced Search); long titles show an ellipsis with the complete title in a hover tooltip.
- Advanced Search "Contains" now genuinely contains — no `*` asterisks required (explicit `*`/`?` wildcards still work).
- The Records page is removed from the left navigation (the route still works for deep links); record work moves to Advanced Search.
- Grid scrolling performance: each row's display values are now computed once per row instead of once per cell.
- Saved-search sessions now persist the Advanced Search object type, so tab restore re-selects the radio and re-runs the right query.

## v0.10.5 — 2026-09-30
- Bulk-operation progress toasts now actually appear during long runs: tight loops (send-to-workspace, remove-from-workspace, CSV export, clipboard copy, select-all) periodically yield to the renderer — previously the UI thread stayed blocked until the operation finished, so the "working" toast never painted.
- Ctrl+A reliability: the browser's hotkey list is now pushed the moment any component registers or unregisters a hotkey (previously it only synced when the main layout re-rendered), so Ctrl+A can no longer tag grid rows while the browser also performs its native select-all.

## v0.10.4 — 2026-09-30
- Working indicators on every bulk operation: adding many records to a workspace, removing them, bulk delete, bulk move, CSV export, clipboard copy, and server-side select-all now show a persistent "working" toast with live progress (e.g. "Adding 3,000 of 50,000…"). The toast only appears if the operation takes longer than a moment, so quick actions never flash it. The Delete and Move dialogs now disable their buttons and show "Deleting…"/"Moving…" with a spinner and progress bar while they work.

## v0.10.3 — 2026-09-30
- All data grids now use infinite scroll: the dashboard and workspace grids (which previously grew the page without any scrollbar) are virtualized with a compact 400px scroll height, matching the main screens' virtualized scrolling.
- Loading indicators everywhere data takes time: grid rows show a spinner while a chunk is being fetched; Dashboard, Workspaces, and Advanced Search show an indeterminate progress bar while loading or searching, with their buttons disabled and relabeled ("Searching…"); Admin → Audit Retention buttons show "Archiving…"/"Exporting…" with a spinner; the test-data Generate button shows a spinner while generating.
- Codebase convention: every Razor component now uses a `.razor.cs` code-behind file — no more `@code` blocks in markup files.

## v0.10.2 — 2026-09-30
- Grids are now truly virtualized (`MudDataGrid` `Virtualize` + `VirtualizeServerData`): scrolling fetches 500-row chunks on demand instead of accumulating every loaded row in memory, so 3,000+ records stay responsive and the first rows render without any horizontal scrolling. A fixed grid height with a sticky header keeps the column titles visible while scrolling.
- Fixed the duplicate horizontal scrollbar: the grid now owns exactly one, via a PRIM-specific CSS rule (also applied to Admin → Deleted Records).
- Rows added after the first chunk no longer wait for a horizontal scroll to appear — vertical scrolling loads them directly.
- Select-all (Ctrl+A) on a filtered grid now uses a server-side ID list capped at 50,000 rows instead of loading entities.
- The grid header shows the live total row count for the current filter.
- Bumped the `prim.js` and `prim.css` cache-busters so browsers pick up the current assets.

## v0.10.1 — 2026-09-30
- Fixed a launch error on the main data pages: the grid was handing MudBlazor both `Items` and `ServerData` at once, which it forbids. The grid now supplies only `ServerData` in infinite-scroll mode.
- Fixed the logo image: the repo copy was base64 text instead of JPEG bytes, so browsers couldn't render it. Replaced with the real image.
- Added `.gitattributes` marking images as binary so Windows checkouts never corrupt them with line-ending conversion.
- Bumped the `prim.js` cache-buster so browsers pick up the current script.

## v0.10.0 — 2026-09-30
- Audit log retention: count-based three-tier plan. The hot audit table is capped at 500,000 rows (configurable via `Audit:MaxHotRows`) — overflow moves oldest-first into a new `AuditEventArchive` table, automatically after bulk operations or on demand from Admin → Audit Retention. The archive table is capped at 5,000,000 rows (`Audit:MaxArchiveRows`) — overflow is exported to a checksummed `.jsonl.gz` file, verified, then removed. Rows are never deleted without a verified copy elsewhere. Every item's audit log reads hot + archived + export files together, so the chain of custody is never broken; export files can also be browsed from the Admin tab.

## v0.9.1 — 2026-09-30
- Removed internal issue references from user-visible text; they remain only in code comments.

## v0.9.0 — 2026-09-30
- Version number shown on the login screen and at the bottom of the navigation drawer; click it to view this changelog.
- PRIM logo added to the app bar (top left) and the login screen.
- Grid column widths now use supported min-width styles so the horizontal scrollbar engages on wide grids.
