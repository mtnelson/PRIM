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

## 4. Search

- [ ] Dashboard quick search: type `HQ` — matching records appear.
- [ ] Advanced Search: add two criteria with AND, run; switch to OR, run.
      Both return sensible results.

## 5. Containers / Locations / Users

- [ ] Containers: right-click menu works; New Container dialog opens/cancels.
- [ ] Locations: hierarchy tree renders; creating a duplicate sibling name
      is rejected.
- [ ] Users: list renders; role switcher in the top bar changes the
      current user (permissions change accordingly).

## 6. Workspaces, Move, Reports

- [ ] Add a record to a workspace; it appears under Workspaces / Favorites.
- [ ] Select records, use **Move Items** to change Home/Assignee or location.
- [ ] Reports: group by State (or another field); counts look right.

## 7. Export / print

- [ ] Records grid: export CSV downloads a file with the grid rows.
- [ ] Copy-to-clipboard action copies the record summary.
- [ ] Label dialog: **Print** opens the print preview.

## 8. Shutdown

- [ ] Close the "PRIM Server" console window; the browser can no longer
      reach the site (server stopped cleanly).
- [ ] `data\prim.db` exists next to `Prim.exe` (your data file — back it up).

If any step fails, note the exact step, what you saw, and any text in the
F12 Console, and report it back.
