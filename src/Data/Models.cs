using System.ComponentModel.DataAnnotations;

namespace Prim.Data;

// ---------------------------------------------------------------------------
// RECORD — TIS-372 / TIS-1588 / TIS-1924 field order and naming rules.
// TIS-2057: "Subfile" is one word; "ID" always capitalized; label is "Volume".
// ---------------------------------------------------------------------------
public class RecordItem
{
    public int Id { get; set; }

    [Required] public string RecordNumber { get; set; } = "";   // system-assigned R-000001
    [Required] public string Barcode { get; set; } = "";        // system-assigned

    [Required] public string RecordType { get; set; } = "Case File"; // Case File, Compressed, Abstract, HQ Bureau Applicant, HQ In Service, HQ Out of Service
    [Required] public string CaseClassification { get; set; } = "";  // forced UPPERCASE
    [Required] public string FieldOffice { get; set; } = "";         // FO code, e.g. HQ
    [Required] public string CaseNumber { get; set; } = "";          // forced UPPERCASE
    public string? SubfileId { get; set; }                          // forced UPPERCASE
    [Required] public string Volume { get; set; } = "";              // label "Volume" (TIS-1743 AC2)
    public string? SerialStart { get; set; }                        // forced UPPERCASE
    public string? SerialEnd { get; set; }                          // forced UPPERCASE
    public string? AuxiliaryOffice { get; set; }
    public bool IsBulky { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsControlFile { get; set; }
    public string? Labels { get; set; }                 // comma-separated record labels
    public string? SecurityClassification { get; set; } // NOT required (TIS-372 test feedback)
    public string? Subject { get; set; }                // after Notes per TIS-1588
    public string? Notes { get; set; }

    // Home / Assignee replace the old Parent field (TIS-1747). Home defaults to
    // the creating user; Assignee defaults to Home (TIS-2201).
    [Required] public string Home { get; set; } = "";
    public string? HomeKind { get; set; }               // Location | Container | User
    public string? AssigneeKind { get; set; }            // Container | Location | User
    public int? HomeRefId { get; set; }
    [Required] public string Assignee { get; set; } = "";
    public int? AssigneeRefId { get; set; }             // navigable assignee target (mirrors HomeRefId)

    public int? ParentRecordId { get; set; }            // child of a Compressed record (expandable tree)

    [Required] public string State { get; set; } = "Active"; // TIS-2294 spike values
    public bool Deleted { get; set; }
    public string? DeleteReason { get; set; }
    public string? MergedIntoBarcode { get; set; }

    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime LastUpdatedUtc { get; set; }
    public string LastUpdatedBy { get; set; } = "";
    [ConcurrencyCheck] public int RowVersion { get; set; }   // TIS-597 optimistic concurrency
}

// ---------------------------------------------------------------------------
// CONTAINER — TIS-576 / TIS-1278 naming / TIS-1747 Home+Assignee.
// Containers have NO Record Number in PRIM (TIS-1743 AC3).
// ---------------------------------------------------------------------------
public class Container
{
    public int Id { get; set; }
    [Required] public string ContainerName { get; set; } = ""; // system-generated per TIS-1278
    [Required] public string ContainerType { get; set; } = "Box"; // Box, Tub, Crate, Pallet, Tote, Truck, Bin, NARA Box, Virtual Container
    [Required] public string FieldOffice { get; set; } = "";
    [Required] public string ContainerCode { get; set; } = "SHIP"; // SHIP, NARA, RESH, 9999, PENT...
    [Required] public string FormattedNumber { get; set; } = "";
    public string? Description { get; set; }              // initial case (TIS-343)
    [Required] public string Barcode { get; set; } = "";
    [Required] public string Home { get; set; } = "";
    public string? HomeKind { get; set; }
    public string? AssigneeKind { get; set; }
    public int? HomeRefId { get; set; }
    [Required] public string Assignee { get; set; } = "";
    public int? AssigneeRefId { get; set; }             // navigable assignee target (mirrors HomeRefId)
    public int? ParentContainerId { get; set; }           // nesting
    public int? LocationId { get; set; }                 // sits inside a location
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime LastUpdatedUtc { get; set; }
    public string LastUpdatedBy { get; set; } = "";
    [ConcurrencyCheck] public int RowVersion { get; set; }
}

// ---------------------------------------------------------------------------
// LOCATION — TIS-575 / TIS-367 N-level hierarchy.
// ---------------------------------------------------------------------------
public class Location
{
    public int Id { get; set; }
    [Required] public string LocationName { get; set; } = ""; // UPPERCASE, unique per type+parent
    [Required] public string LocationType { get; set; } = "Shelf"; // Building, Carousel, Compartment, Freezer, Room, Row, Shelf, Virtual
    public int? ParentId { get; set; }                   // N-level parent/child
    public string? Description { get; set; }            // initial case
    [Required] public string Barcode { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime LastUpdatedUtc { get; set; }
    public string LastUpdatedBy { get; set; } = "";
    [ConcurrencyCheck] public int RowVersion { get; set; }
}

// ---------------------------------------------------------------------------
// USER — no field schema in the TIS export (gap); minimal workable shape.
// ---------------------------------------------------------------------------
public class AppUser
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";   // login
    [Required] public string DisplayName { get; set; } = "";
    [Required] public string Role { get; set; } = "Staff"; // Admin, Records Manager, Staff
    public string? Email { get; set; }
    public bool Active { get; set; } = true;
    public int? LocationId { get; set; }                 // membership in a location
    public string? PasswordHash { get; set; }           // dev password auth (PBKDF2); production uses OAuth/SSO via IAuthProvider
    public string? PasswordSalt { get; set; }
    public DateTime CreatedUtc { get; set; }
    [ConcurrencyCheck] public int RowVersion { get; set; }
}

// ---------------------------------------------------------------------------
// AUDIT — TIS-2202 / TIS-348: who / what field / old / new / when (UTC).
// ---------------------------------------------------------------------------
public class AuditEvent
{
    public int Id { get; set; }
    [Required] public string ObjectKind { get; set; } = ""; // Record | Container | Location | User
    public int ObjectId { get; set; }
    [Required] public string ObjectLabel { get; set; } = "";
    [Required] public string Action { get; set; } = "";     // Created, Updated, Moved, Deleted, Restored, Type Changed
    public string? FieldName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    [Required] public string Actor { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
}

// ---------------------------------------------------------------------------
// WORKSPACE — TIS-1411 / TIS-351: five personal workspaces + favorites.
// ---------------------------------------------------------------------------
public class WorkspaceItem
{
    public int Id { get; set; }
    [Required] public string OwnerUserId { get; set; } = "";
    [Required] public string Slot { get; set; } = "";      // Workspace 1..5 | Favorites
    [Required] public string ObjectKind { get; set; } = "";
    public int ObjectId { get; set; }
    [Required] public string Label { get; set; } = "";
    public DateTime AddedUtc { get; set; }
}

public class SavedSearch
{
    public int Id { get; set; }
    [Required] public string OwnerUserId { get; set; } = "";
    [Required] public string Name { get; set; } = "";
    [Required] public string ObjectKind { get; set; } = "";
    public string FieldsCsv { get; set; } = "";
    public string Criteria { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

// Admin-controlled outage/announcement banner — TIS-1460.
public class Announcement
{
    public int Id { get; set; }
    public string Message { get; set; } = "";
    public bool IsActive { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}

// Per-user data grid column layout (customizable columns, persisted per user).
public class UserGridLayout
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required] public string GridId { get; set; } = "";   // records | containers | locations | users | workspaces
    [Required] public string ColumnsCsv { get; set; } = ""; // ordered visible column keys
    public DateTime UpdatedUtc { get; set; }
}

// ---------------------------------------------------------------------------
// LABELS — named collections of objects. A label is created once (Labels
// screen) and attached to any number of records, containers, locations, or
// users via ObjectLabel. Clicking a label anywhere navigates to its members.
// The legacy RecordItem.Labels comma-separated text is backfilled into these
// tables on upgrade (SeedData.BackfillLabels).
// ---------------------------------------------------------------------------
public class Label
{
    public int Id { get; set; }
    [Required] public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    [Required] public string CreatedBy { get; set; } = "";
}

public class ObjectLabel
{
    public int Id { get; set; }
    public int LabelId { get; set; }
    [Required] public string ObjectKind { get; set; } = ""; // Record | Container | Location | User
    public int ObjectId { get; set; }
}
