# PRIM — Browser Smoke Test

Run this once on the PC where PRIM is installed. It verifies the
interactive behavior that automated server-side tests cannot cover
(Blazor circuit, right-click menus, dialogs, clipboard, printing).
Takes about 5 minutes.

Preconditions: PRIM installed per `README.md`, server started via
`Start PRIM.bat`, browser open at `http://localhost:5000/`.

## 0. Login

- [ ] A login screen appears (username + password).
- [ ] Sign in as **admin01** / **admin01** (dev credentials; each access
      level has its own account: `admin01` Admin, `recordsmgr01`
      Records Manager, `mtnelson` Staff — password equals username).

## 1. Startup

- [ ] Page loads with no "couldn't connect" / error banner.
- [ ] Top bar shows **PRIM — Physical Records Inventory Manager**.
- [ ] Left shortcut pane lists: Dashboard, Records, Containers,
      Locations, Users, Advanced Search, Workspaces, Reports, Administration.
- [ ] Announcement banner is visible (seeded welcome message).
- [ ] Press F12: Console tab shows no red errors.

## 2. Navigation

Click each shortcut; each page renders its grid/content with no error:

- [ ] Dashboard  - [ ] Records  - [ ] Containers  - [ ] Locations
- [ ] Users  - [ ] Advanced Search  - [ ] Workspaces
- [ ] Reports  - [ ] Administration

## 3. Records

- [ ] Grid lists the 5 seeded records.
- [ ] **Right-click** a row: a context menu appears
      (View, Edit, Delete/Restore, Move, Add to workspace, ...).
- [ ] **Left-click** a row: the view pane shows record details
      (record number, barcode, title, Home/Assignee, location).
- [ ] Click **New Record**: the dialog opens. Click **Cancel**: it closes
      with no changes.
- [ ] In New Record, fill the required fields and save: the record
      appears in the grid with a system-generated record number/barcode.
- [ ] Open a record, change its **Record Type**: a confirmation appears
      (record-type change requires Records Manager permission and
      confirmation). Confirm and verify the audit trail shows the change.

## 3b. Hierarchy, links, and breadcrumb paths

- [ ] In the Records grid, the **Path** column shows breadcrumb segments
      separated by › (e.g. shelf › box); clicking a segment shows that
      level's contents.
- [ ] Rows that have children (compressed records, containers with items,
      locations with child locations) show a **caret/chevron** in the first
      column; rows with no children show no caret.
- [ ] Click the caret on a compressed record or container: its child rows
      expand inline. Click again to collapse. Clicking a child row's name
      opens its details in the view pane.
- [ ] Click a **Home** value (e.g. a box name): the app navigates to the
      Containers page and selects/focuses that box.
- [ ] Click an **Assignee** value: the app navigates to that user and
      focuses them.
- [ ] The same caret expansion, clickable Home/Assignee, and Path column
      work on the Containers and Locations grids.

## 4. Search

- [ ] Dashboard quick search: type `HQ` — matching records appear.
- [ ] Advanced Search: add two criteria with AND, run; switch to OR, run.
      Both return sensible results.

## 4b. Test data and infinite scroll

- [ ] Admin → **Test Data** → **Generate 1,500 Test Records**: confirms and
      reports 1,500 created (one-time; rows are prefixed `[TEST]`).
- [ ] Records grid: scroll to the bottom — more rows load automatically
      (500 at a time), no pagination buttons, no manual "load more".
- [ ] With 1,500+ records, Ctrl+A selects only the rows loaded so far;
      the selection counter shows how many are loaded.
- [ ] Type in the filter box: the grid resets to the first 500 matches;
      debounced, no full-table freeze.

## 4c. Search tabs

- [ ] Records page: a tab strip sits above the grid with one tab
      ("All records"). Click **+** to open a second tab; search for
      something different in each.
- [ ] Click back to the first tab: its filter, sort, column layout, and
      selected rows are restored (results re-run server-side).
- [ ] Select rows and expand a child caret in one tab, switch to another
      tab and back: selections and expansions are still there.
- [ ] Open tabs up to the limit: the 11th is refused with a message.
- [ ] Close a tab with its ×; the others are unaffected.
- [ ] Navigate away (Dashboard) and back, or restart the app: open tabs
      persist with their searches.
- [ ] Containers, Locations, Users, and Advanced Search each have their
      own independent tab strip.

## 5. Containers / Locations / Users

- [ ] Containers: right-click menu works; New Container dialog opens/cancels.
- [ ] Locations: hierarchy tree renders; creating a duplicate sibling name
      is rejected.
- [ ] Users: list renders; role switcher in the top bar changes the
      current user (permissions change accordingly).

## 6. Workspaces, Move, Reports

- [ ] Add a record to a workspace; it appears under Workspaces / Favorites.
- [ ] Each workspace group shows **full data grids** (Records, Containers,
      Locations, Users) with the same columns, sorting, selection,
      expansion carets, clickable Home/Assignee, Path breadcrumbs, and
      Ctrl+C copy as the main screens.
- [ ] Select records, use **Move Items** to change Home/Assignee or location.
- [ ] Reports: group by State (or another field); counts look right.

## 7. Export / print

- [ ] Records grid: export CSV downloads a file with the grid rows.
- [ ] Select rows, press **Ctrl+C**, paste into Excel: all displayed columns
      paste with headers, one spreadsheet column per grid column.
- [ ] Right-click a row → **Copy** pastes the same full grid data.
- [ ] Label dialog: **Print** opens the print preview.
- [ ] Activity log names the specific items for exports, moves, deletes,
      test-data generation (long lists show the first 8 plus "+N more").

## 8. Grid scrolling

- [ ] Records grid with all columns visible: a horizontal scrollbar appears
      and every column is reachable by scrolling (columns no longer squeeze
      to fit the window).
- [ ] Expand a compressed record: the child rows show every record column.
- [ ] Admin → Deleted Records: every record column plus Reason, Merged Into,
      Deleted By, with horizontal scrolling.

## 9. Shutdown

- [ ] Close the "PRIM Server" console window; the browser can no longer
      reach the site (server stopped cleanly).
- [ ] `data\prim.db` exists next to `Prim.exe` (your data file — back it up).

If any step fails, note the exact step, what you saw, and any text in the
F12 Console, and report it back.
