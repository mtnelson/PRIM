# RIM Changelog

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
