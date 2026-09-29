namespace Prim.Data;

/// <summary>Demo dataset so the app is usable the first time it starts.</summary>
public static class SeedData
{
    public static readonly string[] FieldOffices =
    {
        "HQ - Washington", "AT - Atlanta", "AX - Alexandria", "BO - Boston", "CE - Charlotte",
        "CG - Chicago", "DL - Dallas", "DN - Denver", "EP - El Paso", "HO - Houston",
        "JK - Jackson", "KC - Kansas City", "LV - Las Vegas", "LA - Los Angeles", "MM - Miami",
        "MP - Minneapolis", "NY - New York", "NK - Newark", "PH - Phoenix", "PX - Pittsburgh",
        "PD - Portland", "RH - Richmond", "SC - Sacramento", "SE - Seattle", "SF - San Francisco"
    };

    public static readonly string[] RecordTypes =
        { "Standard", "Compressed", "Abstract", "HQ Bureau Applicant", "HQ In Service", "HQ Out of Service" };

    public static readonly string[] ContainerTypes =
        { "Box", "Tub", "Crate", "Pallet", "Tote", "Truck", "Bin", "NARA Box", "Virtual Container" };

    public static readonly string[] LocationTypes =
        { "Building", "Carousel", "Compartment", "Freezer", "Room", "Row", "Shelf", "Virtual" };

    public static readonly string[] SecurityClassifications =
        { "Unclassified", "Confidential", "Secret", "Top Secret" };

    public static readonly string[] RecordStates =
        { "Active", "Disposition Ready for Review", "Reviewed Eligible", "Reviewed Ineligible", "Archived" };

    public static readonly string[] Roles = { "Admin", "Records Manager", "Staff" };

    public static void EnsureSeeded(PrimDbContext db)
    {
        if (db.Users.Any()) return;
        var now = DateTime.UtcNow;

        var users = new[]
        {
            new AppUser { UserId = "admin",   DisplayName = "PRIM Administrator", Role = "Admin",           Email = "admin@prim.local", CreatedUtc = now },
            new AppUser { UserId = "krosenberg", DisplayName = "Kelly Rosenberg", Role = "Records Manager", Email = "krosenberg@prim.local", CreatedUtc = now },
            new AppUser { UserId = "chawes",  DisplayName = "Caitlin Hawes",    Role = "Records Manager", Email = "chawes@prim.local", CreatedUtc = now },
            new AppUser { UserId = "mtnelson", DisplayName = "Mike Nelson",     Role = "Staff",            Email = "mtnelson@prim.local", CreatedUtc = now },
        };
        db.Users.AddRange(users);

        // Location hierarchy: BLDG CRC -> SFR 1 -> Row 1 -> Compartment 1 -> Shelf 1 (TIS-367)
        var bldg = new Location { LocationName = "BLDG CRC", LocationType = "Building", Barcode = "LOC000001", Description = "Central records center", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" };
        db.Locations.Add(bldg); db.SaveChanges();
        var sfr = new Location { LocationName = "SFR 1", LocationType = "Room", ParentId = bldg.Id, Barcode = "LOC000002", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" };
        db.Locations.Add(sfr); db.SaveChanges();
        var row = new Location { LocationName = "ROW 1", LocationType = "Row", ParentId = sfr.Id, Barcode = "LOC000003", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" };
        db.Locations.Add(row); db.SaveChanges();
        var comp = new Location { LocationName = "COMPARTMENT 1", LocationType = "Compartment", ParentId = row.Id, Barcode = "LOC000004", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" };
        db.Locations.Add(comp); db.SaveChanges();
        var shelf = new Location { LocationName = "SHELF 1", LocationType = "Shelf", ParentId = comp.Id, Barcode = "LOC000005", Description = "Top shelf, north wall", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" };
        db.Locations.Add(shelf);
        var freezer = new Location { LocationName = "FREEZER A", LocationType = "Freezer", ParentId = bldg.Id, Barcode = "LOC000006", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" };
        db.Locations.Add(freezer);
        db.SaveChanges();

        // Containers per TIS-1278 naming examples
        var containers = new[]
        {
            new Container { ContainerName = "HQ-SHIP-LD263S", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "SHIP", FormattedNumber = "LD263S", Barcode = "CON000001", Description = "Shipping box, lot D", Home = "SHELF 1", HomeKind = "Location", HomeRefId = shelf.Id, Assignee = "krosenberg", LocationId = shelf.Id, CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
            new Container { ContainerName = "AT-SHIP-PLT-00002", ContainerType = "Pallet", FieldOffice = "AT", ContainerCode = "SHIP", FormattedNumber = "00002", Barcode = "CON000002", Home = "BLDG CRC", HomeKind = "Location", HomeRefId = bldg.Id, Assignee = "chawes", LocationId = bldg.Id, CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
            new Container { ContainerName = "BIN464611", ContainerType = "Bin", FieldOffice = "HQ", ContainerCode = "9999", FormattedNumber = "464611", Barcode = "CON000003", Home = "ROW 1", HomeKind = "Location", HomeRefId = row.Id, Assignee = "krosenberg", LocationId = row.Id, CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
            new Container { ContainerName = "TOTE02015", ContainerType = "Tote", FieldOffice = "HQ", ContainerCode = "9999", FormattedNumber = "02015", Barcode = "CON000004", Home = "COMPARTMENT 1", HomeKind = "Location", HomeRefId = comp.Id, Assignee = "mtnelson", LocationId = comp.Id, CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
            new Container { ContainerName = "DT-0001-0001NA", ContainerType = "NARA Box", FieldOffice = "DT", ContainerCode = "NARA", FormattedNumber = "0001-0001NA", Barcode = "CON000005", Description = "NARA transfer box", Home = "BLDG CRC", HomeKind = "Location", HomeRefId = bldg.Id, Assignee = "chawes", LocationId = bldg.Id, CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
            new Container { ContainerName = "HQ-TUBS-0059B", ContainerType = "Tub", FieldOffice = "HQ", ContainerCode = "TUBS", FormattedNumber = "0059B", Barcode = "CON000006", Home = "FREEZER A", HomeKind = "Location", HomeRefId = freezer.Id, Assignee = "mtnelson", LocationId = freezer.Id, CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
        };
        db.Containers.AddRange(containers);
        db.SaveChanges();

        var box = db.Containers.First(c => c.ContainerName == "HQ-SHIP-LD263S");
        var tote = db.Containers.First(c => c.ContainerName == "TOTE02015");

        // Records: Case Classification / Field Office / Case Number / Subfile / Volume / Serials
        var records = new[]
        {
            new RecordItem { RecordNumber = "R-000001", Barcode = "REC000001", RecordType = "Standard", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "12345", SubfileId = "A", Volume = "1", SerialStart = "1", SerialEnd = "250", AuxiliaryOffice = "AT", SecurityClassification = "Unclassified", Subject = "Quarterly review file", Notes = "Seeded demo record.", Home = "HQ-SHIP-LD263S", HomeKind = "Container", HomeRefId = box.Id, Assignee = "krosenberg", State = "Active", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
            new RecordItem { RecordNumber = "R-000002", Barcode = "REC000002", RecordType = "Standard", CaseClassification = "92", FieldOffice = "NY", CaseNumber = "88710", Volume = "2", SerialStart = "1", SerialEnd = "96", IsBulky = true, SecurityClassification = "Confidential", Home = "TOTE02015", HomeKind = "Container", HomeRefId = tote.Id, Assignee = "chawes", State = "Active", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
            new RecordItem { RecordNumber = "R-000003", Barcode = "REC000003", RecordType = "Compressed", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "12345", SubfileId = "B", Volume = "1", SecurityClassification = "Unclassified", Home = "HQ-SHIP-LD263S", HomeKind = "Container", HomeRefId = box.Id, Assignee = "krosenberg", State = "Active", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
            new RecordItem { RecordNumber = "R-000004", Barcode = "REC000004", RecordType = "Abstract", CaseClassification = "65", FieldOffice = "WF", CaseNumber = "4451", Volume = "1", SerialStart = "1", SerialEnd = "12", Home = "krosenberg", HomeKind = "User", Assignee = "krosenberg", State = "Disposition Ready for Review", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
            new RecordItem { RecordNumber = "R-000005", Barcode = "REC000005", RecordType = "HQ In Service", CaseClassification = "77", FieldOffice = "HQ", CaseNumber = "99012", Volume = "1", SerialStart = "1", SerialEnd = "40", Home = "SHELF 1", HomeKind = "Location", HomeRefId = shelf.Id, Assignee = "mtnelson", State = "Active", CreatedUtc = now, CreatedBy = "admin", LastUpdatedUtc = now, LastUpdatedBy = "admin" },
        };
        db.Records.AddRange(records);

        db.Announcements.Add(new Announcement { Message = "", IsActive = false, UpdatedBy = "admin", UpdatedUtc = now });
        db.SaveChanges();
    }
}
