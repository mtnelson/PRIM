using System.Collections.Concurrent;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rim.Data;

namespace Rim.Services;

/// <summary>One breadcrumb segment in an object's ancestor path (root first).</summary>
public record PathSeg(string Kind, int Id, string Label);

/// <summary>One child row rendered under an expanded grid row. Cells/Columns carry
/// the child's full grid column set so expanded rows show every column.</summary>
public record ChildItem(string Kind, int Id, string Label, string Detail, bool HasChildren,
    Dictionary<string, string>? Cells = null, List<(string Key, string Label)>? Columns = null);

/// <summary>CRUD + audit + search over the four RIM object types.</summary>
public class RimService
{
    private readonly IDbContextFactory<RimDbContext> _factory;
    private readonly int _maxHotRows;
    private readonly int _maxArchiveRows;
    private readonly string _auditArchiveDir;
    private readonly SearchQueryLog? _queryLog;
    private readonly ILogger<RimService>? _logger;

    // Role strings (must match SeedData.Roles: "Admin", "Records Manager", "Staff").
    private const string RoleAdmin = "Admin";
    private const string RoleRecordsManager = "Records Manager";

    public RimService(IDbContextFactory<RimDbContext> factory, IConfiguration? config = null, string? auditArchiveDir = null, SearchQueryLog? queryLog = null, ILogger<RimService>? logger = null)
    {
        _factory = factory;
        _queryLog = queryLog;
        _logger = logger;
        _maxHotRows = config?.GetValue<int?>("Audit:MaxHotRows") ?? 500_000;
        _maxArchiveRows = config?.GetValue<int?>("Audit:MaxArchiveRows") ?? 5_000_000;
        _auditArchiveDir = auditArchiveDir
            ?? config?.GetValue<string>("Audit:ArchiveDirectory")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "audit-archive");
    }

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
        // Per-item sets are small: hot + archived + file tier are always combined
        // so the retention plan never breaks an item's chain of custody.
        var hot = await db.AuditEvents.Where(a => a.ObjectKind == kind && a.ObjectId == id).ToListAsync();
        var archived = await db.ArchivedAuditEvents.Where(a => a.ObjectKind == kind && a.ObjectId == id)
            .Select(a => new AuditEvent
            {
                Id = -a.Id, ObjectKind = a.ObjectKind, ObjectId = a.ObjectId, ObjectLabel = a.ObjectLabel,
                Action = a.Action, FieldName = a.FieldName, OldValue = a.OldValue, NewValue = a.NewValue,
                Actor = a.Actor, TimestampUtc = a.TimestampUtc
            }).ToListAsync();
        var all = hot.Concat(archived).ToList();
        // File tier: scan export files for this item's rows. Cheap ordinal
        // pre-filter on the ObjectId token (our own serializer writes it as
        // "ObjectId":<id>), then deserialize candidates and verify exactly.
        if (Directory.Exists(_auditArchiveDir))
        {
            var idNeedle = $"\"ObjectId\":{id}";
            foreach (var path in Directory.GetFiles(_auditArchiveDir, "audit-archive-*.jsonl.gz").OrderBy(p => p))
            {
                await using var fs = File.OpenRead(path);
                await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var sr = new StreamReader(gz);
                string? line;
                while ((line = await sr.ReadLineAsync()) != null)
                {
                    if (!line.Contains(idNeedle, StringComparison.Ordinal)) continue;
                    var r = JsonSerializer.Deserialize<AuditEventArchive>(line);
                    if (r == null || r.ObjectKind != kind || r.ObjectId != id) continue;
                    all.Add(new AuditEvent
                    {
                        Id = -r.Id - 1_000_000_000, ObjectKind = r.ObjectKind, ObjectId = r.ObjectId,
                        ObjectLabel = r.ObjectLabel, Action = r.Action, FieldName = r.FieldName,
                        OldValue = r.OldValue, NewValue = r.NewValue, Actor = r.Actor,
                        TimestampUtc = r.TimestampUtc
                    });
                }
            }
        }
        return all.OrderByDescending(a => a.TimestampUtc).ThenByDescending(a => a.Id).ToList();
    }

    // ---------- Infinite-scroll paging (500-row chunks, server-side) ----------
    // Default ordering is keyset on Id: constant-time at any depth, the
    // 20M-safe path. Explicit column sorts use OFFSET: correct, but deep
    // pages of a sorted view are not constant-time.
    private static readonly HashSet<string> RecordSortProps = new()
        { "RecordNumber","RecordType","CaseClassification","FieldOffice","CaseNumber","SubfileId","Volume",
          "SerialStart","SerialEnd","AuxiliaryOffice","Home","Assignee","Barcode","State","Subject","Notes" };
    private static readonly HashSet<string> ContainerSortProps = new()
        { "ContainerName","ContainerType","FieldOffice","ContainerCode","FormattedNumber","Description",
          "Home","Assignee","Barcode" };
    private static readonly HashSet<string> LocationSortProps = new()
        { "LocationName","LocationType","Description","Barcode" };
    private static readonly HashSet<string> UserSortProps = new()
        { "UserId","DisplayName","Role","Email","Barcode","LocationId","Active" };

    public async Task<GridPageResult<RecordItem>> GetRecordsPageAsync(GridPageRequest req)
    {
        using var db = _factory.CreateDbContext();
        return await PageAsync(RecordsQuery(db, req.Filter), req, RecordSortProps, "records");
    }

    public async Task<GridPageResult<Container>> GetContainersPageAsync(GridPageRequest req)
    {
        using var db = _factory.CreateDbContext();
        return await PageAsync(ContainersQuery(db, req.Filter), req, ContainerSortProps, "containers");
    }

    public async Task<GridPageResult<Location>> GetLocationsPageAsync(GridPageRequest req)
    {
        using var db = _factory.CreateDbContext();
        return await PageAsync(LocationsQuery(db, req.Filter), req, LocationSortProps, "locations");
    }

    public async Task<GridPageResult<AppUser>> GetUsersPageAsync(GridPageRequest req, bool includeInactive)
    {
        using var db = _factory.CreateDbContext();
        return await PageAsync(UsersQuery(db, includeInactive), req, UserSortProps, "users");
    }

    // Shared filtered query builders: the page, count, and id-list methods
    // below must apply the exact same predicate so virtualization totals and
    // select-all stay consistent with the rows shown.
    private static IQueryable<RecordItem> RecordsQuery(RimDbContext db, string? filter)
    {
        IQueryable<RecordItem> q = db.Records.AsNoTracking().Where(r => !r.Deleted);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            q = q.Where(r => r.RecordNumber.Contains(f) || r.CaseNumber.Contains(f)
                          || r.Barcode.Contains(f) || (r.Subject != null && r.Subject.Contains(f)));
        }
        return q;
    }

    private static IQueryable<Container> ContainersQuery(RimDbContext db, string? filter)
    {
        IQueryable<Container> q = db.Containers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            q = q.Where(c => c.ContainerName.Contains(f) || c.Barcode.Contains(f)
                          || (c.Description != null && c.Description.Contains(f)));
        }
        return q;
    }

    private static IQueryable<Location> LocationsQuery(RimDbContext db, string? filter)
    {
        IQueryable<Location> q = db.Locations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            q = q.Where(l => l.LocationName.Contains(f) || l.Barcode.Contains(f)
                          || (l.Description != null && l.Description.Contains(f)));
        }
        return q;
    }

    private static IQueryable<AppUser> UsersQuery(RimDbContext db, bool includeInactive)
    {
        IQueryable<AppUser> q = db.Users.AsNoTracking();
        if (!includeInactive) q = q.Where(u => u.Active);
        return q;
    }

    // Total matching rows for the virtualized grids (single indexed COUNT(*)).
    public async Task<int> CountRecordsAsync(string? filter)
    {
        using var db = _factory.CreateDbContext();
        return await RecordsQuery(db, filter).CountAsync();
    }

    public async Task<int> CountContainersAsync(string? filter)
    {
        using var db = _factory.CreateDbContext();
        return await ContainersQuery(db, filter).CountAsync();
    }

    public async Task<int> CountLocationsAsync(string? filter)
    {
        using var db = _factory.CreateDbContext();
        return await LocationsQuery(db, filter).CountAsync();
    }

    public async Task<int> CountUsersAsync(bool includeInactive)
    {
        using var db = _factory.CreateDbContext();
        return await UsersQuery(db, includeInactive).CountAsync();
    }

    // All matching Ids (Id order) for select-all across a virtualized grid.
    public async Task<List<int>> GetAllRecordIdsAsync(string? filter)
    {
        using var db = _factory.CreateDbContext();
        return await RecordsQuery(db, filter).OrderBy(r => r.Id).Select(r => r.Id).ToListAsync();
    }

    public async Task<List<int>> GetAllContainerIdsAsync(string? filter)
    {
        using var db = _factory.CreateDbContext();
        return await ContainersQuery(db, filter).OrderBy(c => c.Id).Select(c => c.Id).ToListAsync();
    }

    public async Task<List<int>> GetAllLocationIdsAsync(string? filter)
    {
        using var db = _factory.CreateDbContext();
        return await LocationsQuery(db, filter).OrderBy(l => l.Id).Select(l => l.Id).ToListAsync();
    }

    public async Task<List<int>> GetAllUserIdsAsync(bool includeInactive)
    {
        using var db = _factory.CreateDbContext();
        return await UsersQuery(db, includeInactive).OrderBy(u => u.Id).Select(u => u.Id).ToListAsync();
    }

    private static async Task<GridPageResult<T>> PageAsync<T>(IQueryable<T> q, GridPageRequest req,
        HashSet<string> sortProps, string queryKey, Action<string>? captureSql = null) where T : class
    {
        var take = Math.Clamp(req.Take, 1, 1000);
        List<T> rows;
        if (string.IsNullOrEmpty(req.SortColumn) || !sortProps.Contains(req.SortColumn))
        {
            if (req.AfterId.HasValue)
                q = req.SortDescending
                    ? q.Where(x => EF.Property<int>(x, "Id") < req.AfterId.Value)
                    : q.Where(x => EF.Property<int>(x, "Id") > req.AfterId.Value);
            else if (req.Skip > 0)
                // Jump fetch (no keyset cursor available): offset is slower on
                // huge tables but correct; the grid prefers keyset whenever the
                // previous chunk is cached.
                q = q.Skip(req.Skip);
            q = req.SortDescending
                ? q.OrderByDescending(x => EF.Property<int>(x, "Id"))
                : q.OrderBy(x => EF.Property<int>(x, "Id"));
            var final = q.Take(take + 1);
            CaptureSql(final, captureSql);
            rows = await final.ToListAsync();
        }
        else
        {
            q = req.SortDescending
                ? q.OrderByDescending(x => EF.Property<object>(x, req.SortColumn!))
                     .ThenBy(x => EF.Property<int>(x, "Id"))
                : q.OrderBy(x => EF.Property<object>(x, req.SortColumn!))
                     .ThenBy(x => EF.Property<int>(x, "Id"));
            // Sorted keyset resume: the grid evicts cached windows, so a deep
            // sorted page arrives as a bare Skip. When this service previously
            // served the adjacent chunk of the same query, its boundary (last
            // row's sort value + Id) is still in the boundary cache below and
            // the chunk is re-fetched with a keyset predicate instead of OFFSET.
            var fingerprint = $"{queryKey}|{req.SortColumn}|{(req.SortDescending ? "D" : "A")}|{req.Filter}";
            var chunkIndex = req.Skip / take;
            Expression<Func<T, bool>>? keyset = null;
            if (req.Skip > 0 && req.Skip % take == 0 &&
                TryGetSortedBoundary(fingerprint, chunkIndex - 1, out var boundary))
                keyset = BuildSortedKeyset<T>(req.SortColumn!, req.SortDescending,
                    boundary.SortValue, boundary.Id);
            var final = (keyset != null ? q.Where(keyset) : q.Skip(req.Skip)).Take(take + 1);
            CaptureSql(final, captureSql);
            rows = await final.ToListAsync();
            if (rows.Count > 0)
                StoreSortedBoundary(fingerprint, chunkIndex, req.SortColumn!, rows[Math.Min(take, rows.Count) - 1]);
        }
        var hasMore = rows.Count > take;
        return new GridPageResult<T> { Rows = rows.Take(take).ToList(), HasMore = hasMore };
    }

    // Per-chunk boundary cache for sorted paging. Keyed by a fingerprint of
    // the query (page kind + sort + direction + filter); each entry maps a
    // chunk index to the last row's (sort value, Id). Entries live outside
    // the grid's evicted window so deep sorted pages can resume via keyset.
    private sealed record SortedBoundary(object? SortValue, int Id);
    private sealed class SortedPageState
    {
        public DateTime LastUsedUtc = DateTime.UtcNow;
        public readonly Dictionary<int, SortedBoundary> Chunks = new();
    }
    private static readonly ConcurrentDictionary<string, SortedPageState> _sortedPageCache = new();

    private static bool TryGetSortedBoundary(string fingerprint, int chunkIndex, out SortedBoundary boundary)
    {
        boundary = new SortedBoundary(null, 0);
        if (!_sortedPageCache.TryGetValue(fingerprint, out var state)) return false;
        lock (state)
        {
            if (DateTime.UtcNow - state.LastUsedUtc > TimeSpan.FromMinutes(15)) return false;
            state.LastUsedUtc = DateTime.UtcNow;
            return state.Chunks.TryGetValue(chunkIndex, out boundary!);
        }
    }

    private static void StoreSortedBoundary(string fingerprint, int chunkIndex, string sortColumn, object row)
    {
        var prop = row.GetType().GetProperty(sortColumn);
        var idProp = row.GetType().GetProperty("Id");
        if (prop == null || idProp?.GetValue(row) is not int id) return;
        var state = _sortedPageCache.GetOrAdd(fingerprint, _ => new SortedPageState());
        lock (state)
        {
            state.LastUsedUtc = DateTime.UtcNow;
            state.Chunks[chunkIndex] = new SortedBoundary(prop.GetValue(row), id);
            if (state.Chunks.Count > 400)
                foreach (var k in state.Chunks.Keys.OrderBy(k => k).Take(state.Chunks.Count - 400).ToList())
                    state.Chunks.Remove(k);
        }
        // Bound the whole cache: drop the stalest fingerprints.
        if (_sortedPageCache.Count > 500)
            foreach (var k in _sortedPageCache.OrderBy(kv => kv.Value.LastUsedUtc)
                         .Take(_sortedPageCache.Count - 500).Select(kv => kv.Key).ToList())
                _sortedPageCache.TryRemove(k, out _);
    }

    // Keyset predicate for an explicitly sorted page: (sortCol, Id) past the
    // boundary row. The tiebreaker is always ascending Id, mirroring the
    // ThenBy(Id) in PageAsync. NULLs sort first in ASC and last in DESC on
    // both SQLite and SQL Server.
    private static Expression<Func<T, bool>>? BuildSortedKeyset<T>(string sortColumn, bool descending,
        object? sortValue, int afterId)
    {
        var prop = typeof(T).GetProperty(sortColumn, BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || typeof(T).GetProperty("Id") == null) return null;
        // Booleans have no ordering comparison in T-SQL (no `>` on bit):
        // fall back to OFFSET for those columns.
        if (prop.PropertyType == typeof(bool) || prop.PropertyType == typeof(bool?)) return null;
        var param = Expression.Parameter(typeof(T), "x");
        var efProp = typeof(EF).GetMethod(nameof(EF.Property), BindingFlags.Public | BindingFlags.Static);
        if (efProp == null) return null;
        var left = Expression.Call(efProp.MakeGenericMethod(prop.PropertyType),
            param, Expression.Constant(sortColumn));
        var idLeft = Expression.Call(efProp.MakeGenericMethod(typeof(int)),
            param, Expression.Constant("Id"));
        var afterIdConst = Expression.Constant(afterId);
        var idPast = Expression.GreaterThan(idLeft, afterIdConst);
        Expression body;
        if (sortValue == null)
        {
            var isNull = Expression.Equal(left, Expression.Constant(null, prop.PropertyType));
            var notNull = Expression.Not(isNull);
            body = descending
                ? Expression.AndAlso(isNull, idPast)                       // NULLs trail in DESC
                : Expression.OrElse(Expression.AndAlso(isNull, idPast), notNull); // NULLs lead in ASC
        }
        else
        {
            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var typed = Expression.Convert(
                Expression.Constant(Convert.ChangeType(sortValue, underlying)), prop.PropertyType);
            var colPast = descending
                ? Expression.LessThan(left, typed)
                : Expression.GreaterThan(left, typed);
            body = Expression.OrElse(colPast,
                Expression.AndAlso(Expression.Equal(left, typed), idPast));
        }
        return Expression.Lambda<Func<T, bool>>(body, param);
    }

    // Renders the final page SQL for the query log. Best-effort: a
    // translation failure must never break the query itself.
    private static void CaptureSql<T>(IQueryable<T> q, Action<string>? captureSql)
    {
        if (captureSql == null) return;
        try { captureSql(q.ToQueryString()); } catch { /* query still runs */ }
    }

    // Bulk-generates `count` realistic test records (deterministic seed so
    // repeated runs produce the same data). Inserts directly without
    // per-record audit events; numbering continues from the current max so
    // sequences stay consistent with records created through the UI.
    public async Task<(bool Ok, string? Error, List<string> Numbers)> SeedTestRecordsAsync(
        int count, string actor, string actorRole)
    {
        if (actorRole != RoleAdmin)
            return (false, "Only administrators can generate test records.", new List<string>());
        using var db = _factory.CreateDbContext();
        var users = await db.Users.AsNoTracking().ToListAsync();
        var locations = await db.Locations.AsNoTracking().ToListAsync();
        if (users.Count == 0 || locations.Count == 0)
            throw new InvalidOperationException("Seed users/locations before generating test records.");

        var rnd = new Random(42);
        var offices = SeedData.FieldOffices.Select(o => o.Code).ToArray();
        var types = SeedData.RecordTypes.Where(t => t != "Compressed").ToArray();
        var states = new[] { "Active", "Active", "Active", "Inactive", "Archived" };
        var subjects = new[] { "Personnel file", "Contract records", "Case exhibits", "Correspondence",
            "Financial vouchers", "Travel orders", "Training files", "Investigative notes" };

        int nextNum = await NextIntAsync(db.Records.Select(r => r.RecordNumber), "R-", 6);
        int nextBar = await NextIntAsync(db.Records.Select(r => r.Barcode), "REC", 6);

        var now = DateTime.UtcNow;
        var list = new List<RecordItem>(count);
        for (int i = 0; i < count; i++)
        {
            var u = users[rnd.Next(users.Count)];
            var l = locations[rnd.Next(locations.Count)];
            bool homeToUser = rnd.Next(2) == 0;
            var fo = offices[rnd.Next(offices.Length)];
            list.Add(new RecordItem
            {
                RecordNumber = "R-" + (nextNum + i).ToString("D6"),
                Barcode = "REC" + (nextBar + i).ToString("D6"),
                RecordType = types[rnd.Next(types.Length)],
                CaseClassification = rnd.Next(1, 999).ToString("D3"),
                FieldOffice = fo,
                CaseNumber = $"{fo}-{rnd.Next(1000, 9999)}",
                Volume = rnd.Next(1, 12).ToString(),
                SerialStart = rnd.Next(1, 500).ToString(),
                SerialEnd = rnd.Next(501, 999).ToString(),
                AuxiliaryOffice = rnd.Next(4) == 0 ? offices[rnd.Next(offices.Length)] : null,
                IsBulky = rnd.Next(10) == 0,
                IsAdmin = rnd.Next(10) == 0,
                SecurityClassification = "Unclassified",
                Home = homeToUser ? u.DisplayName : l.LocationName,
                HomeKind = homeToUser ? "User" : "Location",
                HomeRefId = homeToUser ? u.Id : l.Id,
                Assignee = u.DisplayName,
                AssigneeKind = "User",
                AssigneeRefId = u.Id,
                State = states[rnd.Next(states.Length)],
                Subject = $"[TEST] {subjects[rnd.Next(subjects.Length)]} #{nextNum + i}",
                Notes = "Bulk-generated test record.",
                CreatedUtc = now, CreatedBy = actor,
                LastUpdatedUtc = now, LastUpdatedBy = actor,
            });
        }
        try
        {
            db.Records.AddRange(list);
            await db.SaveChangesAsync();

            // Make some compressed parents and attach children, so expandable
            // child rows have something to show during testing.
            var parentCount = Math.Min(20, list.Count / 10);
            var parents = list.Take(parentCount).ToList();
            foreach (var p in parents) { p.RecordType = "Compressed"; p.CompressedRole = "Parent"; }
            var kids = list.Skip(parentCount).OrderBy(_ => rnd.Next()).Take(Math.Min(200, list.Count - parentCount)).ToList();
            foreach (var k in kids)
            {
                var p = parents[rnd.Next(parents.Count)];
                k.ParentRecordId = p.Id;
                // Filed records have the parent record itself as Home and
                // Assignee (v0.12.0) — a reference, so they move with the parent.
                k.Home = p.RecordNumber; k.HomeKind = "Record"; k.HomeRefId = p.Id;
                k.Assignee = p.RecordNumber; k.AssigneeKind = "Record"; k.AssigneeRefId = p.Id;
            }
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "SeedTestRecordsAsync failed.");
            return (false, "Could not generate test records. Please try again.", new List<string>());
        }
        RunAuditRetentionFireAndForget();
        return (true, null, list.Select(r => r.RecordNumber).ToList());
    }

    // MaxSuffix removed (M11): SeedTestRecordsAsync now uses server-side NextIntAsync.

    // ---------- Advanced search (AND/OR across criteria rows), SQL-side ----------
    // Every criterion compiles to EF.Functions.Like over its mapped column, so
    // filtering AND paging both happen in the database; Blazor only ever sees
    // 500-row chunks. This is the 20M-safe path: the previous AsEnumerable()
    // implementation materialized the whole table in memory.
    // ---------------- advanced search SQL capture ----------------
    // Records a tagged query's SQL + measured duration into the shared
    // SearchQueryLog. No-op when no tag was supplied or no log is attached,
    // so untagged queries pay nothing.
    private void RecordSearchQuery(string? searchTag, string? sql, double durationMs)
    {
        if (_queryLog == null || sql == null) return;
        if (SearchQueryLog.TryParseTag(searchTag, out var runId, out var kind, out var role))
            _queryLog.Record(runId, kind, role, sql, durationMs);
    }

    // Applies the search tag (a SQL comment, always harmless), renders the
    // final SQL via ToQueryString, executes, and records both. The tag is
    // applied even without a log attached so the SQL comment is present
    // whenever a caller asks for it.
    private async Task<TResult> ExecSearchAsync<TItem, TResult>(
        IQueryable<TItem> query, string? searchTag, Func<IQueryable<TItem>, Task<TResult>> exec)
    {
        if (searchTag != null)
            query = query.TagWith(searchTag);
        string? sql = null;
        if (_queryLog != null && searchTag != null)
        {
            try { sql = query.ToQueryString(); } catch { sql = null; }
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { return await exec(query); }
        finally
        {
            sw.Stop();
            RecordSearchQuery(searchTag, sql, sw.Elapsed.TotalMilliseconds);
        }
    }

    private static readonly HashSet<string> AdvSearchRecordFields = new()
        { "RecordNumber","RecordType","CaseClassification","FieldOffice","CaseNumber","SubfileId","Volume",
          "SerialStart","SerialEnd","Barcode","Home","Assignee","Subject","State" };
    // Searchable fields per object type for Advanced Search. Every name must
    // be a string property on its entity; non-string properties (ids, flags)
    // are excluded because the predicate builder coalesces to string.
    private static readonly HashSet<string> AdvSearchContainerFields = new()
        { "ContainerName","ContainerType","FieldOffice","ContainerCode","FormattedNumber",
          "Description","Home","Assignee","Barcode" };
    private static readonly HashSet<string> AdvSearchLocationFields = new()
        { "LocationName","LocationType","Description","Barcode" };
    private static readonly HashSet<string> AdvSearchUserFields = new()
        { "UserId","DisplayName","Role","Email","Barcode" };

    private static IQueryable<RecordItem> AdvancedSearchQuery(RimDbContext db,
        List<(string Field, string Op, string Value)> rows, string logic)
    {
        IQueryable<RecordItem> q = db.Records.AsNoTracking().Where(r => !r.Deleted);
        var crit = rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (crit.Count > 0) q = q.Where(BuildCriteriaPredicate<RecordItem>(crit, logic, AdvSearchRecordFields));
        return q;
    }

    private static IQueryable<Container> AdvancedSearchContainersQuery(RimDbContext db,
        List<(string Field, string Op, string Value)> rows, string logic)
    {
        IQueryable<Container> q = db.Containers.AsNoTracking();
        var crit = rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (crit.Count > 0) q = q.Where(BuildCriteriaPredicate<Container>(crit, logic, AdvSearchContainerFields));
        return q;
    }

    private static IQueryable<Location> AdvancedSearchLocationsQuery(RimDbContext db,
        List<(string Field, string Op, string Value)> rows, string logic)
    {
        IQueryable<Location> q = db.Locations.AsNoTracking();
        var crit = rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (crit.Count > 0) q = q.Where(BuildCriteriaPredicate<Location>(crit, logic, AdvSearchLocationFields));
        return q;
    }

    // Users: searches active AND inactive users (unlike the Users page's
    // default active-only view) — a search should surface deactivated users too.
    private static IQueryable<AppUser> AdvancedSearchUsersQuery(RimDbContext db,
        List<(string Field, string Op, string Value)> rows, string logic)
    {
        IQueryable<AppUser> q = db.Users.AsNoTracking();
        var crit = rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (crit.Count > 0) q = q.Where(BuildCriteriaPredicate<AppUser>(crit, logic, AdvSearchUserFields));
        return q;
    }

    // Fingerprint for the sorted-page boundary cache: the criteria rows are
    // part of the key so two different advanced searches never share boundaries.
    private static string AdvSearchKey(string kind, List<(string Field, string Op, string Value)> rows, string logic) =>
        $"adv-{kind}|{logic}|{string.Join(";", rows.Select(r => $"{r.Field}|{r.Op}|{r.Value}"))}";

    public async Task<GridPageResult<RecordItem>> AdvancedSearchRecordsPageAsync(
        List<(string Field, string Op, string Value)> rows, string logic, GridPageRequest req,
        string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchQuery(db, rows, logic);
        if (searchTag != null) q = q.TagWith(searchTag);
        // SQL-tab capture: render the final composed SQL only when tagged,
        // so untagged grids pay nothing.
        string? sql = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await PageAsync(q, req, RecordSortProps, AdvSearchKey("records", rows, logic),
                searchTag != null ? (Action<string>)(s => { sql = s; }) : null);
        }
        finally
        {
            sw.Stop();
            RecordSearchQuery(searchTag, sql, sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<int> AdvancedSearchRecordsCountAsync(
        List<(string Field, string Op, string Value)> rows, string logic, string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchQuery(db, rows, logic);
        return await ExecSearchAsync(q, searchTag, x => x.CountAsync());
    }

    // All matching Ids (Id order) for select-all on an advanced search grid.
    public async Task<List<int>> AdvancedSearchRecordIdsAsync(
        List<(string Field, string Op, string Value)> rows, string logic, string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchQuery(db, rows, logic).OrderBy(r => r.Id).Select(r => r.Id);
        return await ExecSearchAsync(q, searchTag, x => x.ToListAsync());
    }

    public async Task<GridPageResult<Container>> AdvancedSearchContainersPageAsync(
        List<(string Field, string Op, string Value)> rows, string logic, GridPageRequest req,
        string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchContainersQuery(db, rows, logic);
        if (searchTag != null) q = q.TagWith(searchTag);
        // SQL-tab capture: render the final composed SQL only when tagged,
        // so untagged grids pay nothing.
        string? sql = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await PageAsync(q, req, ContainerSortProps, AdvSearchKey("containers", rows, logic),
                searchTag != null ? (Action<string>)(s => { sql = s; }) : null);
        }
        finally
        {
            sw.Stop();
            RecordSearchQuery(searchTag, sql, sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<int> AdvancedSearchContainersCountAsync(
        List<(string Field, string Op, string Value)> rows, string logic, string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchContainersQuery(db, rows, logic);
        return await ExecSearchAsync(q, searchTag, x => x.CountAsync());
    }

    public async Task<List<int>> AdvancedSearchContainerIdsAsync(
        List<(string Field, string Op, string Value)> rows, string logic, string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchContainersQuery(db, rows, logic).OrderBy(c => c.Id).Select(c => c.Id);
        return await ExecSearchAsync(q, searchTag, x => x.ToListAsync());
    }

    public async Task<GridPageResult<Location>> AdvancedSearchLocationsPageAsync(
        List<(string Field, string Op, string Value)> rows, string logic, GridPageRequest req,
        string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchLocationsQuery(db, rows, logic);
        if (searchTag != null) q = q.TagWith(searchTag);
        // SQL-tab capture: render the final composed SQL only when tagged,
        // so untagged grids pay nothing.
        string? sql = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await PageAsync(q, req, LocationSortProps, AdvSearchKey("locations", rows, logic),
                searchTag != null ? (Action<string>)(s => { sql = s; }) : null);
        }
        finally
        {
            sw.Stop();
            RecordSearchQuery(searchTag, sql, sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<int> AdvancedSearchLocationsCountAsync(
        List<(string Field, string Op, string Value)> rows, string logic, string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchLocationsQuery(db, rows, logic);
        return await ExecSearchAsync(q, searchTag, x => x.CountAsync());
    }

    public async Task<List<int>> AdvancedSearchLocationIdsAsync(
        List<(string Field, string Op, string Value)> rows, string logic, string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchLocationsQuery(db, rows, logic).OrderBy(l => l.Id).Select(l => l.Id);
        return await ExecSearchAsync(q, searchTag, x => x.ToListAsync());
    }

    public async Task<GridPageResult<AppUser>> AdvancedSearchUsersPageAsync(
        List<(string Field, string Op, string Value)> rows, string logic, GridPageRequest req,
        string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchUsersQuery(db, rows, logic);
        if (searchTag != null) q = q.TagWith(searchTag);
        // SQL-tab capture: render the final composed SQL only when tagged,
        // so untagged grids pay nothing.
        string? sql = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await PageAsync(q, req, UserSortProps, AdvSearchKey("users", rows, logic),
                searchTag != null ? (Action<string>)(s => { sql = s; }) : null);
        }
        finally
        {
            sw.Stop();
            RecordSearchQuery(searchTag, sql, sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<int> AdvancedSearchUsersCountAsync(
        List<(string Field, string Op, string Value)> rows, string logic, string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchUsersQuery(db, rows, logic);
        return await ExecSearchAsync(q, searchTag, x => x.CountAsync());
    }

    public async Task<List<int>> AdvancedSearchUserIdsAsync(
        List<(string Field, string Op, string Value)> rows, string logic, string? searchTag = null)
    {
        using var db = _factory.CreateDbContext();
        var q = AdvancedSearchUsersQuery(db, rows, logic).OrderBy(u => u.Id).Select(u => u.Id);
        return await ExecSearchAsync(q, searchTag, x => x.ToListAsync());
    }

    // Bounded compatibility wrapper: walks server-side pages up to maxResults.
    public async Task<List<RecordItem>> AdvancedSearchRecordsAsync(List<(string Field, string Op, string Value)> rows,
        string logic, int maxResults = 500)
    {
        var results = new List<RecordItem>();
        int? after = null; bool more = true;
        while (more && results.Count < maxResults)
        {
            var page = await AdvancedSearchRecordsPageAsync(rows, logic,
                new GridPageRequest { Take = Math.Min(500, maxResults - results.Count), AfterId = after });
            results.AddRange(page.Rows);
            if (page.Rows.Count > 0) after = page.Rows[^1].Id;
            more = page.HasMore;
        }
        return results;
    }

    private static Expression<Func<T, bool>> BuildCriteriaPredicate<T>(
        List<(string Field, string Op, string Value)> rows, string logic, HashSet<string> fields)
    {
        var param = Expression.Parameter(typeof(T), "r");
        Expression? body = null;
        foreach (var (field, op, value) in rows)
        {
            var row = BuildRowPredicate<T>(fields, field, op, value);
            var rewritten = new ParameterReplacer(row.Parameters[0], param).Visit(row.Body);
            body = body is null ? rewritten
                : logic == "OR" ? Expression.OrElse(body, rewritten)
                                : Expression.AndAlso(body, rewritten);
        }
        return Expression.Lambda<Func<T, bool>>(body ?? Expression.Constant(true), param);
    }

    // Every field name must be a string property on T (checked against the
    // per-type field set). "Contains" genuinely contains: the value is
    // wrapped in %…% after escaping, so no asterisks are needed (explicit
    // * and ? wildcards still work inside any operator).
    private static Expression<Func<T, bool>> BuildRowPredicate<T>(
        HashSet<string> fields, string field, string op, string value)
    {
        var param = Expression.Parameter(typeof(T), "r");
        if (!fields.Contains(field))
            return Expression.Lambda<Func<T, bool>>(Expression.Constant(true), param);
        var prop = Expression.Property(param, field);
        var safe = Expression.Coalesce(prop, Expression.Constant(string.Empty));
        string pattern = op switch
        {
            "Contains" => "%" + ToLike(value) + "%",
            "StartsWith" => ToLike(value.TrimEnd('*', '?')) + "%",
            "EndsWith" => "%" + ToLike(value.TrimStart('*', '?')),
            _ => ToLike(value), // "Equals": wildcard match, * and ? supported
        };
        var like = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like),
            new[] { typeof(DbFunctions), typeof(string), typeof(string), typeof(string) })!;
        var call = Expression.Call(like,
            Expression.Property(null, typeof(EF).GetProperty(nameof(EF.Functions))!),
            safe, Expression.Constant(pattern), Expression.Constant(LikeEscape));
        return Expression.Lambda<Func<T, bool>>(call, param);
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _from, _to;
        public ParameterReplacer(ParameterExpression from, ParameterExpression to) => (_from, _to) = (from, to);
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == _from ? _to : base.VisitParameter(node);
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
    // LIKE escaping is provider-consistent: backslash is the escape
    // character and every EF.Functions.Like call passes it explicitly, so
    // both SQLite and SQL Server generate `LIKE ... ESCAPE '\'`. (The old
    // `[%]`/`[_]` bracket escaping is a SQL Server-ism — SQLite treats the
    // brackets literally.)
    private const string LikeEscape = "\\";
    private static string ToLike(string criteria) =>
        criteria.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")
                .Replace("*", "%").Replace("?", "_");

    // Dashboard quick search is bounded: empty criteria return nothing
    // (an unconstrained search must not dump the whole table), and results
    // are capped at QuickSearchMax rows.
    private const int QuickSearchMax = 2000;

    public async Task<List<RecordItem>> SearchRecordsAsync(Dictionary<string, string> filters)
    {
        var active = filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
        if (active.Count == 0) return new List<RecordItem>();
        using var db = _factory.CreateDbContext();
        var q = db.Records.Where(r => !r.Deleted).AsQueryable();
        foreach (var (field, crit) in active)
        {
            var like = ToLike(crit.Trim());
            q = field switch
            {
                "Barcode" => q.Where(r => EF.Functions.Like(r.Barcode, like, LikeEscape)),
                "Case Classification" or "CaseClassification" => q.Where(r => EF.Functions.Like(r.CaseClassification, like, LikeEscape)),
                "Field Office" or "FieldOffice" => q.Where(r => EF.Functions.Like(r.FieldOffice, like, LikeEscape)),
                "Case Number" or "CaseNumber" => q.Where(r => EF.Functions.Like(r.CaseNumber, like, LikeEscape)),
                "Subfile ID" or "SubfileId" => q.Where(r => r.SubfileId != null && EF.Functions.Like(r.SubfileId, like, LikeEscape)),
                "Volume" => q.Where(r => EF.Functions.Like(r.Volume, like, LikeEscape)),
                "Serial Start" or "SerialStart" => q.Where(r => r.SerialStart != null && EF.Functions.Like(r.SerialStart, like, LikeEscape)),
                "Serial End" or "SerialEnd" => q.Where(r => r.SerialEnd != null && EF.Functions.Like(r.SerialEnd, like, LikeEscape)),
                "Record Number" or "RecordNumber" => q.Where(r => EF.Functions.Like(r.RecordNumber, like, LikeEscape)),
                "Home" => q.Where(r => EF.Functions.Like(r.Home, like, LikeEscape)),
                "Assignee" => q.Where(r => EF.Functions.Like(r.Assignee, like, LikeEscape)),
                _ => q
            };
        }
        return await q.OrderBy(r => r.RecordNumber).Take(QuickSearchMax).ToListAsync();
    }

    public async Task<List<Container>> SearchContainersAsync(Dictionary<string, string> filters)
    {
        var active = filters.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
        if (active.Count == 0) return new List<Container>();
        using var db = _factory.CreateDbContext();
        var q = db.Containers.AsQueryable();
        foreach (var (field, crit) in active)
        {
            var like = ToLike(crit.Trim());
            q = field switch
            {
                "Barcode" => q.Where(c => EF.Functions.Like(c.Barcode, like, LikeEscape)),
                "Container Name" or "ContainerName" => q.Where(c => EF.Functions.Like(c.ContainerName, like, LikeEscape)),
                "Field Office" or "FieldOffice" => q.Where(c => EF.Functions.Like(c.FieldOffice, like, LikeEscape)),
                "Container Code" or "ContainerCode" => q.Where(c => EF.Functions.Like(c.ContainerCode, like, LikeEscape)),
                "Home" => q.Where(c => EF.Functions.Like(c.Home, like, LikeEscape)),
                "Assignee" => q.Where(c => EF.Functions.Like(c.Assignee, like, LikeEscape)),
                _ => q
            };
        }
        return await q.OrderBy(c => c.ContainerName).Take(QuickSearchMax).ToListAsync();
    }

    public async Task<List<Location>> SearchLocationsAsync(string? nameCrit)
    {
        if (string.IsNullOrWhiteSpace(nameCrit)) return new List<Location>();
        using var db = _factory.CreateDbContext();
        var like = ToLike(nameCrit.Trim());
        return await db.Locations
            .Where(l => EF.Functions.Like(l.LocationName, like, LikeEscape))
            .OrderBy(l => l.LocationName).Take(QuickSearchMax).ToListAsync();
    }

    // ---------------- writes ----------------
    // Audit events are staged with AddAudit and persisted by the caller's
    // SaveChangesAsync, so bulk operations (move/delete/restore of many
    // items, multi-field updates) cost one database round-trip instead of
    // one per event. Single-item paths keep using AuditAsync.
    private static void AddAudit(RimDbContext db, string kind, int id, string label,
        string action, string actor, string? field = null, string? oldV = null, string? newV = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            ObjectKind = kind, ObjectId = id, ObjectLabel = label, Action = action,
            FieldName = field, OldValue = oldV, NewValue = newV,
            Actor = actor, TimestampUtc = DateTime.UtcNow
        });
    }

    private async Task AuditAsync(RimDbContext db, string kind, int id, string label,
        string action, string actor, string? field = null, string? oldV = null, string? newV = null)
    {
        AddAudit(db, kind, id, label, action, actor, field, oldV, newV);
        await db.SaveChangesAsync();
    }

    // Server-side next-number: candidates are pulled in descending string
    // order and the numeric max is taken over parseable suffixes only.
    // Lexicographic order equals numeric order for zero-padded fixed-width
    // suffixes, but legacy non-conforming values (e.g. "R-CMP008") can sort
    // above every conforming number and would poison a naive string MAX +
    // TryParse (returning 1 and colliding on every create). Parsing the top
    // candidates in memory ignores those rows; any residual collision is
    // caught by the caller's unique-violation retry.
    private static async Task<int> NextIntAsync(IQueryable<string> numbers, string prefix, int width)
    {
        var candidates = await numbers
            .Where(s => s != null && s.StartsWith(prefix) && s.Length == prefix.Length + width)
            .OrderByDescending(s => s)
            .Take(1000)
            .ToListAsync();
        var max = 0;
        foreach (var s in candidates)
            if (int.TryParse(s.AsSpan(prefix.Length), out var n) && n > max) max = n;
        return max + 1;
    }

    private static async Task<string> NextNumberAsync(IQueryable<string> numbers, string prefix, int width) =>
        prefix + (await NextIntAsync(numbers, prefix, width)).ToString().PadLeft(width, '0');

    // Chunks an IN-list so a query never exceeds SQL Server's 2100-parameter limit.
    private static IEnumerable<T[]> ChunkIds<T>(IEnumerable<T> ids, int size = 1000)
    {
        var chunk = new List<T>(size);
        foreach (var id in ids)
        {
            chunk.Add(id);
            if (chunk.Count == size) { yield return chunk.ToArray(); chunk = new List<T>(size); }
        }
        if (chunk.Count > 0) yield return chunk.ToArray();
    }

    // True when the exception reports a unique-constraint violation —
    // SQLite: "UNIQUE constraint failed", SQL Server: "Violation of UNIQUE
    // KEY constraint". Heuristic on purpose: providers surface this differently.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
        ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true;

    // Assigns numbers via assignNumbers, saves, and on a unique collision
    // (two writers racing NextIntAsync) recomputes the numbers once and
    // retries. Returns null on success, else a user-facing error message.
    private async Task<string?> SaveNewWithNumberRetryAsync(RimDbContext db, Func<Task> assignNumbers)
    {
        await assignNumbers();
        try
        {
            await db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _logger?.LogInformation(ex, "Number collision on create; retrying once with a recomputed number.");
            await assignNumbers();
            try
            {
                await db.SaveChangesAsync();
                return null;
            }
            catch (DbUpdateException ex2)
            {
                _logger?.LogWarning(ex2, "Create failed after number-collision retry.");
                return "Could not save: a duplicate number was detected. Please try again.";
            }
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "Create failed.");
            return "Could not save the new item. Please try again.";
        }
    }

    // AuditAsync that returns a user-facing error instead of throwing, so an
    // audit-write failure surfaces as a save error rather than tearing the circuit.
    private async Task<string?> TryAuditAsync(RimDbContext db, string kind, int id, string label,
        string action, string actor, string? field = null, string? oldV = null, string? newV = null)
    {
        try
        {
            await AuditAsync(db, kind, id, label, action, actor, field, oldV, newV);
            return null;
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "Audit write failed for {Kind} {Id}.", kind, id);
            return "The item was saved, but the audit entry could not be written. Please try again.";
        }
    }

    // Returns (ok, error). Implements TIS-597 optimistic concurrency via RowVersion.
    // actorRole is the caller's role ("Admin", "Records Manager", "Staff"):
    // changing the record type requires Admin or Records Manager.
    public async Task<(bool Ok, string? Error)> SaveRecordAsync(RecordItem input, string actor, string actorRole)
    {
        using var db = _factory.CreateDbContext();
        input.CaseClassification = input.CaseClassification.ToUpperInvariant();
        input.FieldOffice = (input.FieldOffice ?? "").ToUpperInvariant();
        input.CaseNumber = (input.CaseNumber ?? "").ToUpperInvariant();
        input.SubfileId = input.SubfileId?.ToUpperInvariant();
        input.SerialStart = input.SerialStart?.ToUpperInvariant();
        input.SerialEnd = input.SerialEnd?.ToUpperInvariant();

        // Compressed-record children: only Compressed Parents may have child
        // records (tightened v0.12.0 — the role is part of the rule).
        var childErr = await ValidateRecordParentAsync(db, input);
        if (childErr != null) return (false, childErr);

        // v0.12.0 compressed Parent/Child role rules (service-level, not just
        // the dialog): role required for the Compressed type, no nesting, a
        // Child must file under a parent, and a parent with children cannot
        // change to a non-Compressed type.
        var roleErr = await ValidateCompressedRoleAsync(db, input);
        if (roleErr != null) return (false, roleErr);

        if (input.ParentRecordId != null)
        {
            // A record filed under a compressed parent has the parent record
            // itself as its Home and Assignee — a reference, not a copy, so
            // the child moves with the parent automatically (no cascade).
            // (ValidateRecordParentAsync above guarantees the parent exists.)
            var parent = await db.Records.FindAsync(input.ParentRecordId.Value);
            input.Home = parent!.RecordNumber; input.HomeKind = "Record"; input.HomeRefId = parent.Id;
            input.Assignee = parent.RecordNumber; input.AssigneeKind = "Record"; input.AssigneeRefId = parent.Id;
        }
        else
        {
            // Homing rules (service-level): records home only to containers, locations, users.
            var homeErr = HomeRules.ValidateHome("Record", input.HomeKind);
            if (homeErr != null) return (false, homeErr);
            var assigneeErr = HomeRules.ValidateHome("Record", input.AssigneeKind, "Assignee");
            if (assigneeErr != null) return (false, assigneeErr);
            input.AssigneeRefId ??= await ResolveAssigneeRefAsync(db, input.AssigneeKind, input.Assignee);
            input.HomeRefId ??= await ResolveAssigneeRefAsync(db, input.HomeKind, input.Home);
        }

        if (input.Id == 0)
        {
            input.CreatedUtc = input.LastUpdatedUtc = DateTime.UtcNow;
            input.CreatedBy = input.LastUpdatedBy = actor;
            db.Records.Add(input);
            var createErr = await SaveNewWithNumberRetryAsync(db, async () =>
            {
                input.RecordNumber = await NextNumberAsync(db.Records.Select(r => r.RecordNumber), "R-", 6);
                input.Barcode = await NextNumberAsync(db.Records.Select(r => r.Barcode), "REC", 6);
            });
            if (createErr != null) return (false, createErr);
            var auditErr = await TryAuditAsync(db, "Record", input.Id, input.RecordNumber, "Created", actor);
            if (auditErr != null) return (false, auditErr);
            return (true, null);
        }

        var cur = await db.Records.FindAsync(input.Id);
        if (cur == null) return (false, "Record no longer exists.");
        if (cur.RowVersion != input.RowVersion)
            return (false, "Not the latest version — another user changed this record. Your view was refreshed.");

        // Service-level role rule: changing the record type requires the
        // Records Manager or Admin role.
        if (cur.RecordType != input.RecordType
            && actorRole != RoleAdmin && actorRole != RoleRecordsManager)
            return (false, "Changing the record type requires the Records Manager role.");

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
        Chg("CompressedRole", cur.CompressedRole, input.CompressedRole);
        Chg("ParentRecordId", cur.ParentRecordId?.ToString(), input.ParentRecordId?.ToString());
        if (cur.Home != input.Home || cur.Assignee != input.Assignee)
            tracked.Add(("Movement", $"Home={cur.Home}, Assignee={cur.Assignee}", $"Home={input.Home}, Assignee={input.Assignee}"));

        var typeChanged = cur.RecordType != input.RecordType;
        try
        {
            db.Entry(cur).CurrentValues.SetValues(input);
            cur.LastUpdatedUtc = DateTime.UtcNow; cur.LastUpdatedBy = actor; cur.RowVersion++;
            await db.SaveChangesAsync();
            foreach (var (f, o, n) in tracked)
                AddAudit(db, "Record", cur.Id, cur.RecordNumber, typeChanged && f == "RecordType" ? "Type Changed" : "Updated", actor, f, o, n);
            await db.SaveChangesAsync();
            return (true, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "This record was changed by someone else. Please refresh and try again.");
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "SaveRecordAsync update failed for record {Id}.", input.Id);
            return (false, "Could not save the record. Please try again.");
        }
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
            input.CreatedUtc = input.LastUpdatedUtc = DateTime.UtcNow;
            input.CreatedBy = input.LastUpdatedBy = actor;
            db.Containers.Add(input);
            var createErr = await SaveNewWithNumberRetryAsync(db, async () =>
            {
                input.Barcode = await NextNumberAsync(db.Containers.Select(c => c.Barcode), "CON", 6);
            });
            if (createErr != null) return (false, createErr);
            var auditErr = await TryAuditAsync(db, "Container", input.Id, input.ContainerName, "Created", actor);
            if (auditErr != null) return (false, auditErr);
            return (true, null);
        }
        var cur = await db.Containers.FindAsync(input.Id);
        if (cur == null) return (false, "Container no longer exists.");
        if (cur.RowVersion != input.RowVersion)
            return (false, "Not the latest version — another user changed this container.");
        var oldName = cur.ContainerName; var oldHome = cur.Home; var oldAssignee = cur.Assignee;
        try
        {
            db.Entry(cur).CurrentValues.SetValues(input);
            cur.LastUpdatedUtc = DateTime.UtcNow; cur.LastUpdatedBy = actor; cur.RowVersion++;
            await db.SaveChangesAsync();
            await AuditAsync(db, "Container", cur.Id, cur.ContainerName, "Updated", actor, "Fields",
                $"Name={oldName}, Home={oldHome}, Assignee={oldAssignee}",
                $"Name={cur.ContainerName}, Home={cur.Home}, Assignee={cur.Assignee}");
            return (true, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "This container was changed by someone else. Please refresh and try again.");
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "SaveContainerAsync update failed for container {Id}.", input.Id);
            return (false, "Could not save the container. Please try again.");
        }
    }

    public async Task<(bool Ok, string? Error)> SaveLocationAsync(Location input, string actor)
    {
        using var db = _factory.CreateDbContext();
        input.LocationName = input.LocationName.ToUpperInvariant();
        var dup = await db.Locations.AnyAsync(l => l.Id != input.Id && l.LocationType == input.LocationType
            && l.ParentId == input.ParentId && l.LocationName == input.LocationName);
        if (dup) return (false, "A location with this name already exists for this type and parent.");

        if (input.Id == 0)
        {
            input.CreatedUtc = input.LastUpdatedUtc = DateTime.UtcNow;
            input.CreatedBy = input.LastUpdatedBy = actor;
            db.Locations.Add(input);
            var createErr = await SaveNewWithNumberRetryAsync(db, async () =>
            {
                input.Barcode = await NextNumberAsync(db.Locations.Select(l => l.Barcode), "LOC", 6);
            });
            if (createErr != null) return (false, createErr);
            var auditErr = await TryAuditAsync(db, "Location", input.Id, input.LocationName, "Created", actor);
            if (auditErr != null) return (false, auditErr);
            return (true, null);
        }
        var cur = await db.Locations.FindAsync(input.Id);
        if (cur == null) return (false, "Location no longer exists.");
        if (cur.RowVersion != input.RowVersion)
            return (false, "Not the latest version — another user changed this location.");
        if (input.ParentId == input.Id) return (false, "A location cannot be its own parent.");
        var old = $"{cur.LocationName}|{cur.LocationType}|{cur.ParentId}";
        try
        {
            db.Entry(cur).CurrentValues.SetValues(input);
            cur.LastUpdatedUtc = DateTime.UtcNow; cur.LastUpdatedBy = actor; cur.RowVersion++;
            await db.SaveChangesAsync();
            await AuditAsync(db, "Location", cur.Id, cur.LocationName, "Updated", actor, "Fields", old,
                $"{cur.LocationName}|{cur.LocationType}|{cur.ParentId}");
            return (true, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "This location was changed by someone else. Please refresh and try again.");
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "SaveLocationAsync update failed for location {Id}.", input.Id);
            return (false, "Could not save the location. Please try again.");
        }
    }

    // actorRole is the caller's role ("Admin", "Records Manager", "Staff"):
    // managing users requires the Admin role.
    public async Task<(bool Ok, string? Error)> SaveUserAsync(AppUser input, string actor, string actorRole)
    {
        if (actorRole != RoleAdmin)
            return (false, "Only administrators can manage users.");
        using var db = _factory.CreateDbContext();
        var dup = await db.Users.AnyAsync(u => u.Id != input.Id && u.UserId == input.UserId);
        if (dup) return (false, "User ID already exists.");
        if (input.Id == 0)
        {
            input.CreatedUtc = DateTime.UtcNow;
            db.Users.Add(input);
            var createErr = await SaveNewWithNumberRetryAsync(db, async () =>
            {
                input.Barcode = await NextNumberAsync(db.Users.Select(u => u.Barcode), "USR", 6); // v0.12.0: system-assigned
            });
            if (createErr != null) return (false, createErr);
            var auditErr = await TryAuditAsync(db, "User", input.Id, input.UserId, "Created", actor);
            if (auditErr != null) return (false, auditErr);
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
        try
        {
            await db.SaveChangesAsync();
            await AuditAsync(db, "User", cur.Id, cur.UserId, "Updated", actor);
            return (true, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (false, "This user was changed by someone else. Please refresh and try again.");
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "SaveUserAsync update failed for user {Id}.", input.Id);
            return (false, "Could not save the user. Please try again.");
        }
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
            // v0.12.0: records filed under a compressed parent have the
            // parent as their Home and Assignee — moving them here would
            // silently break that invariant, so refuse with a clear message.
            var parented = items.Where(r => r.ParentRecordId != null).ToList();
            if (parented.Count > 0)
                throw new InvalidOperationException(
                    "Records filed under a compressed parent cannot be moved in bulk: " +
                    string.Join(", ", parented.Select(r => r.RecordNumber)) +
                    ". Edit the record to change its parent.");
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
                AddAudit(db, "Record", r.Id, r.RecordNumber, "Moved", actor, "Movement", o, $"Home={r.Home}, Assignee={r.Assignee}");
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
                AddAudit(db, "Container", c.Id, c.ContainerName, "Moved", actor, "Movement", o, $"Home={c.Home}, Assignee={c.Assignee}");
                n++;
            }
        }
        await db.SaveChangesAsync();
        RunAuditRetentionFireAndForget();
        return n;
    }

    // TIS-370 deletion reason workflow: flag + reason, excluded from standard search.
    public async Task<(bool Ok, string? Error, int Count)> DeleteRecordsAsync(IEnumerable<int> ids, string reason, string? mergedInto, string? otherText, string actor)
    {
        // Domain rules enforced here, not just in the dialog:
        // "Merged with case file" requires the target barcode; "Other" requires free text.
        if (reason == "Merged with case file" && string.IsNullOrWhiteSpace(mergedInto))
            return (false, "Merged Into barcode is required when the reason is 'Merged with case file'.", 0);
        if (reason == "Other" && string.IsNullOrWhiteSpace(otherText))
            return (false, "A reason is required when the reason is 'Other'.", 0);
        using var db = _factory.CreateDbContext();
        var items = await db.Records.Where(r => ids.Contains(r.Id) && !r.Deleted).ToListAsync();
        // M4: refuse soft-deleting a compressed Parent that has live
        // (non-deleted) children — mirrors the type-change block in
        // ValidateCompressedRoleAsync.
        var parentIds = items.Where(r => r.RecordType == "Compressed" && r.CompressedRole == "Parent")
            .Select(r => r.Id).ToList();
        if (parentIds.Count > 0)
        {
            var withLiveKids = await db.Records
                .Where(r => !r.Deleted && r.ParentRecordId != null && parentIds.Contains(r.ParentRecordId.Value))
                .Select(r => r.ParentRecordId!.Value).Distinct().ToListAsync();
            if (withLiveKids.Count > 0)
                return (false, "Cannot delete a compressed parent that has live children. " +
                               "Move the children out or delete them first.", 0);
        }
        try
        {
            foreach (var r in items)
            {
                r.Deleted = true;
                r.DeleteReason = reason == "Other" ? $"Other: {otherText}" : reason;
                r.MergedIntoBarcode = mergedInto;
                r.LastUpdatedUtc = DateTime.UtcNow; r.LastUpdatedBy = actor; r.RowVersion++;
                AddAudit(db, "Record", r.Id, r.RecordNumber, "Deleted", actor, "DeleteReason", null,
                    r.DeleteReason + (mergedInto != null ? $" (merged into {mergedInto})" : ""));
            }
            await db.SaveChangesAsync();
            RunAuditRetentionFireAndForget();
            return (true, null, items.Count);
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "DeleteRecordsAsync failed.");
            return (false, "Could not delete the records. Please try again.", 0);
        }
    }

    public async Task<(bool Ok, string? Error, int Count)> RestoreRecordsAsync(IEnumerable<int> ids, string actor)
    {
        using var db = _factory.CreateDbContext();
        var items = await db.Records.Where(r => ids.Contains(r.Id) && r.Deleted).ToListAsync();
        try
        {
            foreach (var r in items)
            {
                r.Deleted = false; r.DeleteReason = null; r.MergedIntoBarcode = null;
                r.LastUpdatedUtc = DateTime.UtcNow; r.LastUpdatedBy = actor; r.RowVersion++;
                AddAudit(db, "Record", r.Id, r.RecordNumber, "Restored", actor);
            }
            await db.SaveChangesAsync();
            RunAuditRetentionFireAndForget();
            return (true, null, items.Count);
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "RestoreRecordsAsync failed.");
            return (false, "Could not restore the records. Please try again.", 0);
        }
    }

    // Containers are hard-deleted (the source requirements define a deletion
    // workflow for records only — TIS-370; none exists for containers).
    // Guard: a container with child containers cannot be deleted, otherwise
    // the children's ParentContainerId would dangle. (Records reference
    // containers only via free-text Home, so no FK orphan there.)
    // M2: the guard also covers records/containers homed TO the container
    // (HomeKind='Container' + HomeRefId) — those would dangle the same way.
    public async Task<(bool Ok, string? Error, int Count)> DeleteContainersAsync(IEnumerable<int> ids, string actor)
    {
        using var db = _factory.CreateDbContext();
        var idList = ids.ToList();
        var items = await db.Containers.Where(c => idList.Contains(c.Id)).ToListAsync();
        var withChildren = await db.Containers
            .Where(c => c.ParentContainerId != null && idList.Contains(c.ParentContainerId.Value))
            .Select(c => c.ParentContainerId!.Value).Distinct().ToListAsync();
        if (withChildren.Count > 0)
        {
            var names = await db.Containers.Where(c => withChildren.Contains(c.Id))
                .Select(c => c.ContainerName).ToListAsync();
            return (false, "Cannot delete container(s) with child containers: " + string.Join(", ", names), 0);
        }
        var homed = (await db.Records
                .Where(r => !r.Deleted && r.HomeKind == "Container" && r.HomeRefId != null
                    && idList.Contains(r.HomeRefId.Value))
                .Select(r => r.HomeRefId!.Value).Distinct().ToListAsync())
            .Concat(await db.Containers
                .Where(c => c.HomeKind == "Container" && c.HomeRefId != null
                    && idList.Contains(c.HomeRefId.Value))
                .Select(c => c.HomeRefId!.Value).Distinct().ToListAsync())
            .Distinct().ToList();
        if (homed.Count > 0)
        {
            var names = await db.Containers.Where(c => homed.Contains(c.Id))
                .Select(c => c.ContainerName).ToListAsync();
            return (false, "Cannot delete container(s) with homed records or containers: " +
                           string.Join(", ", names) + ". Move them out first.", 0);
        }
        try
        {
            foreach (var c in items)
                AddAudit(db, "Container", c.Id, c.ContainerName, "Deleted", actor);
            db.Containers.RemoveRange(items);
            await db.SaveChangesAsync();
            RunAuditRetentionFireAndForget();
            return (true, null, items.Count);
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "DeleteContainersAsync failed.");
            return (false, "Could not delete the containers. Please try again.", 0);
        }
    }

    // ---------------- homing rules / compressed children / grid layouts / passwords ----------------

    /// <summary>
    /// A record may only be filed under a Compressed Parent (v0.12.0: the
    /// role is part of the rule, not just the type); rejects missing
    /// parents, non-parent targets, self-parenting, and cycles.
    /// </summary>
    private static async Task<string?> ValidateRecordParentAsync(RimDbContext db, RecordItem input)
    {
        if (input.ParentRecordId == null) return null;
        var pid = input.ParentRecordId.Value;
        if (input.Id != 0 && pid == input.Id) return "A record cannot be its own parent.";
        var parent = await db.Records.FindAsync(pid);
        if (parent == null) return "The parent record does not exist.";
        if (parent.Deleted) return "Cannot file under a deleted record."; // M3
        if (parent.RecordType != "Compressed" || parent.CompressedRole != "Parent")
            return "Records can only be filed under a compressed parent.";
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

    /// <summary>
    /// v0.12.0 compressed Parent/Child rules, enforced in the service (not
    /// just the dialog): the role is required for the Compressed type; a
    /// Child must file under a parent; a Parent cannot itself be filed under
    /// anything (no nesting); a Child cannot have children of its own; and a
    /// parent that has children cannot change to a non-Compressed type.
    /// </summary>
    private static async Task<string?> ValidateCompressedRoleAsync(RimDbContext db, RecordItem input)
    {
        if (input.RecordType == "Compressed"
            && input.CompressedRole != "Parent" && input.CompressedRole != "Child")
            return "Select whether this compressed record is a Parent or a Child.";
        // The stored row, so transition-only rules below don't fire on every
        // edit of a non-Compressed record.
        var cur = input.Id != 0
            ? await db.Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == input.Id)
            : null;
        var leavingCompressed = cur != null && cur.RecordType == "Compressed"
            && input.RecordType != "Compressed";
        if (leavingCompressed)
        {
            // M5: leaving the Compressed type unfiles the record — the role is
            // cleared AND so is the parent link (a dangling ParentRecordId on a
            // non-Compressed record would silently keep the parent's Home/
            // Assignee override). The caller must then supply a valid home
            // (records home to containers, locations, or users); otherwise the
            // homing-rule check in SaveRecordAsync rejects the save.
            // Any other record type may keep (or gain) a ParentRecordId —
            // filing under a compressed parent keeps its type.
            input.CompressedRole = null;
            input.ParentRecordId = null;
        }
        else if (input.RecordType != "Compressed")
        {
            // Not a transition: a non-Compressed record simply carries no role.
            input.CompressedRole = null;
        }
        if (input.CompressedRole == "Child" && input.ParentRecordId == null)
            return "A compressed child must select a compressed parent.";
        if (input.CompressedRole == "Parent" && input.ParentRecordId != null)
            return "A compressed parent cannot be placed inside another record.";
        if (input.Id != 0)
        {
            var hasChildren = await db.Records
                .AnyAsync(r => !r.Deleted && r.ParentRecordId == input.Id);
            if (input.CompressedRole == "Child" && hasChildren)
                return "A compressed child cannot have children of its own.";
            // A parent with children cannot change to a non-Compressed type.
            if (leavingCompressed && hasChildren)
                return "Cannot change the type of a compressed parent that has children. " +
                       "Move the children out first.";
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

    /// <summary>Root-first ancestor path for each requested object, including the object itself.
    /// Batched frontier walk: each round issues one WHERE Id IN (...) query per
    /// object kind that still has unresolved ancestors. Whole tables are never
    /// materialized, so this stays cheap at 20M rows.</summary>
    public async Task<Dictionary<int, List<PathSeg>>> GetAncestorPathsAsync(string kind, IEnumerable<int> ids)
    {
        using var db = _factory.CreateDbContext();
        var result = new Dictionary<int, List<PathSeg>>();
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return result;
        if (kind is not ("Record" or "Container" or "Location" or "User"))
        {
            foreach (var id in idList) result[id] = new List<PathSeg>();
            return result;
        }

        // id -> (label, parentKind, parentId) for every ancestor discovered.
        var recNodes = new Dictionary<int, (string Label, string? PKind, int? PId)>();
        var contNodes = new Dictionary<int, (string Label, string? PKind, int? PId)>();
        var locNodes = new Dictionary<int, (string Label, int? PId)>();
        var userNodes = new Dictionary<int, string>();

        var frontier = new Dictionary<string, HashSet<int>>
        {
            ["Record"] = new(), ["Container"] = new(), ["Location"] = new(), ["User"] = new(),
        };
        frontier[kind].UnionWith(idList);
        var visited = new HashSet<(string, int)>();

        for (var depth = 0; depth < 50; depth++) // depth cap + visited set = cycle protection
        {
            var next = new Dictionary<string, HashSet<int>>
            {
                ["Record"] = new(), ["Container"] = new(), ["Location"] = new(), ["User"] = new(),
            };
            var progressed = false;

            foreach (var chunk in ChunkIds(frontier["Record"].Where(id => visited.Add(("Record", id))).ToList()))
            {
                progressed = true;
                var rows = await db.Records.AsNoTracking()
                    .Where(r => !r.Deleted && chunk.Contains(r.Id))
                    .Select(r => new { r.Id, r.RecordNumber, r.HomeKind, r.HomeRefId, r.ParentRecordId })
                    .ToListAsync();
                foreach (var r in rows)
                {
                    // A record enclosed in a compressed record paths through that
                    // parent; otherwise the path follows the storage home.
                    string? pk = null; int? pid = null;
                    if (r.ParentRecordId != null) { pk = "Record"; pid = r.ParentRecordId; }
                    else if (r.HomeKind != null && r.HomeRefId != null) { pk = r.HomeKind; pid = r.HomeRefId; }
                    recNodes[r.Id] = (r.RecordNumber, pk, pid);
                    if (pk != null && next.ContainsKey(pk)) next[pk].Add(pid!.Value);
                }
            }
            foreach (var chunk in ChunkIds(frontier["Container"].Where(id => visited.Add(("Container", id))).ToList()))
            {
                progressed = true;
                var rows = await db.Containers.AsNoTracking().Where(c => chunk.Contains(c.Id))
                    .Select(c => new { c.Id, c.ContainerName, c.ParentContainerId, c.HomeKind, c.HomeRefId, c.LocationId })
                    .ToListAsync();
                foreach (var c in rows)
                {
                    string? pk = null; int? pid = null;
                    if (c.ParentContainerId != null) { pk = "Container"; pid = c.ParentContainerId; }
                    else if (c.HomeKind != null && c.HomeRefId != null) { pk = c.HomeKind; pid = c.HomeRefId; }
                    else if (c.LocationId != null) { pk = "Location"; pid = c.LocationId; }
                    contNodes[c.Id] = (c.ContainerName, pk, pid);
                    if (pk != null && next.ContainsKey(pk)) next[pk].Add(pid!.Value);
                }
            }
            foreach (var chunk in ChunkIds(frontier["Location"].Where(id => visited.Add(("Location", id))).ToList()))
            {
                progressed = true;
                var rows = await db.Locations.AsNoTracking().Where(l => chunk.Contains(l.Id))
                    .Select(l => new { l.Id, l.LocationName, l.ParentId })
                    .ToListAsync();
                foreach (var l in rows)
                {
                    locNodes[l.Id] = (l.LocationName, l.ParentId);
                    if (l.ParentId != null) next["Location"].Add(l.ParentId.Value);
                }
            }
            foreach (var chunk in ChunkIds(frontier["User"].Where(id => visited.Add(("User", id))).ToList()))
            {
                progressed = true;
                var rows = await db.Users.AsNoTracking().Where(u => chunk.Contains(u.Id))
                    .Select(u => new { u.Id, u.DisplayName })
                    .ToListAsync();
                foreach (var u in rows) userNodes[u.Id] = u.DisplayName;
            }

            if (!progressed) break;
            frontier = next;
        }

        (string Label, string? PKind, int? PId)? NodeOf(string k, int id) => k switch
        {
            "Record" => recNodes.TryGetValue(id, out var r) ? (r.Label, r.PKind, r.PId) : null,
            "Container" => contNodes.TryGetValue(id, out var c) ? (c.Label, c.PKind, c.PId) : null,
            "Location" => locNodes.TryGetValue(id, out var l) ? (l.Label, "Location", l.PId) : null,
            "User" => userNodes.TryGetValue(id, out var u) ? (u, (string?)null, (int?)null) : null,
            _ => null
        };

        foreach (var id in idList)
        {
            var segs = new List<PathSeg>();
            var seenPath = new HashSet<(string, int)>();
            var curK = kind; var curId = id;
            for (var depth = 0; depth < 50; depth++)
            {
                if (!seenPath.Add((curK, curId))) break;   // cycle guard
                var node = NodeOf(curK, curId);
                if (node == null) break;                    // dangling reference
                segs.Add(new PathSeg(curK, curId, node.Value.Label));
                if (node.Value.PKind == null || node.Value.PId == null) break;
                curK = node.Value.PKind; curId = node.Value.PId.Value;
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
                $"Record · {r.RecordType} · {r.Barcode}", withKids.Contains(r.Id), GridColumns.RecordRow(r), GridColumns.RecordColumns())));
            await FillChildLabelCellsAsync(items);
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
                $"Record · {r.RecordType} · {r.Barcode}", withKidsR.Contains(r.Id), GridColumns.RecordRow(r), GridColumns.RecordColumns())));
            items.AddRange(conts.Select(c => new ChildItem("Container", c.Id, c.ContainerName,
                $"Container · {c.ContainerType} · {c.Barcode}", withKidsC.Contains(c.Id), GridColumns.ContainerRow(c), GridColumns.ContainerColumns())));
            await FillChildLabelCellsAsync(items);
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
                $"Record · {r.RecordType} · {r.Barcode}", withKidsR.Contains(r.Id), GridColumns.RecordRow(r), GridColumns.RecordColumns())));
            items.AddRange(conts.Select(c => new ChildItem("Container", c.Id, c.ContainerName,
                $"Container · {c.ContainerType} · {c.Barcode}", withKidsC.Contains(c.Id), GridColumns.ContainerRow(c), GridColumns.ContainerColumns())));
            items.AddRange(locs.Select(l => new ChildItem("Location", l.Id, l.LocationName,
                $"Location · {l.LocationType}", withKidsL.Contains(l.Id), GridColumns.LocationRow(l), GridColumns.LocationColumns())));
            await FillChildLabelCellsAsync(items);
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
                $"Record · {r.RecordType} · {r.Barcode}", withKidsR.Contains(r.Id), GridColumns.RecordRow(r), GridColumns.RecordColumns())));
            items.AddRange(conts.Select(c => new ChildItem("Container", c.Id, c.ContainerName,
                $"Container · {c.ContainerType} · {c.Barcode}", withKidsC.Contains(c.Id), GridColumns.ContainerRow(c), GridColumns.ContainerColumns())));
            await FillChildLabelCellsAsync(items);
            return items;
        }
        return items;
    }

    
    // Fills the "Labels" cell of each child row (children already carry the
    // full column set via Cells/Columns; labels need one extra indexed query).
    private async Task FillChildLabelCellsAsync(List<ChildItem> items)
    {
        foreach (var g in items.GroupBy(k => k.Kind))
        {
            var pairs = await GetObjectLabelPairsAsync(g.Key, g.Select(k => k.Id));
            foreach (var k in g)
                if (k.Cells != null)
                    k.Cells["Labels"] = pairs.TryGetValue(k.Id, out var lp)
                        ? string.Join(", ", lp.Select(x => x.Name)) : "";
        }
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
    private async Task<int?> ResolveAssigneeRefAsync(RimDbContext db, string? kind, string? name)
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
    // actorRole is the caller's role ("Admin", "Records Manager", "Staff"):
    // resetting passwords requires the Admin role.
    public async Task<(bool Ok, string? Error)> SetUserPasswordAsync(string userId, string password, string actor, string actorRole)
    {
        if (actorRole != RoleAdmin)
            return (false, "Only administrators can reset passwords.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return (false, "Password must be at least 8 characters.");
        using var db = _factory.CreateDbContext();
        var u = await db.Users.FirstOrDefaultAsync(x => x.UserId == userId);
        if (u == null) return (false, "User not found.");
        var (hash, salt) = PasswordHasher.Hash(password);
        u.PasswordHash = hash; u.PasswordSalt = salt; u.RowVersion++;
        try
        {
            await db.SaveChangesAsync();
            await AuditAsync(db, "User", u.Id, u.UserId, "Updated", actor, "Password", null, "(changed)");
            return (true, null);
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "SetUserPasswordAsync failed for user {UserId}.", userId);
            return (false, "Could not update the password. Please try again.");
        }
    }

    public async Task<string?> GetUserLocationNameAsync(int? locationId)
    {
        if (locationId == null) return null;
        using var db = _factory.CreateDbContext();
        return (await db.Locations.FindAsync(locationId.Value))?.LocationName;
    }

    // ---------------- theme preference (v0.12.0 dark/light mode) ----------------
    // Per-user stored choice: "Light", "Dark", or null (no stored choice —
    // the UI falls back to the OS prefers-color-scheme setting).
    public async Task<string?> GetThemePreferenceAsync(string userId)
    {
        using var db = _factory.CreateDbContext();
        return await db.Users.Where(u => u.UserId == userId)
            .Select(u => u.ThemePreference).FirstOrDefaultAsync();
    }

    public async Task SetThemePreferenceAsync(string userId, string? theme)
    {
        if (theme != null && theme != "Light" && theme != "Dark")
            throw new ArgumentException("Theme must be 'Light' or 'Dark'.", nameof(theme));
        using var db = _factory.CreateDbContext();
        var u = await db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
        if (u == null) return;
        u.ThemePreference = theme;
        await db.SaveChangesAsync();
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

    // ---------------- barcode scanning tool (v0.12.0) ----------------
    /// <summary>An object resolved from a scanned barcode.</summary>
    public record BarcodeHit(string Kind, int Id, string Name, string Label, string Barcode);

    /// <summary>Per-barcode outcome of a barcode-tool action.</summary>
    public record BarcodeOutcome(string Barcode, bool Ok, string Message);

    /// <summary>Full result of a barcode-tool action, with success/fail counts.</summary>
    public record BarcodeActionResult(List<BarcodeOutcome> Outcomes)
    {
        public int SuccessCount => Outcomes.Count(o => o.Ok);
        public int FailCount => Outcomes.Count(o => !o.Ok);
    }

    private static string NormBarcode(string? s) => (s ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// Resolves scanned barcodes to objects across records, containers,
    /// locations, and users. One indexed query per table per 1000-barcode
    /// chunk (IN clause) — the 20M-safe path, not a per-barcode round-trip.
    /// </summary>
    public async Task<Dictionary<string, BarcodeHit>> ResolveBarcodesAsync(IEnumerable<string> barcodes)
    {
        var list = barcodes.Select(NormBarcode).Where(s => s.Length > 0).Distinct().ToList();
        var map = new Dictionary<string, BarcodeHit>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0) return map;
        using var db = _factory.CreateDbContext();
        foreach (var chunk in ChunkIds(list))
        {
            foreach (var r in await db.Records.Where(x => !x.Deleted && chunk.Contains(x.Barcode)).ToListAsync())
                map[r.Barcode] = new BarcodeHit("Record", r.Id, r.RecordNumber, $"{r.RecordNumber} · {r.Barcode}", r.Barcode);
            foreach (var c in await db.Containers.Where(x => chunk.Contains(x.Barcode)).ToListAsync())
                map[c.Barcode] = new BarcodeHit("Container", c.Id, c.ContainerName, $"{c.ContainerName} · {c.Barcode}", c.Barcode);
            foreach (var l in await db.Locations.Where(x => chunk.Contains(x.Barcode)).ToListAsync())
                map[l.Barcode] = new BarcodeHit("Location", l.Id, l.LocationName, $"{l.LocationName} · {l.Barcode}", l.Barcode);
            foreach (var u in await db.Users.Where(x => chunk.Contains(x.Barcode)).ToListAsync())
                map[u.Barcode] = new BarcodeHit("User", u.Id, u.DisplayName, $"{u.DisplayName} · {u.Barcode}", u.Barcode);
        }
        return map;
    }

    public async Task<BarcodeHit?> ResolveBarcodeAsync(string barcode)
        => (await ResolveBarcodesAsync(new[] { barcode })).Values.FirstOrDefault();

    /// <summary>Adds scanned objects to a workspace slot or Favorites.</summary>
    public async Task<BarcodeActionResult> BarcodeAddToSlotAsync(string owner, string slot,
        IEnumerable<string> barcodes)
    {
        var outcomes = new List<BarcodeOutcome>();
        var hits = await ResolveBarcodesAsync(barcodes);
        using var db = _factory.CreateDbContext();
        foreach (var bc in barcodes.Select(NormBarcode).Where(s => s.Length > 0).Distinct())
        {
            if (!hits.TryGetValue(bc, out var hit))
            { outcomes.Add(new BarcodeOutcome(bc, false, "Barcode not found.")); continue; }
            if (await db.WorkspaceItems.AnyAsync(w => w.OwnerUserId == owner && w.Slot == slot
                    && w.ObjectKind == hit.Kind && w.ObjectId == hit.Id))
                outcomes.Add(new BarcodeOutcome(bc, true, $"Already in {slot}."));
            else
            {
                db.WorkspaceItems.Add(new WorkspaceItem
                {
                    OwnerUserId = owner, Slot = slot, ObjectKind = hit.Kind, ObjectId = hit.Id,
                    Label = hit.Label, AddedUtc = DateTime.UtcNow
                });
                outcomes.Add(new BarcodeOutcome(bc, true, $"Added to {slot}."));
            }
        }
        await db.SaveChangesAsync();
        return new BarcodeActionResult(outcomes);
    }

    /// <summary>Barcode tool: sets Home for scanned records/containers.</summary>
    public async Task<BarcodeActionResult> BarcodeSetHomeAsync(IEnumerable<string> barcodes,
        string destBarcode, string actor)
        => await BarcodeMoveAsync(barcodes, destBarcode, null, actor);

    /// <summary>Barcode tool: sets Assignee for scanned records/containers.</summary>
    public async Task<BarcodeActionResult> BarcodeSetAssigneeAsync(IEnumerable<string> barcodes,
        string userBarcode, string actor)
        => await BarcodeMoveAsync(barcodes, null, userBarcode, actor);

    /// <summary>Barcode tool: sets Home and Assignee in one pass.</summary>
    public async Task<BarcodeActionResult> BarcodeSetHomeAndAssigneeAsync(IEnumerable<string> barcodes,
        string homeBarcode, string userBarcode, string actor)
        => await BarcodeMoveAsync(barcodes, homeBarcode, userBarcode, actor);

    // Batched: one ResolveBarcodesAsync call covers the scanned codes plus the
    // destination/assignee, then one indexed query per table loads all target
    // records/containers (and their compressed parents) into dictionaries —
    // no per-barcode round-trips. IN lists are chunked at 1000 parameters
    // (SQL Server allows 2100 per query).
    private async Task<BarcodeActionResult> BarcodeMoveAsync(IEnumerable<string> barcodes,
        string? homeBarcode, string? userBarcode, string actor)
    {
        var outcomes = new List<BarcodeOutcome>();
        var codes = barcodes.Select(NormBarcode).Where(s => s.Length > 0).Distinct().ToList();
        static BarcodeActionResult FailAll(List<string> all, string msg)
            => new(all.Select(bc => new BarcodeOutcome(bc, false, msg)).ToList());

        var allCodes = codes.ToList();
        if (!string.IsNullOrWhiteSpace(homeBarcode)) allCodes.Add(NormBarcode(homeBarcode));
        if (!string.IsNullOrWhiteSpace(userBarcode)) allCodes.Add(NormBarcode(userBarcode));
        var hits = await ResolveBarcodesAsync(allCodes);

        BarcodeHit? home = null, assignee = null;
        if (homeBarcode != null)
        {
            if (!hits.TryGetValue(NormBarcode(homeBarcode), out home))
                return FailAll(codes, $"Destination barcode '{NormBarcode(homeBarcode)}' not found.");
            if (home.Kind is not ("Location" or "Container" or "User"))
                return FailAll(codes, $"'{home.Label}' is a {home.Kind} — home must be a location, container, or user.");
        }
        if (userBarcode != null)
        {
            if (!hits.TryGetValue(NormBarcode(userBarcode), out assignee))
                return FailAll(codes, $"Assignee barcode '{NormBarcode(userBarcode)}' not found.");
            if (assignee.Kind != "User")
                return FailAll(codes, $"'{assignee.Label}' is a {assignee.Kind} — assignee must be a user.");
        }

        using var db = _factory.CreateDbContext();
        var recIds = codes
            .Select(bc => hits.TryGetValue(bc, out var h) && h.Kind == "Record" ? h.Id : 0)
            .Where(id => id != 0).ToList();
        var contIds = codes
            .Select(bc => hits.TryGetValue(bc, out var h) && h.Kind == "Container" ? h.Id : 0)
            .Where(id => id != 0).ToList();
        var recs = new Dictionary<int, RecordItem>();
        foreach (var chunk in ChunkIds(recIds))
            foreach (var r in await db.Records.Where(r => chunk.Contains(r.Id)).ToListAsync())
                recs[r.Id] = r;
        var conts = new Dictionary<int, Container>();
        foreach (var chunk in ChunkIds(contIds))
            foreach (var c in await db.Containers.Where(c => chunk.Contains(c.Id)).ToListAsync())
                conts[c.Id] = c;
        var parentNums = new Dictionary<int, string>();
        var parentIds = recs.Values
            .Where(r => r.ParentRecordId != null).Select(r => r.ParentRecordId!.Value).Distinct().ToList();
        foreach (var chunk in ChunkIds(parentIds))
            foreach (var p in await db.Records.Where(r => chunk.Contains(r.Id))
                .Select(r => new { r.Id, r.RecordNumber }).ToListAsync())
                parentNums[p.Id] = p.RecordNumber;

        foreach (var bc in codes)
        {
            if (!hits.TryGetValue(bc, out var hit))
            { outcomes.Add(new BarcodeOutcome(bc, false, "Barcode not found.")); continue; }
            if (hit.Kind is not ("Record" or "Container"))
            { outcomes.Add(new BarcodeOutcome(bc, false, $"'{hit.Label}' is a {hit.Kind} — only records and containers can be moved.")); continue; }

            if (hit.Kind == "Record")
            {
                if (!recs.TryGetValue(hit.Id, out var rec) || rec.Deleted)
                { outcomes.Add(new BarcodeOutcome(bc, false, "Record no longer exists.")); continue; }
                if (rec.ParentRecordId != null)
                {
                    parentNums.TryGetValue(rec.ParentRecordId.Value, out var pNum);
                    outcomes.Add(new BarcodeOutcome(bc, false,
                        $"Filed under compressed parent {pNum ?? "?"} — it moves with its parent."));
                    continue;
                }
                var old = $"Home={rec.Home}, Assignee={rec.Assignee}";
                if (home != null) { rec.Home = home.Name; rec.HomeKind = home.Kind; rec.HomeRefId = home.Id; }
                if (assignee != null) { rec.Assignee = assignee.Name; rec.AssigneeKind = "User"; rec.AssigneeRefId = assignee.Id; }
                rec.LastUpdatedUtc = DateTime.UtcNow; rec.LastUpdatedBy = actor; rec.RowVersion++;
                AddAudit(db, "Record", rec.Id, rec.RecordNumber, "Moved", actor, "Movement", old,
                    $"Home={rec.Home}, Assignee={rec.Assignee}");
            }
            else
            {
                if (!conts.TryGetValue(hit.Id, out var cont))
                { outcomes.Add(new BarcodeOutcome(bc, false, "Container no longer exists.")); continue; }
                var old = $"Home={cont.Home}, Assignee={cont.Assignee}";
                if (home != null) { cont.Home = home.Name; cont.HomeKind = home.Kind; cont.HomeRefId = home.Id; }
                if (assignee != null) { cont.Assignee = assignee.Name; cont.AssigneeKind = "User"; cont.AssigneeRefId = assignee.Id; }
                cont.LastUpdatedUtc = DateTime.UtcNow; cont.LastUpdatedBy = actor; cont.RowVersion++;
                AddAudit(db, "Container", cont.Id, cont.ContainerName, "Moved", actor, "Movement", old,
                    $"Home={cont.Home}, Assignee={cont.Assignee}");
            }
            outcomes.Add(new BarcodeOutcome(bc, true,
                $"Moved{(home != null ? $" home → {home.Name}" : "")}{(assignee != null ? $" assignee → {assignee.Name}" : "")}."));
        }
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger?.LogWarning(ex, "BarcodeMoveAsync failed.");
            return FailAll(codes, "Could not save the move (database error). Please try again.");
        }
        RunAuditRetentionFireAndForget();
        return new BarcodeActionResult(outcomes);
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

    // ---------------- search sessions (per-page tabs) ----------------
    public const int MaxOpenSessionsPerPage = 10;

    public async Task<List<SearchSession>> GetOpenSessionsAsync(string owner, string pageKind)
    {
        using var db = _factory.CreateDbContext();
        return await db.SearchSessions
            .Where(s => s.OwnerUserId == owner && s.PageKind == pageKind && s.IsOpen)
            .OrderByDescending(s => s.LastUsedUtc).ToListAsync();
    }

    /// <summary>Opens a new search tab. Refuses when the per-page cap is hit.</summary>
    public async Task<(bool Ok, string? Error, SearchSession? Session)> CreateSessionAsync(SearchSession s)
    {
        using var db = _factory.CreateDbContext();
        var open = await db.SearchSessions.CountAsync(x =>
            x.OwnerUserId == s.OwnerUserId && x.PageKind == s.PageKind && x.IsOpen);
        if (open >= MaxOpenSessionsPerPage)
            return (false, $"Close a tab first — {MaxOpenSessionsPerPage} open tabs per page.", null);
        s.CreatedUtc = s.LastUsedUtc = DateTime.UtcNow;
        s.IsOpen = true;
        db.SearchSessions.Add(s);
        await db.SaveChangesAsync();
        return (true, null, s);
    }

    /// <summary>Upserts a session descriptor; bumps LastUsedUtc. Refuses when the
    /// caller does not own the session.</summary>
    public async Task<(bool Ok, string? Error)> SaveSessionAsync(SearchSession s)
    {
        if (s.Id == 0) { var r = await CreateSessionAsync(s); return (r.Ok, r.Error); }
        using var db = _factory.CreateDbContext();
        var existing = await db.SearchSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == s.Id);
        if (existing == null) return (false, "Session not found.");
        if (existing.OwnerUserId != s.OwnerUserId) return (false, "Not your tab.");
        s.CreatedUtc = existing.CreatedUtc;
        s.LastUsedUtc = DateTime.UtcNow;
        db.SearchSessions.Update(s);
        await db.SaveChangesAsync();
        return (true, null);
    }

    public async Task DeleteSessionAsync(int id, string owner)
    {
        using var db = _factory.CreateDbContext();
        var s = await db.SearchSessions.FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == owner);
        if (s != null) { db.SearchSessions.Remove(s); await db.SaveChangesAsync(); }
    }

    // ---------------- search activity: per-user log of executed searches ----
    public const int MaxSearchActivityPerUser = 200;

    // Records one executed search, then prunes only the capped overflow
    // (oldest first) so the per-user log never grows unboundedly.
    public async Task LogSearchActivityAsync(SearchActivity a)
    {
        using var db = _factory.CreateDbContext();
        db.SearchActivities.Add(a);
        await db.SaveChangesAsync();
        var overflowIds = await db.SearchActivities
            .Where(s => s.UserId == a.UserId)
            .OrderByDescending(s => s.TimestampUtc).ThenByDescending(s => s.Id)
            .Select(s => s.Id)
            .Skip(MaxSearchActivityPerUser)
            .ToListAsync();
        if (overflowIds.Count > 0)
        {
            await db.SearchActivities.Where(s => overflowIds.Contains(s.Id)).ExecuteDeleteAsync();
        }
    }

    // Newest first. Plain projection-free read — the Activity tab renders
    // rows directly with no per-row service calls.
    public async Task<List<SearchActivity>> GetSearchActivityAsync(string userId, int take = 200)
    {
        using var db = _factory.CreateDbContext();
        return await db.SearchActivities.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.TimestampUtc).ThenByDescending(s => s.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync();
    }

    // ---------------- labels: named collections of objects ----------------
    public async Task<List<Label>> GetLabelsAsync()
    {
        using var db = _factory.CreateDbContext();
        return await db.Labels.OrderBy(l => l.Name).ToListAsync();
    }

    public async Task<Label?> GetLabelAsync(int id)
    {
        using var db = _factory.CreateDbContext();
        return await db.Labels.FindAsync(id);
    }

    public async Task<Dictionary<int, int>> GetLabelCountsAsync()
    {
        using var db = _factory.CreateDbContext();
        return await db.ObjectLabels.GroupBy(o => o.LabelId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<Label> GetOrCreateLabelAsync(string name, string actor)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Label name is required.", nameof(name));
        using var db = _factory.CreateDbContext();
        var upper = name.ToUpperInvariant();
        var existing = await db.Labels.FirstOrDefaultAsync(l => l.Name.ToUpper() == upper);
        if (existing != null) return existing;
        var label = new Label { Name = name, CreatedUtc = DateTime.UtcNow, CreatedBy = actor };
        db.Labels.Add(label);
        await db.SaveChangesAsync();
        await AuditAsync(db, "Label", label.Id, label.Name, "Created", actor);
        return label;
    }

    public async Task RenameLabelAsync(int id, string newName, string actor)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) throw new ArgumentException("Label name is required.", nameof(newName));
        using var db = _factory.CreateDbContext();
        var label = await db.Labels.FindAsync(id) ?? throw new InvalidOperationException("Label not found.");
        var upper = newName.ToUpperInvariant();
        if (await db.Labels.AnyAsync(l => l.Id != id && l.Name.ToUpper() == upper))
            throw new InvalidOperationException($"A label named '{newName}' already exists.");
        var old = label.Name;
        label.Name = newName;
        await AuditAsync(db, "Label", label.Id, newName, "Renamed", actor, "Name", old, newName);
    }

    public async Task DeleteLabelAsync(int id, string actor)
    {
        using var db = _factory.CreateDbContext();
        var label = await db.Labels.FindAsync(id);
        if (label == null) return;
        db.ObjectLabels.RemoveRange(db.ObjectLabels.Where(o => o.LabelId == id));
        await AuditAsync(db, "Label", label.Id, label.Name, "Deleted", actor);
        db.Labels.Remove(label);
        await db.SaveChangesAsync();
    }

    // Label chips for grid rows: object id -> ordered (label id, name) pairs.
    public async Task<Dictionary<int, List<(int Id, string Name)>>> GetObjectLabelPairsAsync(
        string kind, IEnumerable<int> ids)
    {
        using var db = _factory.CreateDbContext();
        var idList = ids.ToList();
        var rows = await db.ObjectLabels
            .Where(o => o.ObjectKind == kind && idList.Contains(o.ObjectId))
            .Join(db.Labels, o => o.LabelId, l => l.Id,
                (o, l) => new { o.ObjectId, l.Id, l.Name })
            .OrderBy(x => x.Name).ToListAsync();
        return rows.GroupBy(x => x.ObjectId)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.Id, x.Name)).ToList());
    }

    public async Task<List<string>> GetObjectLabelNamesAsync(string kind, int id)
    {
        var pairs = await GetObjectLabelPairsAsync(kind, new[] { id });
        return pairs.TryGetValue(id, out var list) ? list.Select(x => x.Name).ToList() : new();
    }

    // Full entities for every member of a label, by kind — backs the label
    // detail page's per-kind data grids. Deleted records are excluded.
    public async Task<List<RecordItem>> GetLabelRecordsAsync(int labelId)
    {
        using var db = _factory.CreateDbContext();
        var ids = await db.ObjectLabels.Where(o => o.LabelId == labelId && o.ObjectKind == "Record")
            .Select(o => o.ObjectId).ToListAsync();
        return await db.Records.AsNoTracking().Where(r => !r.Deleted && ids.Contains(r.Id))
            .OrderBy(r => r.RecordNumber).ToListAsync();
    }

    public async Task<List<Container>> GetLabelContainersAsync(int labelId)
    {
        using var db = _factory.CreateDbContext();
        var ids = await db.ObjectLabels.Where(o => o.LabelId == labelId && o.ObjectKind == "Container")
            .Select(o => o.ObjectId).ToListAsync();
        return await db.Containers.AsNoTracking().Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.ContainerName).ToListAsync();
    }

    public async Task<List<Location>> GetLabelLocationsAsync(int labelId)
    {
        using var db = _factory.CreateDbContext();
        var ids = await db.ObjectLabels.Where(o => o.LabelId == labelId && o.ObjectKind == "Location")
            .Select(o => o.ObjectId).ToListAsync();
        return await db.Locations.AsNoTracking().Where(l => ids.Contains(l.Id))
            .OrderBy(l => l.LocationName).ToListAsync();
    }

    public async Task<List<AppUser>> GetLabelUsersAsync(int labelId)
    {
        using var db = _factory.CreateDbContext();
        var ids = await db.ObjectLabels.Where(o => o.LabelId == labelId && o.ObjectKind == "User")
            .Select(o => o.ObjectId).ToListAsync();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .OrderBy(u => u.DisplayName).ToListAsync();
    }

    // Replaces an object's label set; creates unknown names. Writes one audit
    // event naming the added/removed labels.
    public async Task SetObjectLabelsAsync(string kind, int id, IEnumerable<string> names, string actor)
    {
        var wanted = names.Select(n => (n ?? "").Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var db = _factory.CreateDbContext();
        var upper = wanted.Select(w => w.ToUpperInvariant()).ToList();
        var labels = await db.Labels.Where(l => upper.Contains(l.Name.ToUpper())).ToListAsync();
        foreach (var w in wanted.Where(w => !labels.Any(l => l.Name.Equals(w, StringComparison.OrdinalIgnoreCase))))
        {
            var created = new Label { Name = w, CreatedUtc = DateTime.UtcNow, CreatedBy = actor };
            db.Labels.Add(created);
            labels.Add(created);
        }
        await db.SaveChangesAsync();
        var wantedIds = labels.Select(l => l.Id).ToHashSet();
        var current = await db.ObjectLabels
            .Where(o => o.ObjectKind == kind && o.ObjectId == id).ToListAsync();
        var currentIds = current.Select(o => o.LabelId).ToHashSet();
        var toAdd = wantedIds.Except(currentIds).ToList();
        var toRemove = current.Where(o => !wantedIds.Contains(o.LabelId)).ToList();
        foreach (var lid in toAdd)
            db.ObjectLabels.Add(new ObjectLabel { LabelId = lid, ObjectKind = kind, ObjectId = id });
        db.ObjectLabels.RemoveRange(toRemove);
        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            var nameOf = labels.ToDictionary(l => l.Id, l => l.Name);
            var oldV = string.Join(", ", current.Select(o => nameOf.TryGetValue(o.LabelId, out var n) ? n : "?").OrderBy(n => n));
            var newV = string.Join(", ", wanted.OrderBy(n => n));
            var objLabel = await GetObjectLabelAsync(kind, id);
            await AuditAsync(db, kind, id, objLabel, "Updated", actor, "Labels", oldV, newV);
        }
        await db.SaveChangesAsync();
    }

    // Every member of a label: (kind, id, display label), ordered by kind then label.
    public async Task<List<(string Kind, int Id, string Label)>> GetLabelMembersAsync(int labelId)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.ObjectLabels.Where(o => o.LabelId == labelId).ToListAsync();
        var out_ = new List<(string Kind, int Id, string Label)>();
        foreach (var g in rows.GroupBy(r => r.ObjectKind))
        {
            var ids = g.Select(r => r.ObjectId).ToList();
            switch (g.Key)
            {
                case "Record":
                    out_.AddRange(await db.Records.Where(r => ids.Contains(r.Id))
                        .Select(r => new ValueTuple<string, int, string>("Record", r.Id, r.RecordNumber)).ToListAsync());
                    break;
                case "Container":
                    out_.AddRange(await db.Containers.Where(c => ids.Contains(c.Id))
                        .Select(c => new ValueTuple<string, int, string>("Container", c.Id, c.ContainerName)).ToListAsync());
                    break;
                case "Location":
                    out_.AddRange(await db.Locations.Where(l => ids.Contains(l.Id))
                        .Select(l => new ValueTuple<string, int, string>("Location", l.Id, l.LocationName)).ToListAsync());
                    break;
                case "User":
                    out_.AddRange(await db.Users.Where(u => ids.Contains(u.Id))
                        .Select(u => new ValueTuple<string, int, string>("User", u.Id, u.DisplayName)).ToListAsync());
                    break;
            }
        }
        return out_.OrderBy(x => x.Kind).ThenBy(x => x.Label).ToList();
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

    /// <summary>Server-side grouping for the Reports page: GROUP BY is
    /// translated to SQL with counts, so no rows are materialized. Prefer
    /// this over the Func-based GroupRecordsAsync (kept for compatibility).
    /// <paramref name="field"/> must be one of the advanced-search record fields.</summary>
    public async Task<List<(string Label, int Count)>> GroupRecordsByFieldAsync(string field)
    {
        if (!AdvSearchRecordFields.Contains(field)) return new List<(string, int)>();
        using var db = _factory.CreateDbContext();
        var groups = await db.Records.Where(r => !r.Deleted)
            .GroupBy(r => EF.Property<string>(r, field))
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();
        return groups
            .Select(x => (Label: string.IsNullOrWhiteSpace(x.Key) ? "(blank)" : x.Key, Count: x.Count))
            .ToList();
    }

    // Container name preview per TIS-1278 (simplified, documented).
    public async Task<string> PreviewContainerNameAsync(string type, string foCode, string code)
    {
        using var db = _factory.CreateDbContext();
        foCode = foCode.ToUpperInvariant(); code = code.ToUpperInvariant();
        if (type == "Bin")
            return await NextNumberAsync(
                db.Containers.Where(c => c.ContainerType == "Bin").Select(c => c.FormattedNumber), "BIN", 6);
        if (type == "Tote")
            return await NextNumberAsync(
                db.Containers.Where(c => c.ContainerType == "Tote").Select(c => c.FormattedNumber), "TOTE", 5);
        if (type == "Pallet" || type == "Truck")
        {
            var seq = await NextNumberAsync(
                db.Containers.Where(c => c.ContainerType == type && c.FieldOffice == foCode && c.ContainerCode == code)
                    .Select(c => c.FormattedNumber), "", 5);
            return $"{foCode} - {code} - {(type == "Pallet" ? "PLT" : "TRUCK")} - {seq}";
        }
        return $"{foCode}-{code}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
    }

    // ---------------- audit retention (count-based) ----------------
    // Three tiers: hot AuditEvents (capped at MaxHotRows) -> AuditEventArchive
    // table (capped at MaxArchiveRows) -> checksummed .jsonl.gz files on disk.
    // Rows are moved, never deleted, until a verified file copy exists.

    public record AuditStats(long HotRows, long ArchivedRows, int ExportFiles,
        int MaxHotRows, int MaxArchiveRows, string ArchiveDirectory);
    public record AuditExportInfo(string FileName, long Rows, long Bytes, DateTime CreatedUtc, string Sha256);

    public async Task<AuditStats> GetAuditStatsAsync()
    {
        using var db = _factory.CreateDbContext();
        var hot = await db.AuditEvents.LongCountAsync();
        var archived = await db.ArchivedAuditEvents.LongCountAsync();
        var files = Directory.Exists(_auditArchiveDir)
            ? Directory.GetFiles(_auditArchiveDir, "audit-archive-*.jsonl.gz").Length : 0;
        return new AuditStats(hot, archived, files, _maxHotRows, _maxArchiveRows, _auditArchiveDir);
    }

    /// <summary>Moves oldest-first audit rows from the hot table to the archive
    /// table when the hot row cap is exceeded. Admin-only ("archive now").
    /// Returns rows moved (0 = under cap).</summary>
    public async Task<(bool Ok, string? Error, int Moved)> ArchiveAuditIfNeededAsync(
        string actorRole, int? maxHotRows = null)
    {
        if (actorRole != RoleAdmin)
            return (false, "Only administrators can archive audit events.", 0);
        try
        {
            return (true, null, await ArchiveAuditCoreAsync(maxHotRows));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Audit archiving failed.");
            return (false, "Audit archiving failed. Please try again.", 0);
        }
    }

    // Retention core used by the fire-and-forget path below (system-initiated,
    // no actor role involved). Never called directly with user input.
    private async Task<int> ArchiveAuditCoreAsync(int? maxHotRows = null)
    {
        var cap = maxHotRows ?? _maxHotRows;
        using var db = _factory.CreateDbContext();
        var count = await db.AuditEvents.LongCountAsync();
        if (count <= cap) return 0;
        // Ids are insertion-ordered: everything below the keep-from Id is safe
        // to move even if concurrent writers add newer rows mid-run.
        var keepFromId = await db.AuditEvents.OrderBy(a => a.Id).Select(a => a.Id)
            .Skip((int)(count - cap)).FirstAsync();
        var moved = 0;
        var now = DateTime.UtcNow;
        while (true)
        {
            var batch = await db.AuditEvents.Where(a => a.Id < keepFromId)
                .OrderBy(a => a.Id).Take(10_000).ToListAsync();
            if (batch.Count == 0) break;
            foreach (var e in batch)
                db.ArchivedAuditEvents.Add(new AuditEventArchive
                {
                    ObjectKind = e.ObjectKind, ObjectId = e.ObjectId, ObjectLabel = e.ObjectLabel,
                    Action = e.Action, FieldName = e.FieldName, OldValue = e.OldValue,
                    NewValue = e.NewValue, Actor = e.Actor, TimestampUtc = e.TimestampUtc,
                    ArchivedUtc = now
                });
            db.AuditEvents.RemoveRange(batch);
            await db.SaveChangesAsync();
            moved += batch.Count;
        }
        await ExportAuditArchiveCoreAsync(_maxArchiveRows, _auditArchiveDir);
        return moved;
    }

    // M7: audit retention runs OFF the write path. Fire-and-forget with its
    // own try/catch + logging — a retention failure must never fail the
    // user's save.
    private void RunAuditRetentionFireAndForget()
    {
        _ = Task.Run(async () =>
        {
            try { await ArchiveAuditCoreAsync(); }
            catch (Exception ex) { _logger?.LogError(ex, "Background audit retention failed."); }
        });
    }

    /// <summary>Exports oldest archive-table rows to a checksummed .jsonl.gz file
    /// when the archive row cap is exceeded. Admin-only ("export now").
    /// A null path means under cap; a failure returns an error and leaves the
    /// rows in the archive table for retry.</summary>
    public async Task<(bool Ok, string? Error, string? Path)> ExportAuditArchiveIfNeededAsync(
        string actorRole, int? maxArchiveRows = null, string? directory = null)
    {
        if (actorRole != RoleAdmin)
            return (false, "Only administrators can export the audit archive.", null);
        var (path, failed) = await ExportAuditArchiveCoreAsync(
            maxArchiveRows ?? _maxArchiveRows, directory ?? _auditArchiveDir);
        return failed
            ? (false, "Audit archive export failed (see server logs). Rows were left in the archive table for retry.", null)
            : (true, null, path);
    }

    // M9: all file IO is inside try/catch — a failure (e.g. disk full) is
    // logged, returns failure, and leaves the rows for retry instead of
    // poisoning the write that triggered retention.
    private async Task<(string? Path, bool Failed)> ExportAuditArchiveCoreAsync(int cap, string dir)
    {
        try
        {
            List<AuditEventArchive> rows;
            using (var db = _factory.CreateDbContext())
            {
                var count = await db.ArchivedAuditEvents.LongCountAsync();
                if (count <= cap) return (null, false);
                rows = await db.ArchivedAuditEvents.OrderBy(a => a.Id)
                    .Take((int)(count - cap)).ToListAsync();
            }
            Directory.CreateDirectory(dir);
            var name = $"audit-archive-{rows.First().Id:D8}-{rows.Last().Id:D8}.jsonl.gz";
            var path = Path.Combine(dir, name);
            await using (var fs = File.Create(path))
            await using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                foreach (var r in rows)
                {
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r) + "\n");
                    await gz.WriteAsync(bytes);
                }
            // Verify before deleting: the file must decompress to exactly the row count.
            long lines = 0;
            await using (var fs = File.OpenRead(path))
            await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var sr = new StreamReader(gz))
                while (await sr.ReadLineAsync() != null) lines++;
            if (lines != rows.Count)
            {
                File.Delete(path);
                _logger?.LogError(
                    "Audit archive export verification failed: wrote {Wrote} rows, file holds {Lines}. Rows left for retry.",
                    rows.Count, lines);
                return (null, true);
            }
            await using (var fs = File.OpenRead(path))
                await File.WriteAllTextAsync(path + ".sha256",
                    Convert.ToHexString(await SHA256.HashDataAsync(fs)) + "  " + name + "\n");
            try
            {
                using var db = _factory.CreateDbContext();
                var maxId = rows.Last().Id;
                var doomed = await db.ArchivedAuditEvents.Where(a => a.Id <= maxId).ToListAsync();
                db.ArchivedAuditEvents.RemoveRange(doomed);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // The export file verified fine; the rows stay for the next
                // run to clean up. Not a failure of the export itself.
                _logger?.LogError(ex, "Audit export file verified but archive-table cleanup failed; will retry.");
            }
            return (path, false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Audit archive export failed; rows left in the archive table for retry.");
            return (null, true);
        }
    }

    public Task<List<AuditExportInfo>> GetAuditExportFilesAsync()
    {
        var list = new List<AuditExportInfo>();
        if (Directory.Exists(_auditArchiveDir))
            foreach (var f in new DirectoryInfo(_auditArchiveDir)
                .GetFiles("audit-archive-*.jsonl.gz").OrderBy(f => f.Name))
            {
                long rows = 0;
                var m = Regex.Match(f.Name, @"audit-archive-(\d+)-(\d+)\.jsonl\.gz");
                if (m.Success) rows = long.Parse(m.Groups[2].Value) - long.Parse(m.Groups[1].Value) + 1;
                var sha = "";
                var sidecar = f.FullName + ".sha256";
                if (File.Exists(sidecar)) sha = File.ReadAllText(sidecar).Split(' ')[0].Trim();
                list.Add(new AuditExportInfo(f.Name, rows, f.Length, f.CreationTimeUtc, sha));
            }
        return Task.FromResult(list);
    }

    /// <summary>Reads up to <paramref name="take"/> rows from an export file for the viewer.</summary>
    public async Task<List<AuditEventArchive>> ReadAuditExportAsync(string fileName, int take = 500)
    {
        var path = Path.GetFullPath(Path.Combine(_auditArchiveDir, fileName));
        if (!path.StartsWith(Path.GetFullPath(_auditArchiveDir) + Path.DirectorySeparatorChar))
            throw new ArgumentException("Invalid file name.", nameof(fileName));
        var rows = new List<AuditEventArchive>();
        await using var fs = File.OpenRead(path);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz);
        string? line;
        while (rows.Count < take && (line = await sr.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var r = JsonSerializer.Deserialize<AuditEventArchive>(line);
            if (r != null) rows.Add(r);
        }
        return rows;
    }
}
