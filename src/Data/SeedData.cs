namespace Prim.Data;

using Microsoft.EntityFrameworkCore;
using Prim.Services;

/// <summary>Demo dataset so the app is usable the first time it starts.</summary>
public static class SeedData
{
    // Typo corrections applied 2026-09-29: Cincinnati (was "Cincinnatti"),
    // Las Vegas (was "Las"), Minneapolis (was "Minneapolios").
    public static readonly (string Code, string Name)[] FieldOffices =
        Prim.Services.FieldOffices.Offices.ToArray();

    public static readonly string[] RecordTypes =
        { "Case File", "Compressed", "Abstract", "HQ Bureau Applicant", "HQ In Service", "HQ Out of Service" };

    public static readonly string[] ContainerTypes =
        { "Box", "Tub", "Crate", "Pallet", "Tote", "Truck", "Bin", "NARA Box", "Virtual Container" };

    public static readonly string[] LocationTypes =
        { "Building", "Carousel", "Compartment", "Freezer", "Room", "Row", "Shelf", "Virtual" };

    public static readonly string[] SecurityClassifications =
        { "Unclassified", "Confidential", "Secret", "Top Secret" };

    public static readonly string[] RecordStates =
        { "Active", "Disposition Ready for Review", "Reviewed Eligible", "Reviewed Ineligible", "Archived" };

    public static readonly string[] Roles = { "Admin", "Records Manager", "Staff" };

    // EnsureCreated never alters an existing SQLite file, so this adds any
    // columns/tables introduced after the database was first created.
    // Idempotent: safe to run on every startup, on SQLite and SQL Server.
    public static void UpgradeSchema(PrimDbContext db)
    {
        AddColumn(db, "Users", "PasswordHash", "TEXT");
        AddColumn(db, "Users", "PasswordSalt", "TEXT");
        AddColumn(db, "Users", "LocationId", "INTEGER");
        AddColumn(db, "Records", "ParentRecordId", "INTEGER");
        AddColumn(db, "Records", "AssigneeKind", "TEXT");
        AddColumn(db, "Containers", "AssigneeKind", "TEXT");
        AddColumn(db, "Records", "AssigneeRefId", "INTEGER");
        AddColumn(db, "Containers", "AssigneeRefId", "INTEGER");
        BackfillRefIds(db);
        // Existing rows predate the AssigneeKind column: their assignees were
        // always user IDs, so default to "User" (matches fresh seed data).
#pragma warning disable EF1002
        db.Database.ExecuteSqlRaw("UPDATE Records SET AssigneeKind = 'User' WHERE AssigneeKind IS NULL");
        db.Database.ExecuteSqlRaw("UPDATE Containers SET AssigneeKind = 'User' WHERE AssigneeKind IS NULL");
#pragma warning restore EF1002
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS UserGridLayouts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL,
                GridId TEXT NOT NULL,
                ColumnsCsv TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_UserGridLayouts_UserId_GridId
            ON UserGridLayouts (UserId, GridId)
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS Labels (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                CreatedBy TEXT NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Labels_Name ON Labels (Name)
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS ObjectLabels (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                LabelId INTEGER NOT NULL,
                ObjectKind TEXT NOT NULL,
                ObjectId INTEGER NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ObjectLabels_Label_Object
            ON ObjectLabels (LabelId, ObjectKind, ObjectId)
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS IX_ObjectLabels_Object
            ON ObjectLabels (ObjectKind, ObjectId)
            """);
        // Search-session tabs (per-page open search descriptors). Fresh DBs
        // get the table from EnsureCreated; existing DBs are backfilled here.
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS SearchSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OwnerUserId TEXT NOT NULL,
                PageKind TEXT NOT NULL,
                Title TEXT NOT NULL,
                Filter TEXT NOT NULL,
                CriteriaJson TEXT NOT NULL,
                SortColumn TEXT,
                SortDescending INTEGER NOT NULL,
                ColumnKeysCsv TEXT NOT NULL,
                SelectedIdsCsv TEXT NOT NULL,
                ExpandedIdsCsv TEXT NOT NULL,
                IsOpen INTEGER NOT NULL,
                CreatedUtc TEXT NOT NULL,
                LastUsedUtc TEXT NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS IX_SearchSessions_Owner_Page_Open
            ON SearchSessions (OwnerUserId, PageKind, IsOpen)
            """);
        // Audit retention cold tier: rows moved here when the hot AuditEvents
        // table exceeds its row cap. Fresh DBs get it from EnsureCreated.
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS AuditEventArchive (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ObjectKind TEXT NOT NULL,
                ObjectId INTEGER NOT NULL,
                ObjectLabel TEXT NOT NULL,
                Action TEXT NOT NULL,
                FieldName TEXT,
                OldValue TEXT,
                NewValue TEXT,
                Actor TEXT NOT NULL,
                TimestampUtc TEXT NOT NULL,
                ArchivedUtc TEXT NOT NULL
            )
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS IX_AuditEventArchive_Object
            ON AuditEventArchive (ObjectKind, ObjectId)
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS IX_AuditEvents_Object
            ON AuditEvents (ObjectKind, ObjectId)
            """);
        BackfillLabels(db);
    }

    // One-time promotion of the legacy RecordItem.Labels comma-separated text
    // into the Labels / ObjectLabels tables. Idempotent: skips records whose
    // labels are already fully assigned.
    public static void BackfillLabels(PrimDbContext db)
    {
        var existing = db.Labels.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
        var assigned = db.ObjectLabels
            .Where(o => o.ObjectKind == "Record")
            .Select(o => new { o.LabelId, o.ObjectId })
            .ToList();
        var assignedSet = assigned.Select(a => (a.LabelId, a.ObjectId)).ToHashSet();
        var records = db.Records.Where(r => r.Labels != null && r.Labels != "").ToList();
        foreach (var r in records)
        {
            var names = r.Labels!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var name in names)
            {
                if (!existing.TryGetValue(name, out var label))
                {
                    label = new Label { Name = name, CreatedUtc = DateTime.UtcNow, CreatedBy = "backfill" };
                    db.Labels.Add(label);
                    db.SaveChanges();
                    existing[name] = label;
                }
                if (assignedSet.Add((label.Id, r.Id)))
                    db.ObjectLabels.Add(new ObjectLabel { LabelId = label.Id, ObjectKind = "Record", ObjectId = r.Id });
            }
        }
        db.SaveChanges();
    }

    // Existing rows predate AssigneeRefId / missing HomeRefId: resolve the stored
    // home/assignee names against Users/Containers/Locations so old links navigate.
    // Best-effort and idempotent; unresolvable names keep a null ref (link
    // renders as plain text) until the item is next saved.
    private static void BackfillRefIds(PrimDbContext db)
    {
        var users = db.Users.ToList();
        var containers = db.Containers.ToList();
        var locations = db.Locations.ToList();
        int? FindUser(string name) =>
            users.FirstOrDefault(u => u.UserId == name || u.DisplayName == name)?.Id;
        int? FindContainer(string name) =>
            containers.FirstOrDefault(c => c.ContainerName == name)?.Id;
        int? FindLocation(string name) =>
            locations.FirstOrDefault(l => l.LocationName == name)?.Id;

        int? Resolve(string? kind, string name) => kind switch
        {
            "User" => FindUser(name),
            "Container" => FindContainer(name),
            "Location" => FindLocation(name),
            _ => null
        };

        foreach (var r in db.Records.Where(r => r.AssigneeRefId == null && r.Assignee != null))
        {
            var id = Resolve(r.AssigneeKind, r.Assignee);
            if (id != null) r.AssigneeRefId = id;
        }
        foreach (var c in db.Containers.Where(c => c.AssigneeRefId == null && c.Assignee != null))
        {
            var id = Resolve(c.AssigneeKind, c.Assignee);
            if (id != null) c.AssigneeRefId = id;
        }
        foreach (var r in db.Records.Where(r => r.HomeRefId == null && r.Home != null))
        {
            var id = Resolve(r.HomeKind, r.Home);
            if (id != null) r.HomeRefId = id;
        }
        foreach (var c in db.Containers.Where(c => c.HomeRefId == null && c.Home != null))
        {
            var id = Resolve(c.HomeKind, c.Home);
            if (id != null) c.HomeRefId = id;
        }
        db.SaveChanges();
    }

    private static void AddColumn(PrimDbContext db, string table, string column, string type)
    {
        // table/column/type are fixed compile-time literals from UpgradeSchema
        // above, never user input — the EF interpolation warning is suppressed.
#pragma warning disable EF1002
        var existing = db.Database.SqlQueryRaw<string>(
                $"SELECT name FROM pragma_table_info('{table}')").ToList();
        if (!existing.Contains(column))
            db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN {column} {type}");
#pragma warning restore EF1002
    }

    // Existing databases predate password logins: give every user without a
    // password the dev password matching their username (username/password
    // are identical in the dev seed).
    public static void BackfillDevCredentials(PrimDbContext db)
    {
        foreach (var u in db.Users.Where(u => u.PasswordHash == null).ToList())
        {
            var (hash, salt) = Prim.Services.PasswordHasher.Hash(u.UserId);
            u.PasswordHash = hash; u.PasswordSalt = salt;
        }
        db.SaveChanges();
    }

    private static AppUser NewDevUser(string userId, string displayName, string role, string email, DateTime now)
    {
        // Dev password == username (explicitly temporary; production uses OAuth/SSO).
        var (hash, salt) = Prim.Services.PasswordHasher.Hash(userId);
        return new AppUser
        {
            UserId = userId, DisplayName = displayName, Role = role, Email = email,
            PasswordHash = hash, PasswordSalt = salt, CreatedUtc = now
        };
    }

    public static void EnsureSeeded(PrimDbContext db)
    {
        if (db.Users.Any()) return;
        var now = DateTime.UtcNow;

        // Dev login credentials (username / password, dev only — production uses
        // OAuth/SSO via IAuthProvider). Role-based usernames; no real names.
        //   admin01 / admin01       — Admin
        //   recordsmgr01 / recordsmgr01 — Records Manager
        //   mtnelson / mtnelson     — Staff
        var users = new[]
        {
            NewDevUser("admin01", "PRIM Administrator", "Admin", "admin01@prim.local", now),
            NewDevUser("recordsmgr01", "Records Manager", "Records Manager", "recordsmgr01@prim.local", now),
            NewDevUser("mtnelson", "Mike Nelson", "Staff", "mtnelson@prim.local", now),
        };
        db.Users.AddRange(users);

        // Location hierarchy: BLDG CRC -> SFR 1 -> Row 1 -> Compartment 1 -> Shelf 1 (TIS-367)
        var bldg = new Location { LocationName = "BLDG CRC", LocationType = "Building", Barcode = "LOC000001", Description = "Central records center", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" };
        db.Locations.Add(bldg); db.SaveChanges();
        var sfr = new Location { LocationName = "SFR 1", LocationType = "Room", ParentId = bldg.Id, Barcode = "LOC000002", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" };
        db.Locations.Add(sfr); db.SaveChanges();
        var row = new Location { LocationName = "ROW 1", LocationType = "Row", ParentId = sfr.Id, Barcode = "LOC000003", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" };
        db.Locations.Add(row); db.SaveChanges();
        var comp = new Location { LocationName = "COMPARTMENT 1", LocationType = "Compartment", ParentId = row.Id, Barcode = "LOC000004", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" };
        db.Locations.Add(comp); db.SaveChanges();
        var shelf = new Location { LocationName = "SHELF 1", LocationType = "Shelf", ParentId = comp.Id, Barcode = "LOC000005", Description = "Top shelf, north wall", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" };
        db.Locations.Add(shelf);
        var freezer = new Location { LocationName = "FREEZER A", LocationType = "Freezer", ParentId = bldg.Id, Barcode = "LOC000006", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" };
        db.Locations.Add(freezer);
        db.SaveChanges();

        // Containers per TIS-1278 naming examples
        var containers = new[]
        {
            new Container { ContainerName = "HQ-SHIP-LD263S", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "SHIP", FormattedNumber = "LD263S", Barcode = "CON000001", Description = "Shipping box, lot D", Home = "SHELF 1", HomeKind = "Location", HomeRefId = shelf.Id, Assignee = "recordsmgr01", AssigneeKind = "User", LocationId = shelf.Id, CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
            new Container { ContainerName = "AT-SHIP-PLT-00002", ContainerType = "Pallet", FieldOffice = "AT", ContainerCode = "SHIP", FormattedNumber = "00002", Barcode = "CON000002", Home = "BLDG CRC", HomeKind = "Location", HomeRefId = bldg.Id, Assignee = "recordsmgr01", AssigneeKind = "User", LocationId = bldg.Id, CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
            new Container { ContainerName = "BIN464611", ContainerType = "Bin", FieldOffice = "HQ", ContainerCode = "9999", FormattedNumber = "464611", Barcode = "CON000003", Home = "ROW 1", HomeKind = "Location", HomeRefId = row.Id, Assignee = "recordsmgr01", AssigneeKind = "User", LocationId = row.Id, CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
            new Container { ContainerName = "TOTE02015", ContainerType = "Tote", FieldOffice = "HQ", ContainerCode = "9999", FormattedNumber = "02015", Barcode = "CON000004", Home = "COMPARTMENT 1", HomeKind = "Location", HomeRefId = comp.Id, Assignee = "mtnelson", AssigneeKind = "User", LocationId = comp.Id, CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
            new Container { ContainerName = "DT-0001-0001NA", ContainerType = "NARA Box", FieldOffice = "DT", ContainerCode = "NARA", FormattedNumber = "0001-0001NA", Barcode = "CON000005", Description = "NARA transfer box", Home = "BLDG CRC", HomeKind = "Location", HomeRefId = bldg.Id, Assignee = "recordsmgr01", AssigneeKind = "User", LocationId = bldg.Id, CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
            new Container { ContainerName = "HQ-TUBS-0059B", ContainerType = "Tub", FieldOffice = "HQ", ContainerCode = "TUBS", FormattedNumber = "0059B", Barcode = "CON000006", Home = "FREEZER A", HomeKind = "Location", HomeRefId = freezer.Id, Assignee = "mtnelson", AssigneeKind = "User", LocationId = freezer.Id, CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
        };
        db.Containers.AddRange(containers);
        db.SaveChanges();

        var box = db.Containers.First(c => c.ContainerName == "HQ-SHIP-LD263S");
        var tote = db.Containers.First(c => c.ContainerName == "TOTE02015");

        // Records: Case Classification / Field Office / Case Number / Subfile / Volume / Serials
        var records = new[]
        {
            new RecordItem { RecordNumber = "R-000001", Barcode = "REC000001", RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "12345", SubfileId = "A", Volume = "1", SerialStart = "1", SerialEnd = "250", AuxiliaryOffice = "AT", SecurityClassification = "Unclassified", Subject = "Quarterly review file", Notes = "Seeded demo record.", Home = "HQ-SHIP-LD263S", HomeKind = "Container", HomeRefId = box.Id, Assignee = "recordsmgr01", AssigneeKind = "User", State = "Active", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
            new RecordItem { RecordNumber = "R-000002", Barcode = "REC000002", RecordType = "Case File", CaseClassification = "92", FieldOffice = "NY", CaseNumber = "88710", Volume = "2", SerialStart = "1", SerialEnd = "96", IsBulky = true, SecurityClassification = "Confidential", Home = "TOTE02015", HomeKind = "Container", HomeRefId = tote.Id, Assignee = "recordsmgr01", AssigneeKind = "User", State = "Active", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
            new RecordItem { RecordNumber = "R-000003", Barcode = "REC000003", RecordType = "Compressed", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "12345", SubfileId = "B", Volume = "1", SecurityClassification = "Unclassified", Home = "HQ-SHIP-LD263S", HomeKind = "Container", HomeRefId = box.Id, Assignee = "recordsmgr01", AssigneeKind = "User", State = "Active", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
            new RecordItem { RecordNumber = "R-000004", Barcode = "REC000004", RecordType = "Abstract", CaseClassification = "65", FieldOffice = "WF", CaseNumber = "4451", Volume = "1", SerialStart = "1", SerialEnd = "12", Home = "recordsmgr01", HomeKind = "User", Assignee = "recordsmgr01", AssigneeKind = "User", State = "Disposition Ready for Review", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
            new RecordItem { RecordNumber = "R-000005", Barcode = "REC000005", RecordType = "HQ In Service", CaseClassification = "77", FieldOffice = "HQ", CaseNumber = "99012", Volume = "1", SerialStart = "1", SerialEnd = "40", Home = "SHELF 1", HomeKind = "Location", HomeRefId = shelf.Id, Assignee = "mtnelson", AssigneeKind = "User", State = "Active", CreatedUtc = now, CreatedBy = "admin01", LastUpdatedUtc = now, LastUpdatedBy = "admin01" },
        };
        db.Records.AddRange(records);

        db.Announcements.Add(new Announcement { Message = "", IsActive = false, UpdatedBy = "admin01", UpdatedUtc = now });
        db.SaveChanges();
        // Seed rows omit AssigneeRefId (and R-000004's user home lacks HomeRefId);
        // resolve them the same way old databases are backfilled.
        BackfillRefIds(db);
    }
}
