using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Prim.Data;

namespace Prim.Services;

/// <summary>One breadcrumb segment in an object's ancestor path (root first).</summary>
public record PathSeg(string Kind, int Id, string Label);

/// <summary>One child row rendered under an expanded grid row.</summary>
public record ChildItem(string Kind, int Id, string Label, string Detail, bool HasChildren);

/// <summary>CRUD + audit + search over the four PRIM object types.</summary>
public class PrimService
{    private readonly IDbContextFactory<PrimDbContext> _factory;
    public PrimService(IDbContextFactory<PrimDbContext> factory) => _factory = factory;

    // ---------------- reads ----------------
    public async Task<List<RecordItem>> GetRecordsAsync(bool includeDeleted = false)
    {
        using var db = _factory.CreateDbContext();
        var q = db.Records.AsQueryable();
        if (!includeDeleted) q = q.Where(r => !r.Deleted);
        return await q.OrderBy(r => r.RecordNumber).ToListAsync();
    }
    public async Task<List<Container>> GetContainersAsync()
    {
        using var db = _factory.CreateDbContext();
        return await db.Containers.OrderBy(c => c.ContainerName).ToListAsync();
    }
    public async Task<List<Location>> GetLocationsAsync()
    {
        using var db = _factory.CreateDbContext();
        return await db.Locations.OrderBy(l => l.LocationName).ToListAsync();
    }
    public async Task<List<AppUser>> GetUsersAsync()
    {
        using var db = _factory.CreateDbContext();
        return await db.Users.OrderBy(u => u.DisplayName).ToListAsync();
    }
    public async Task<RecordItem?> GetRecordAsync(int id)
    { using var db = _factory.CreateDbContext(); return await db.Records.FindAsync(id); }
    public async Task<Container?> GetContainerAsync(int id)
    { using var db = _factory.CreateDbContext(); return await db.Containers.FindAsync(id); }
    public async Task<Location?> GetLocationAsync(int id)
    { using var db = _factory.CreateDbContext(); return await db.Locations.FindAsync(id); }
    public async Task<AppUser?> GetUserAsync(int id)
    { using var db = _factory.CreateDbContext(); return await db.Users.FindAsync(id); }

    public async Task<List<AuditEvent>> GetAuditAsync(string kind, int id)
    {
        using var db = _factory.CreateDbContext();
        return await db.AuditEvents.Where(a => a.ObjectKind == kind && a.ObjectId == id)
            .OrderByDescending(a => a.TimestampUtc).ToListAsync();
    }

    // ---------- Advanced search (AND/OR across criteria rows) ----------
    public Task<List<RecordItem>> AdvancedSearchRecordsAsync(List<(string Field, string Op, string Value)> rows, string logic)
    {
        return Task.Run(() =>
        {
            using var db = _factory.CreateDbContext();
            var matchers = rows.Select(r => BuildMatcher(r.Field, r.Op, r.Value)).ToList();
            var q = db.Records.Where(r => !r.Deleted).AsEnumerable();
            q = logic == "OR" ? q.Where(r => matchers.Any(m => m(r))) : q.Where(r => matchers.All(m => m(r)));
            return q.OrderBy(r => r.RecordNumber).Take(500).ToList();
        });
    }

    private static Func<RecordItem, bool> BuildMatcher(string field, string op, string value)
    {
        var rx = new Regex("^" + Regex.Escape(value).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        Func<string?, bool> test = s => op switch
        {
            "StartsWith" => (s ?? "").StartsWith(value.TrimEnd('*', '?'), StringComparison.OrdinalIgnoreCase),
            "EndsWith" => (s ?? "").EndsWith(value.TrimStart('*', '?'), StringComparison.OrdinalIgnoreCase),
            _ => rx.IsMatch(s ?? ""),
        };
        return field switch
        {
            "RecordNumber" => r => test(r.RecordNumber),
            "RecordType" => r => test(r.RecordType),
            "CaseClassification" => r => test(r.CaseClassification),
            "FieldOffice" => r => test(r.FieldOffice),
            "CaseNumber" => r => test(r.CaseNumber),
            "SubfileId" => r => test(r.SubfileId),
            "Volume" => r => test(r.Volume),
            "SerialStart" => r => test(r.SerialStart),
            "SerialEnd" => r => test(r.SerialEnd),
            "Barcode" => r => test(r.Barcode),
            "Home" => r => test(r.Home),
            "Assignee" => r => test(r.Assignee),
            "Subject" => r => test(r.Subject),
            "State" => r => test(r.State),
            _ => _ => true,
        };
    }

    public async Task<string?> GetAnnouncementAsync()
    {
        using var db = _factory.CreateDbContext();
        var a = await db.Announcements.OrderBy(a => a.Id).FirstOrDefaultAsync();
        return a is { IsActive: true } && !string.IsNullOrWhiteSpace(a.Message) ? a.Message : null;
    }
    public async Task<Announcement?> GetAnnouncementRecordAsync()
    { using var db = _factory.CreateDbContext(); return await db.Announcements.OrderBy(a => a.Id).FirstOrDefaultAsync(); }

    public async Task SetAnnouncementAsync(string message, bool active, string actor)
    {
        using var db = _factory.CreateDbContext();
        var a = await db.Announcements.OrderBy(a => a.Id).FirstOrDefaultAsync() ?? new Announcement();
        if (a.Id == 0) db.Announcements.Add(a);
        a.Message = message; a.IsActive = active; a.UpdatedBy = actor; a.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // ---------------- search (TIS-1742/1743): wildcards * and ? ----------------
    private static string ToLike(string criteria) =>
        criteria.Replace("%", "[%]").Replace("_", "[_]").Replace("*", "%").Replace("?", "_");

    public async Task<List<RecordItem>> SearchRecordsAsync(Dictionary<string, string> filters)
    {
        using var db = _factory.CreateDbContext();
        var q = db.Records.Where(r => !r.Deleted).AsQueryable();
        foreach (var (field, crit) in filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
        {
            var like = ToLike(crit.Trim());
            q = field switch
            {
                "Barcode" => q.Where(r => EF.Functions.Like(r.Barcode, like)),
                "Case Classification" or "CaseClassification" => q.Where(r => EF.Functions.Like(r.CaseClassification, like)),
                "Field Office" or "FieldOffice" => q.Where(r => EF.Functions.Like(r.FieldOffice, like)),
                "Case Number" or "CaseNumber" => q.Where(r => EF.Functions.Like(r.CaseNumber, like)),
                "Subfile ID" or "SubfileId" => q.Where(r => r.SubfileId != null && EF.Functions.Like(r.SubfileId, like)),
                "Volume" => q.Where(r => EF.Functions.Like(r.Volume, like)),
                "Serial Start" or "SerialStart" => q.Where(r => r.SerialStart != null && EF.Functions.Like(r.SerialStart, like)),
                "Serial End" or "SerialEnd" => q.Where(r => r.SerialEnd != null && EF.Functions.Like(r.SerialEnd, like)),
                "Record Number" or "RecordNumber" => q.Where(r => EF.Functions.Like(r.RecordNumber, like)),
                "Home" => q.Where(r => EF.Functions.Like(r.Home, like)),
                "Assignee" => q.Where(r => EF.Functions.Like(r.Assignee, like)),
                _ => q
            };
        }
        return await q.OrderBy(r => r.RecordNumber).ToListAsync();
    }

    public async Task<List<Container>> SearchContainersAsync(Dictionary<string, string> filters)
    {
        using var db = _factory.CreateDbContext();
        var q = db.Containers.AsQueryable();
        foreach (var (field, crit) in filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
        {
            var like = ToLike(crit.Trim());
            q = field switch
            {
                "Barcode" => q.Where(c => EF.Functions.Like(c.Barcode, like)),
                "Container Name" or "ContainerName" => q.Where(c => EF.Functions.Like(c.ContainerName, like)),
                "Field Office" or "FieldOffice" => q.Where(c => EF.Functions.Like(c.FieldOffice, like)),
                "Container Code" or "ContainerCode" => q.Where(c => EF.Functions.Like(c.ContainerCode, like)),
                "Home" => q.Where(c => EF.Functions.Like(c.Home, like)),
                "Assignee" => q.Where(c => EF.Functions.Like(c.Assignee, like)),
                _ => q
            };
        }
        return await q.OrderBy(c => c.ContainerName).ToListAsync();
    }

    public async Task<List<Location>> SearchLocationsAsync(string? nameCrit)
    {
        using var db = _factory.CreateDbContext();
        var q = db.Locations.AsQueryable();
        if (!string.IsNullOrWhiteSpace(nameCrit))
        {
            var like = ToLike(nameCrit.Trim());
            q = q.Where(l => EF.Functions.Like(l.LocationName, like));
        }
        return await q.OrderBy(l => l.LocationName).ToListAsync();
    }

    // ---------------- writes ----------------
    private async Task AuditAsync(PrimDbContext db, string kind, int id, string label,
        string action, string actor, string? field = null, string? oldV = null, string? newV = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            ObjectKind = kind, ObjectId = id, ObjectLabel = label, Action = action,
            FieldName = field, OldValue = oldV, NewValue = newV,
            Actor = actor, TimestampUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static string NextNumber(IEnumerable<string> existing, string prefix, int width)
    {
        int max = 0;
        foreach (var s in existing)
            if (s.StartsWith(prefix) && int.TryParse(s[prefix.Length..], out var n) && n > max) max = n;
        return prefix + (max + 1).ToString().PadLeft(width, '0');
    }

    // Returns (ok, error). Implements TIS-597 optimistic concurrency via RowVersion.
    public async Task<(bool Ok, string? Error)> SaveRecordAsync(RecordItem input, string actor)
    {
        using var db = _factory.CreateDbContext();
        input.CaseClassification = input.CaseClassification.ToUpperInvariant();
        input.FieldOffice = (input.FieldOffice ?? "").ToUpperInvariant();
        input.CaseNumber = (input.CaseNumber ?? "").ToUpperInvariant();
        input.SubfileId = input.SubfileId?.ToUpperInvariant();
        input.SerialStart = input.SerialStart?.ToUpperInvariant();
        input.SerialEnd = input.SerialEnd?.ToUpperInvariant();

        // Homing rules (service-level): records home only to containers, locations, users.
        var homeErr = HomeRules.ValidateHome("Record", input.HomeKind);
        if (homeErr != null) return (false, homeErr);
        var assigneeErr = HomeRules.ValidateHome("Record", input.AssigneeKind, "Assignee");
        if (assigneeErr != null) return (false, assigneeErr);
        input.AssigneeRefId ??= await ResolveAssigneeRefAsync(db, input.AssigneeKind, input.Assignee);
        input.HomeRefId ??= await ResolveAssigneeRefAsync(db, input.HomeKind, input.Home);

        // Compressed-record children: only Compressed records may have child records.
        var childErr = await ValidateRecordParentAsync(db, input);
        if (childErr != null) return (false, childErr);

        if (input.Id == 0)
        {
            input.RecordNumber = NextNumber(db.Records.Select(r => r.RecordNumber), "R-", 6);
            input.Barcode = NextNumber(db.Records.Select(r => r.Barcode), "REC", 6);
            input.CreatedUtc = input.LastUpdatedUtc = DateTime.UtcNow;
            input.CreatedBy = input.LastUpdatedBy = actor;
            db.Records.Add(input);
            await db.SaveChangesAsync();
            await AuditAsync(db, "Record", input.Id, input.RecordNumber, "Created", actor);
            return (true, null);
        }

        var cur = await db.Records.FindAsync(input.Id);
        if (cur == null) return (false, "Record no longer exists.");
        if (cur.RowVersion != input.RowVersion)
            return (false, "Not the latest version — another user changed this record. Your view was refreshed.");

        var tracked = new List<(string F, string? O, string? N)>();
        void Chg(string f, string? o, string? n) { if (o != n) tracked.Add((f, o, n)); }
        Chg("RecordType", cur.RecordType, input.RecordType); Chg("CaseClassification", cur.CaseClassification, input.CaseClassification);
        Chg("FieldOffice", cur.FieldOffice, input.FieldOffice); Chg("CaseNumber", cur.CaseNumber, input.CaseNumber);
        Chg("SubfileId", cur.SubfileId, input.SubfileId); Chg("Volume", cur.Volume, input.Volume);
        Chg("SerialStart", cur.SerialStart, input.SerialStart); Chg("SerialEnd", cur.SerialEnd, input.SerialEnd);
        Chg("AuxiliaryOffice", cur.AuxiliaryOffice, input.AuxiliaryOffice);
        Chg("IsBulky", cur.IsBulky.ToString(), input.IsBulky.ToString()); Chg("IsAdmin", cur.IsAdmin.ToString(), input.IsAdmin.ToString());
        Chg("IsControlFile", cur.IsControlFile.ToString(), input.IsControlFile.ToString());
        Chg("Labels", cur.Labels, input.Labels); Chg("SecurityClassification", cur.SecurityClassification, input.SecurityClassification);
        Chg("Subject", cur.Subject, input.Subject); Chg("Notes", cur.Notes, input.Notes);
        Chg("State", cur.State, input.State);
        if (cur.Home != input.Home || cur.Assignee != input.Assignee)
            tracked.Add(("Movement", $"Home={cur.Home}, Assignee={cur.Assignee}", $"Home={input.Home}, Assignee={input.Assignee}"));

        var typeChanged = cur.RecordType != input.RecordType;
        db.Entry(cur).CurrentValues.SetValues(input);
        cur.LastUpdatedUtc = DateTime.UtcNow; cur.LastUpdatedBy = actor; cur.RowVersion++;
        await db.SaveChangesAsync();
        foreach (var (f, o, n) in tracked)
            await AuditAsync(db, "Record", cur.Id, cur.RecordNumber, typeChanged && f == "RecordType" ? "Type Changed" : "Updated", actor, f, o, n);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SaveContainerAsync(Container input, string actor)
    {
        using var db = _factory.CreateDbContext();
        input.ContainerName = input.ContainerName.ToUpperInvariant();

        // Homing rules (service-level): containers home only to containers, locations, users.
        var homeErr = HomeRules.ValidateHome("Container", input.HomeKind);
        if (homeErr != null) return (false, homeErr);
        var assigneeErr = HomeRules.ValidateHome("Container", input.AssigneeKind, "Assignee");
        if (assigneeErr != null) return (false, assigneeErr);
        input.AssigneeRefId ??= await ResolveAssigneeRefAsync(db, input.AssigneeKind, input.Assignee);
        input.HomeRefId ??= await ResolveAssigneeRefAsync(db, input.HomeKind, input.Home);

        if (input.Id == 0)
        {
            input.Barcode = NextNumber(db.Containers.Select(c => c.Barcode), "CON", 6);
            input.CreatedUtc = input.LastUpdatedUtc = DateTime.UtcNow;
            input.CreatedBy = input.LastUpdatedBy = actor;
            db.Containers.Add(input);
            await db.SaveChangesAsync();
            await AuditAsync(db, "Container", input.Id, input.ContainerName, "Created", actor);
            return (true, null);
        }
        var cur = await db.Containers.FindAsync(input.Id);
        if (cur == null) return (false, "Container no longer exists.");
        if (cur.RowVersion != input.RowVersion)
            return (false, "Not the latest version — another user changed this container.");
        var oldName = cur.ContainerName; var oldHome = cur.Home; var oldAssignee = cur.Assignee;
        db.Entry(cur).CurrentValues.SetValues(input);
        cur.LastUpdatedUtc = DateTime.UtcNow; cur.LastUpdatedBy = actor; cur.RowVersion++;
        await db.SaveChangesAsync();
        await AuditAsync(db, "Container", cur.Id, cur.ContainerName, "Updated", actor, "Fields",
            $"Name={oldName}, Home={oldHome}, Assignee={oldAssignee}",
            $"Name={cur.ContainerName}, Home={cur.Home}, Assignee={cur.Assignee}");
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SaveLocationAsync(Location input, string actor)
    {
        using var db = _factory.CreateDbContext();
        input.LocationName = input.LocationName.ToUpperInvariant();
        var dup = await db.Locations.AnyAsync(l => l.Id != input.Id && l.LocationType == input.LocationType
            && l.ParentId == input.ParentId && l.LocationName == input.LocationName);
        if (dup) return (false, "A location with this name already exists for this type and parent (TIS-345).");

        if (input.Id == 0)
        {
            input.Barcode = NextNumber(db.Locations.Select(l => l.Barcode), "LOC", 6);
            input.CreatedUtc = input.LastUpdatedUtc = DateTime.UtcNow;
            input.CreatedBy = input.LastUpdatedBy = actor;
            db.Locations.Add(input);
            await db.SaveChangesAsync();
            await AuditAsync(db, "Location", input.Id, input.LocationName, "Created", actor);
            return (true, null);
        }
        var cur = await db.Locations.FindAsync(input.Id);
        if (cur == null) return (false, "Location no longer exists.");
        if (cur.RowVersion != input.RowVersion)
            return (false, "Not the latest version — another user changed this location.");
        if (input.ParentId == input.Id) return (false, "A location cannot be its own parent.");
        var old = $"{cur.LocationName}|{cur.LocationType}|{cur.ParentId}";
        db.Entry(cur).CurrentValues.SetValues(input);
        cur.LastUpdatedUtc = DateTime.UtcNow; cur.LastUpdatedBy = actor; cur.RowVersion++;
        await db.SaveChangesAsync();
        await AuditAsync(db, "Location", cur.Id, cur.LocationName, "Updated", actor, "Fields", old,
            $"{cur.LocationName}|{cur.LocationType}|{cur.ParentId}");
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SaveUserAsync(AppUser input, string actor)
    {
        using var db = _factory.CreateDbContext();
        var dup = await db.Users.AnyAsync(u => u.Id != input.Id && u.UserId == input.UserId);
        if (dup) return (false, "User ID already exists.");
        if (input.Id == 0)
        {
            input.CreatedUtc = DateTime.UtcNow;
            db.Users.Add(input);
            await db.SaveChangesAsync();
            await AuditAsync(db, "User", input.Id, input.UserId, "Created", actor);
            return (true, null);
        }
        var cur = await db.Users.FindAsync(input.Id);
        if (cur == null) return (false, "User no longer exists.");
        if (cur.RowVersion != input.RowVersion)
            return (false, "Not the latest version — another user changed this user.");
        // Explicit field copy: never touch PasswordHash/PasswordSalt here
        // (SetValues would null them because edit models don't carry them).
        cur.UserId = input.UserId; cur.DisplayName = input.DisplayName;
        cur.Role = input.Role; cur.Email = input.Email;
        cur.LocationId = input.LocationId; cur.Active = input.Active;
        cur.RowVersion++;
        await db.SaveChangesAsync();
        await AuditAsync(db, "User", cur.Id, cur.UserId, "Updated", actor);
        return (true, null);
    }

    // TIS-2219 Move Items: change Home and/or Assignee, multi-row.
    public async Task<int> MoveItemsAsync(string kind, IEnumerable<int> ids, string? newHome,
        string? newHomeKind, int? newHomeRefId, string? newAssignee, string? newAssigneeKind,
        int? newAssigneeRefId, bool assigneeFollowsHome, string actor)
    {
        if (newHome != null)
        {
            var homeErr = HomeRules.ValidateHome(kind, newHomeKind);
            if (homeErr != null) throw new ArgumentException(homeErr, nameof(newHomeKind));
        }
        if (newAssignee != null)
        {
            var assigneeErr = HomeRules.ValidateHome(kind, newAssigneeKind, "assignee");
            if (assigneeErr != null) throw new ArgumentException(assigneeErr, nameof(newAssigneeKind));
        }
        using var db = _factory.CreateDbContext();
        int n = 0;
        if (kind == "Record")
        {
            var items = await db.Records.Where(r => ids.Contains(r.Id)).ToListAsync();
            foreach (var r in items)
            {
                var o = $"Home={r.Home}, Assignee={r.Assignee}";
                if (newHome != null) { r.Home = newHome; r.HomeKind = newHomeKind; r.HomeRefId = newHomeRefId; }
                if (assigneeFollowsHome && newHome != null)
                {
                    r.Assignee = newHome;
                    r.AssigneeKind = newHomeKind;
                    r.AssigneeRefId = newHomeRefId;
                }
                else if (newAssignee != null) { r.Assignee = newAssignee; r.AssigneeKind = newAssigneeKind; r.AssigneeRefId = newAssigneeRefId; }
                r.LastUpdatedUtc = DateTime.UtcNow; r.LastUpdatedBy = actor; r.RowVersion++;
                await AuditAsync(db, "Record", r.Id, r.RecordNumber, "Moved", actor, "Movement", o, $"Home={r.Home}, Assignee={r.Assignee}");
                n++;
            }
        }
        else if (kind == "Container")
        {
            var items = await db.Containers.Where(c => ids.Contains(c.Id)).ToListAsync();
            foreach (var c in items)
            {
                var o = $"Home={c.Home}, Assignee={c.Assignee}";
                if (newHome != null) { c.Home = newHome; c.HomeKind = newHomeKind; c.HomeRefId = newHomeRefId; }
                if (newAssignee != null) { c.Assignee = newAssignee; c.AssigneeKind = newAssigneeKind; c.AssigneeRefId = newAssigneeRefId; }
                c.LastUpdatedUtc = DateTime.UtcNow; c.LastUpdatedBy = actor; c.RowVersion++;
                await AuditAsync(db, "Container", c.Id, c.ContainerName, "Moved", actor, "Movement", o, $"Home={c.Home}, Assignee={c.Assignee}");
                n++;
            }
        }
        await db.SaveChangesAsync();
        return n;
    }

    // TIS-370 deletion reason workflow: flag + reason, excluded from standard search.
    public async Task<int> DeleteRecordsAsync(IEnumerable<int> ids, string reason, string? mergedInto, string? otherText, string actor)
    {
        // Domain rules enforced here, not just in the dialog:
        // "Merged with case file" requires the target barcode; "Other" requires free text.
        if (reason == "Merged with case file" && string.IsNullOrWhiteSpace(mergedInto))
            throw new ArgumentException("Merged Into barcode is required when the reason is 'Merged with case file'.", nameof(mergedInto));
        if (reason == "Other" && string.IsNullOrWhiteSpace(otherText))
            throw new ArgumentException("A reason is required when the reason is 'Other'.", nameof(otherText));
        using var db = _factory.CreateDbContext();
        var items = await db.Records.Where(r => ids.Contains(r.Id) && !r.Deleted).ToListAsync();
        foreach (var r in items)
        {
            r.Deleted = true;
            r.DeleteReason = reason == "Other" ? $"Other: {otherText}" : reason;
            r.MergedIntoBarcode = mergedInto;
            r.LastUpdatedUtc = DateTime.UtcNow; r.LastUpdatedBy = actor; r.RowVersion++;
            await AuditAsync(db, "Record", r.Id, r.RecordNumber, "Deleted", actor, "DeleteReason", null,
                r.DeleteReason + (mergedInto != null ? $" (merged into {mergedInto})" : ""));
        }
        await db.SaveChangesAsync();
        return items.Count;
    }

    public async Task<int> RestoreRecordsAsync(IEnumerable<int> ids, string actor)
    {
        using var db = _factory.CreateDbContext();
        var items = await db.Records.Where(r => ids.Contains(r.Id) && r.Deleted).ToListAsync();
        foreach (var r in items)
        {
            r.Deleted = false; r.DeleteReason = null; r.MergedIntoBarcode = null;
            r.LastUpdatedUtc = DateTime.UtcNow; r.LastUpdatedBy = actor; r.RowVersion++;
            await AuditAsync(db, "Record", r.Id, r.RecordNumber, "Restored", actor);
        }
        await db.SaveChangesAsync();
        return items.Count;
    }

    // Containers are hard-deleted (the source requirements define a deletion
    // workflow for records only — TIS-370; none exists for containers).
    // Guard: a container with child containers cannot be deleted, otherwise
    // the children's ParentContainerId would dangle. (Records reference
    // containers only via free-text Home, so no FK orphan there.)
    public async Task<int> DeleteContainersAsync(IEnumerable<int> ids, string actor)
    {
        using var db = _factory.CreateDbContext();
        var items = await db.Containers.Where(c => ids.Contains(c.Id)).ToListAsync();
        var withChildren = await db.Containers
            .Where(c => c.ParentContainerId != null && ids.Contains(c.ParentContainerId.Value))
            .Select(c => c.ParentContainerId!.Value).Distinct().ToListAsync();
        if (withChildren.Count > 0)
        {
            var names = await db.Containers.Where(c => withChildren.Contains(c.Id))
                .Select(c => c.ContainerName).ToListAsync();
            throw new InvalidOperationException(
                "Cannot delete container(s) with child containers: " + string.Join(", ", names));
        }
        foreach (var c in items)
            await AuditAsync(db, "Container", c.Id, c.ContainerName, "Deleted", actor);
        db.Containers.RemoveRange(items);
        await db.SaveChangesAsync();
        return items.Count;
    }

    // ---------------- homing rules / compressed children / grid layouts / passwords ----------------

    /// <summary>
    /// A record may only be a child of a Compressed record; rejects missing
    /// parents, non-compressed parents, self-parenting, and cycles.
    /// </summary>
    private static async Task<string?> ValidateRecordParentAsync(PrimDbContext db, RecordItem input)
    {
        if (input.ParentRecordId == null) return null;
        var pid = input.ParentRecordId.Value;
        if (input.Id != 0 && pid == input.Id) return "A record cannot be its own parent.";
        var parent = await db.Records.FindAsync(pid);
        if (parent == null) return "The parent record does not exist.";
        if (parent.RecordType != "Compressed")
            return "Only compressed records can have child records.";
        // Cycle check: walk the ancestor chain.
        var seen = new HashSet<int> { input.Id };
        var cur = parent;
        while (cur != null)
        {
            if (!seen.Add(cur.Id)) return "This would create a circular parent chain.";
            cur = cur.ParentRecordId == null ? null : await db.Records.FindAsync(cur.ParentRecordId.Value);
        }
        return null;
    }

    public async Task<List<RecordItem>> GetChildRecordsAsync(int parentId)
    {
        using var db = _factory.CreateDbContext();
        return await db.Records.Where(r => !r.Deleted && r.ParentRecordId == parentId)
            .OrderBy(r => r.RecordNumber).ToListAsync();
    }

    // ---------------- hierarchy: expandable child rows + breadcrumb paths ----------------

    /// <summary>Root-first ancestor path for each requested object, including the object itself.</summary>
    public async Task<Dictionary<int, List<PathSeg>>> GetAncestorPathsAsync(string kind, IEnumerable<int> ids)
    {
        using var db = _factory.CreateDbContext();
        var result = new Dictionary<int, List<PathSeg>>();
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return result;

        var recMap = await db.Records.Where(r => !r.Deleted)
            .ToDictionaryAsync(r => r.Id, r => (r.RecordNumber, r.HomeKind, r.HomeRefId, r.ParentRecordId));
        var contMap = await db.Containers
            .ToDictionaryAsync(c => c.Id, c => (c.ContainerName, c.ParentContainerId, c.HomeKind, c.HomeRefId, c.LocationId));
        var locMap = await db.Locations
            .ToDictionaryAsync(l => l.Id, l => (l.LocationName, l.ParentId));
        var userMap = await db.Users
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        string? LabelOf(string k, int id) => k switch
        {
            "Record" => recMap.TryGetValue(id, out var r) ? r.RecordNumber : null,
            "Container" => contMap.TryGetValue(id, out var c) ? c.ContainerName : null,
            "Location" => locMap.TryGetValue(id, out var l) ? l.LocationName : null,
            "User" => userMap.TryGetValue(id, out var u) ? u : null,
            _ => null
        };
        (string Kind, int Id)? ParentOf(string k, int id) => k switch
        {
            // A record enclosed in a compressed record paths through that parent;
            // otherwise the path follows the storage home.
            "Record" => recMap.TryGetValue(id, out var r)
                ? r.ParentRecordId != null ? ("Record", r.ParentRecordId.Value)
                  : r.HomeKind != null && r.HomeRefId != null ? (r.HomeKind, r.HomeRefId.Value)
                  : ((string, int)?)null
                : null,
            "Container" => contMap.TryGetValue(id, out var c)
                ? c.ParentContainerId != null ? ("Container", c.ParentContainerId.Value)
                  : c.HomeKind != null && c.HomeRefId != null ? (c.HomeKind, c.HomeRefId.Value)
                  : c.LocationId != null ? ("Location", c.LocationId.Value)
                  : ((string, int)?)null
                : null,
            "Location" => locMap.TryGetValue(id, out var l) && l.ParentId != null
                ? ("Location", l.ParentId.Value) : null,
            _ => null
        };

        foreach (var id in idList)
        {
            var segs = new List<PathSeg>();
            var visited = new HashSet<(string, int)>();
            var cur = (Kind: kind, Id: id);
            for (var depth = 0; depth < 50; depth++)
            {
                if (!visited.Add((cur.Kind, cur.Id))) break;      // cycle guard
                var label = LabelOf(cur.Kind, cur.Id);
                if (label == null) break;                          // dangling reference
                segs.Add(new PathSeg(cur.Kind, cur.Id, label));
                var parent = ParentOf(cur.Kind, cur.Id);
                if (parent == null) break;
                cur = parent.Value;
            }
            segs.Reverse();
            result[id] = segs;
        }
        return result;
    }

    /// <summary>Ids (of the requested kind) that have at least one child item.</summary>
    public async Task<HashSet<int>> GetHasChildrenAsync(string kind, IEnumerable<int> ids)
    {
        using var db = _factory.CreateDbContext();
        var idList = ids.ToList();
        var have = new HashSet<int>();
        if (idList.Count == 0) return have;

        if (kind == "Record")
        {
            var keys = await db.Records
                .Where(r => !r.Deleted && r.ParentRecordId != null && idList.Contains(r.ParentRecordId.Value))
                .Select(r => r.ParentRecordId!.Value).Distinct().ToListAsync();
            foreach (var k in keys) have.Add(k);
            return have;
        }

        // Containers, Locations, Users: children are homed records/containers,
        // plus nested containers (Container) or child locations (Location).
        var recKeys = await db.Records
            .Where(r => !r.Deleted && r.HomeKind == kind && r.HomeRefId != null && idList.Contains(r.HomeRefId.Value))
            .Select(r => r.HomeRefId!.Value).Distinct().ToListAsync();
        var contHomeKeys = await db.Containers
            .Where(c => c.HomeKind == kind && c.HomeRefId != null && idList.Contains(c.HomeRefId.Value))
            .Select(c => c.HomeRefId!.Value).Distinct().ToListAsync();
        foreach (var k in recKeys.Concat(contHomeKeys)) have.Add(k);

        if (kind == "Container")
        {
            var nested = await db.Containers
                .Where(c => c.ParentContainerId != null && idList.Contains(c.ParentContainerId.Value))
                .Select(c => c.ParentContainerId!.Value).Distinct().ToListAsync();
            foreach (var k in nested) have.Add(k);
        }
        else if (kind == "Location")
        {
            var childLocs = await db.Locations
                .Where(l => l.ParentId != null && idList.Contains(l.ParentId.Value))
                .Select(l => l.ParentId!.Value).Distinct().ToListAsync();
            foreach (var k in childLocs) have.Add(k);
        }
        return have;
    }

    /// <summary>Direct children of an object for the expandable grid rows.</summary>
    public async Task<List<ChildItem>> GetChildItemsAsync(string kind, int id)
    {
        using var db = _factory.CreateDbContext();
        var items = new List<ChildItem>();
        if (kind == "Record")
        {
            var kids = await db.Records.Where(r => !r.Deleted && r.ParentRecordId == id)
                .OrderBy(r => r.RecordNumber).ToListAsync();
            var withKids = await GetHasChildrenAsync("Record", kids.Select(r => r.Id));
            items.AddRange(kids.Select(r => new ChildItem("Record", r.Id, r.RecordNumber,
                $"Record · {r.RecordType} · {r.Barcode}", withKids.Contains(r.Id))));
            return items;
        }
        if (kind == "Container")
        {
            var recs = await db.Records.Where(r => !r.Deleted && r.HomeKind == "Container" && r.HomeRefId == id)
                .OrderBy(r => r.RecordNumber).ToListAsync();
            var conts = await db.Containers
                .Where(c => c.ParentContainerId == id || (c.HomeKind == "Container" && c.HomeRefId == id))
                .OrderBy(c => c.ContainerName).ToListAsync();
            var withKidsR = await GetHasChildrenAsync("Record", recs.Select(r => r.Id));
            var withKidsC = await GetHasChildrenAsync("Container", conts.Select(c => c.Id));
            items.AddRange(recs.Select(r => new ChildItem("Record", r.Id, r.RecordNumber,
                $"Record · {r.RecordType} · {r.Barcode}", withKidsR.Contains(r.Id))));
            items.AddRange(conts.Select(c => new ChildItem("Container", c.Id, c.ContainerName,
                $"Container · {c.ContainerType} · {c.Barcode}", withKidsC.Contains(c.Id))));
            return items;
        }
        if (kind == "Location")
        {
            var recs = await db.Records.Where(r => !r.Deleted && r.HomeKind == "Location" && r.HomeRefId == id)
                .OrderBy(r => r.RecordNumber).ToListAsync();
            var conts = await db.Containers.Where(c => c.HomeKind == "Location" && c.HomeRefId == id)
                .OrderBy(c => c.ContainerName).ToListAsync();
            var locs = await db.Locations.Where(l => l.ParentId == id)
                .OrderBy(l => l.LocationName).ToListAsync();
            var withKidsR = await GetHasChildrenAsync("Record", recs.Select(r => r.Id));
            var withKidsC = await GetHasChildrenAsync("Container", conts.Select(c => c.Id));
            var withKidsL = await GetHasChildrenAsync("Location", locs.Select(l => l.Id));
            items.AddRange(recs.Select(r => new ChildItem("Record", r.Id, r.RecordNumber,
                $"Record · {r.RecordType} · {r.Barcode}", withKidsR.Contains(r.Id))));
            items.AddRange(conts.Select(c => new ChildItem("Container", c.Id, c.ContainerName,
                $"Container · {c.ContainerType} · {c.Barcode}", withKidsC.Contains(c.Id))));
            items.AddRange(locs.Select(l => new ChildItem("Location", l.Id, l.LocationName,
                $"Location · {l.LocationType}", withKidsL.Contains(l.Id))));
            return items;
        }
        if (kind == "User")
        {
            var recs = await db.Records.Where(r => !r.Deleted && r.HomeKind == "User" && r.HomeRefId == id)
                .OrderBy(r => r.RecordNumber).ToListAsync();
            var conts = await db.Containers.Where(c => c.HomeKind == "User" && c.HomeRefId == id)
                .OrderBy(c => c.ContainerName).ToListAsync();
            var withKidsR = await GetHasChildrenAsync("Record", recs.Select(r => r.Id));
            var withKidsC = await GetHasChildrenAsync("Container", conts.Select(c => c.Id));
            items.AddRange(recs.Select(r => new ChildItem("Record", r.Id, r.RecordNumber,
                $"Record · {r.RecordType} · {r.Barcode}", withKidsR.Contains(r.Id))));
            items.AddRange(conts.Select(c => new ChildItem("Container", c.Id, c.ContainerName,
                $"Container · {c.ContainerType} · {c.Barcode}", withKidsC.Contains(c.Id))));
            return items;
        }
        return items;
    }

    // ---------------- reads by id (workspace resolution) ----------------
    public async Task<List<RecordItem>> GetRecordsByIdsAsync(IEnumerable<int> ids)
    {
        using var db = _factory.CreateDbContext();
        var list = ids.ToList();
        return await db.Records.Where(r => !r.Deleted && list.Contains(r.Id))
            .OrderBy(r => r.RecordNumber).ToListAsync();
    }

    public async Task<List<Container>> GetContainersByIdsAsync(IEnumerable<int> ids)
    {
        using var db = _factory.CreateDbContext();
        var list = ids.ToList();
        return await db.Containers.Where(c => list.Contains(c.Id))
            .OrderBy(c => c.ContainerName).ToListAsync();
    }

    public async Task<List<Location>> GetLocationsByIdsAsync(IEnumerable<int> ids)
    {
        using var db = _factory.CreateDbContext();
        var list = ids.ToList();
        return await db.Locations.Where(l => list.Contains(l.Id))
            .OrderBy(l => l.LocationName).ToListAsync();
    }

    public async Task<List<AppUser>> GetUsersByIdsAsync(IEnumerable<int> ids)
    {
        using var db = _factory.CreateDbContext();
        var list = ids.ToList();
        return await db.Users.Where(u => list.Contains(u.Id))
            .OrderBy(u => u.DisplayName).ToListAsync();
    }

    /// <summary>Display label for any object (used when focusing an object not on the current page).</summary>
    public async Task<string> GetObjectLabelAsync(string kind, int id)
    {
        using var db = _factory.CreateDbContext();
        return kind switch
        {
            "Record" => await db.Records.Where(r => r.Id == id).Select(r => r.RecordNumber).FirstOrDefaultAsync() ?? "",
            "Container" => await db.Containers.Where(c => c.Id == id).Select(c => c.ContainerName).FirstOrDefaultAsync() ?? "",
            "Location" => await db.Locations.Where(l => l.Id == id).Select(l => l.LocationName).FirstOrDefaultAsync() ?? "",
            "User" => await db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "",
            _ => ""
        };
    }

    // Best-effort assignee name -> id resolution, so assignee links navigate
    // even when the caller only supplied a display name.
    private async Task<int?> ResolveAssigneeRefAsync(PrimDbContext db, string? kind, string? name)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(name)) return null;
        return kind switch
        {
            "User" => await db.Users.Where(u => u.UserId == name || u.DisplayName == name)
                .OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync(),
            "Container" => await db.Containers.Where(c => c.ContainerName == name)
                .OrderBy(c => c.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync(),
            "Location" => await db.Locations.Where(l => l.LocationName == name)
                .OrderBy(l => l.Id).Select(l => (int?)l.Id).FirstOrDefaultAsync(),
            _ => null
        };
    }

    // ---------------- per-user grid column layouts ----------------
    public async Task<List<string>?> GetGridLayoutAsync(string userId, string gridId)
    {
        using var db = _factory.CreateDbContext();
        var row = await db.UserGridLayouts
            .FirstOrDefaultAsync(g => g.UserId == userId && g.GridId == gridId);
        return row == null ? null :
            row.ColumnsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public async Task SaveGridLayoutAsync(string userId, string gridId, List<string> columns)
    {
        using var db = _factory.CreateDbContext();
        var row = await db.UserGridLayouts
            .FirstOrDefaultAsync(g => g.UserId == userId && g.GridId == gridId)
            ?? new UserGridLayout { UserId = userId, GridId = gridId };
        if (row.Id == 0) db.UserGridLayouts.Add(row);
        row.ColumnsCsv = string.Join(",", columns);
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // ---------------- dev passwords (DevPasswordAuthProvider) ----------------
    public async Task<(bool Ok, string? Error)> SetUserPasswordAsync(string userId, string password, string actor)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            return (false, "Password must be at least 4 characters.");
        using var db = _factory.CreateDbContext();
        var u = await db.Users.FirstOrDefaultAsync(x => x.UserId == userId);
        if (u == null) return (false, "User not found.");
        var (hash, salt) = PasswordHasher.Hash(password);
        u.PasswordHash = hash; u.PasswordSalt = salt; u.RowVersion++;
        await db.SaveChangesAsync();
        await AuditAsync(db, "User", u.Id, u.UserId, "Updated", actor, "Password", null, "(changed)");
        return (true, null);
    }

    public async Task<string?> GetUserLocationNameAsync(int? locationId)
    {
        if (locationId == null) return null;
        using var db = _factory.CreateDbContext();
        return (await db.Locations.FindAsync(locationId.Value))?.LocationName;
    }

    // ---------------- workspaces / favorites (TIS-1411/351) ----------------
    public async Task AddToSlotAsync(string owner, string slot, string kind, int id)
    {
        using var db = _factory.CreateDbContext();
        if (!await db.WorkspaceItems.AnyAsync(w => w.OwnerUserId == owner && w.Slot == slot && w.ObjectKind == kind && w.ObjectId == id))
        {
            db.WorkspaceItems.Add(new WorkspaceItem { OwnerUserId = owner, Slot = slot, ObjectKind = kind, ObjectId = id, AddedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
    }
    public async Task<bool> AddToWorkspaceAsync(string owner, string slot, string kind, int id, string label)
    {
        using var db = _factory.CreateDbContext();
        if (await db.WorkspaceItems.AnyAsync(w => w.OwnerUserId == owner && w.Slot == slot && w.ObjectKind == kind && w.ObjectId == id))
            return false;
        db.WorkspaceItems.Add(new WorkspaceItem { OwnerUserId = owner, Slot = slot, ObjectKind = kind, ObjectId = id, Label = label, AddedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return true;
    }
    public async Task RemoveFromSlotAsync(string owner, string slot, string kind, int id)
    {
        using var db = _factory.CreateDbContext();
        var w = await db.WorkspaceItems.FirstOrDefaultAsync(x => x.OwnerUserId == owner && x.Slot == slot && x.ObjectKind == kind && x.ObjectId == id);
        if (w != null) { db.WorkspaceItems.Remove(w); await db.SaveChangesAsync(); }
    }
    public async Task<List<WorkspaceItem>> GetSlotAsync(string owner, string slot)
    {
        using var db = _factory.CreateDbContext();
        return await db.WorkspaceItems.Where(w => w.OwnerUserId == owner && w.Slot == slot).OrderBy(w => w.AddedUtc).ToListAsync();
    }

    // ---------------- saved searches (TIS-358) ----------------
    public async Task<List<SavedSearch>> GetSavedSearchesAsync(string owner)
    {
        using var db = _factory.CreateDbContext();
        return await db.SavedSearches.Where(s => s.OwnerUserId == owner).OrderBy(s => s.Name).ToListAsync();
    }
    public async Task SaveSearchAsync(SavedSearch s)
    {
        using var db = _factory.CreateDbContext();
        s.CreatedUtc = DateTime.UtcNow;
        db.SavedSearches.Add(s);
        await db.SaveChangesAsync();
    }
    public async Task DeleteSavedSearchAsync(int id)
    {
        using var db = _factory.CreateDbContext();
        var s = await db.SavedSearches.FindAsync(id);
        if (s != null) { db.SavedSearches.Remove(s); await db.SaveChangesAsync(); }
    }

    // ---------------- reports (TIS-1288/1289) ----------------
    public async Task<Dictionary<string, int>> GetCountsAsync()
    {
        using var db = _factory.CreateDbContext();
        return new Dictionary<string, int>
        {
            ["Records"] = await db.Records.CountAsync(r => !r.Deleted),
            ["Containers"] = await db.Containers.CountAsync(),
            ["Locations"] = await db.Locations.CountAsync(),
            ["Users"] = await db.Users.CountAsync(),
            ["Deleted records"] = await db.Records.CountAsync(r => r.Deleted),
            ["Audit events"] = await db.AuditEvents.CountAsync(),
        };
    }

    public async Task<List<(string Label, int Count)>> GroupRecordsAsync(Func<RecordItem, string> key)
    {
        using var db = _factory.CreateDbContext();
        var list = await db.Records.Where(r => !r.Deleted).ToListAsync();
        return list.GroupBy(key).OrderByDescending(g => g.Count())
            .Select(g => (Label: string.IsNullOrWhiteSpace(g.Key) ? "(blank)" : g.Key, Count: g.Count()))
            .ToList();
    }

    // Container name preview per TIS-1278 (simplified, documented).
    public async Task<string> PreviewContainerNameAsync(string type, string foCode, string code)
    {
        using var db = _factory.CreateDbContext();
        foCode = foCode.ToUpperInvariant(); code = code.ToUpperInvariant();
        if (type == "Bin")
        {
            var nums = await db.Containers.Where(c => c.ContainerType == "Bin").Select(c => c.FormattedNumber).ToListAsync();
            return NextNumber(nums, "BIN", 6);
        }
        if (type == "Tote")
        {
            var nums = await db.Containers.Where(c => c.ContainerType == "Tote").Select(c => c.FormattedNumber).ToListAsync();
            return NextNumber(nums, "TOTE", 5);
        }
        if (type == "Pallet" || type == "Truck")
        {
            var existing = await db.Containers.Where(c => c.ContainerType == type && c.FieldOffice == foCode && c.ContainerCode == code)
                .Select(c => c.FormattedNumber).ToListAsync();
            var seq = NextNumber(existing, "", 5);
            return $"{foCode} - {code} - {(type == "Pallet" ? "PLT" : "TRUCK")} - {seq}";
        }
        return $"{foCode}-{code}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
    }
}
