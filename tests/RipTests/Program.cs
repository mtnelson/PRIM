// Integration tests for Prim services: runs against a temp SQLite database,
// exercising the same PrimService the Blazor UI uses. Exit code 0 = all pass.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Prim.Data;
using Prim.Services;

var dbPath = Path.Combine(Path.GetTempPath(), $"riptest-{Guid.NewGuid():N}.db");
var factory = new TestFactory(
    new DbContextOptionsBuilder<PrimDbContext>().UseSqlite($"Data Source={dbPath}").Options);
using (var db = factory.CreateDbContext()) { db.Database.EnsureCreated(); SeedData.EnsureSeeded(db); }
var svc = new PrimService(factory);

int pass = 0, fail = 0;
void Check(bool cond, string name, string? detail = null)
{
    if (cond) { pass++; Console.WriteLine($"PASS {name}"); }
    else { fail++; Console.WriteLine($"FAIL {name}" + (detail != null ? $" :: {detail}" : "")); }
}

// ---- seed ----
Check((await svc.GetRecordsAsync()).Count == 5, "seed records=5");
Check((await svc.GetContainersAsync()).Count == 6, "seed containers=6");
Check((await svc.GetLocationsAsync()).Count == 6, "seed locations=6");
Check((await svc.GetUsersAsync()).Count == 4, "seed users=4");

// ---- quick wildcard search ----
var q1 = await svc.SearchRecordsAsync(new Dictionary<string, string> { ["Case Number"] = "123*" });
Check(q1.Count == 2, "quick wildcard CaseNumber 123* -> 2", $"got {q1.Count}");
var q2 = await svc.SearchRecordsAsync(new Dictionary<string, string> { ["CaseNumber"] = "*88710" });
Check(q2.Count == 1 && q2[0].RecordNumber == "R-000002", "quick wildcard *88710 -> R-000002");
var q3 = await svc.SearchRecordsAsync(new Dictionary<string, string> { ["FieldOffice"] = "HQ", ["State"] = "Active" });
Check(q3.Count == 3, "quick multi-filter HQ+Active -> 3", $"got {q3.Count}");

// ---- advanced search AND / OR ----
var rows = new List<(string, string, string)> { ("CaseNumber", "=", "12345"), ("RecordType", "=", "Standard") };
var advAnd = await svc.AdvancedSearchRecordsAsync(rows, "AND");
Check(advAnd.Count == 1 && advAnd[0].RecordNumber == "R-000001", "advanced AND -> R-000001");
var advOr = await svc.AdvancedSearchRecordsAsync(rows, "OR");
Check(advOr.Count == 3, "advanced OR -> 3", $"got {advOr.Count}");
var advWild = await svc.AdvancedSearchRecordsAsync(new List<(string, string, string)> { ("Home", "=", "HQ-SHIP-*") }, "AND");
Check(advWild.Count == 2, "advanced wildcard Home HQ-SHIP-* -> 2", $"got {advWild.Count}");

// ---- record create: system numbers, uppercase normalization ----
var nr = new RecordItem { RecordType = "Standard", CaseClassification = "149", FieldOffice = "hq", CaseNumber = "abc123", Volume = "1", Subject = "harness test", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", State = "Active" };
var cr = await svc.SaveRecordAsync(nr, "harness");
Check(cr.Ok, "record create ok", cr.Error);
Check(nr.RecordNumber == "R-000006" && nr.Barcode == "REC000006", "system-generated R-000006/REC000006", $"{nr.RecordNumber}/{nr.Barcode}");
Check(nr.CaseNumber == "ABC123", "case number forced uppercase");
Check(nr.FieldOffice == "HQ", "field office forced uppercase (2-letter FO code)");
var auditCreate = await svc.GetAuditAsync("Record", nr.Id);
Check(auditCreate.Any(a => a.Action == "Created"), "audit Created event");

// ---- record update: field-level audit ----
var loaded = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000001");
var rv = loaded.RowVersion;
loaded.Subject = "Updated subject";
var up = await svc.SaveRecordAsync(loaded, "harness");
Check(up.Ok, "record update ok", up.Error);
var auditUpd = await svc.GetAuditAsync("Record", loaded.Id);
var subjAudit = auditUpd.FirstOrDefault(a => a.FieldName == "Subject");
Check(subjAudit != null && subjAudit.OldValue == "Quarterly review file" && subjAudit.NewValue == "Updated subject",
    "field-level audit Subject old/new", subjAudit == null ? "missing" : $"{subjAudit.OldValue}->{subjAudit.NewValue}");

// ---- optimistic concurrency ----
var stale = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000002");
var staleRv = stale.RowVersion;
var fresh = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000002");
fresh.Notes = "fresh change"; await svc.SaveRecordAsync(fresh, "harness");
stale.Notes = "stale change";
var cc = await svc.SaveRecordAsync(stale, "harness");
Check(!cc.Ok && cc.Error != null && cc.Error.Contains("latest version"), "concurrency rejects stale RowVersion", cc.Error);
Check(staleRv == fresh.RowVersion - 1 || stale.RowVersion != (await svc.GetRecordAsync(stale.Id))!.RowVersion, "rowversion advanced");

// ---- deletion reasons (all five) ----
async Task<int> NewDeletable(string subj)
{
    var r = new RecordItem { RecordType = "Standard", CaseClassification = "99", FieldOffice = "HQ", CaseNumber = "DEL" + Guid.NewGuid().ToString("N")[..6].ToUpper(), Volume = "1", Subject = subj, Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", State = "Active" };
    var res = await svc.SaveRecordAsync(r, "harness");
    if (!res.Ok) throw new Exception("setup create failed: " + res.Error);
    return r.Id;
}
var d1 = await NewDeletable("dup"); var d2 = await NewDeletable("merged");
var d3 = await NewDeletable("invalid"); var d4 = await NewDeletable("dispo"); var d5 = await NewDeletable("other");
Check(await svc.DeleteRecordsAsync(new[] { d1 }, "Duplicate entry", null, null, "harness") == 1, "delete reason: Duplicate entry");
Check(await svc.DeleteRecordsAsync(new[] { d2 }, "Merged with case file", "REC000001", null, "harness") == 1, "delete reason: Merged with case file");
Check(await svc.DeleteRecordsAsync(new[] { d3 }, "Invalid Case Number", null, null, "harness") == 1, "delete reason: Invalid Case Number");
Check(await svc.DeleteRecordsAsync(new[] { d4 }, "All files within range dispositioned", null, null, "harness") == 1, "delete reason: dispositioned");
Check(await svc.DeleteRecordsAsync(new[] { d5 }, "Other", null, "entered in error", "harness") == 1, "delete reason: Other");
var delRec = (await svc.GetRecordsAsync(includeDeleted: true)).First(r => r.Id == d5);
Check(delRec.Deleted && delRec.DeleteReason == "Other: entered in error", "Other reason text stored", delRec.DeleteReason);
var mergedRec = (await svc.GetRecordsAsync(includeDeleted: true)).First(r => r.Id == d2);
Check(mergedRec.MergedIntoBarcode == "REC000001", "merged-into barcode stored");
var visibleAfterDelete = await svc.GetRecordsAsync();
Check(visibleAfterDelete.Count == 6 && visibleAfterDelete.All(r => !r.Deleted), "deleted excluded from default search", $"got {visibleAfterDelete.Count}");
var delAudit = await svc.GetAuditAsync("Record", d1);
Check(delAudit.Any(a => a.Action == "Deleted" && (a.NewValue ?? "").Contains("Duplicate entry")), "audit Deleted with reason");

// ---- deletion reason validation (service-level, TIS-370) ----
var dv1 = await NewDeletable("neg-merge");
var negMerge = false;
try { await svc.DeleteRecordsAsync(new[] { dv1 }, "Merged with case file", null, null, "harness"); }
catch (ArgumentException) { negMerge = true; }
Check(negMerge, "service rejects merge without barcode");
var dv2 = await NewDeletable("neg-other");
var negOther = false;
try { await svc.DeleteRecordsAsync(new[] { dv2 }, "Other", null, "   ", "harness"); }
catch (ArgumentException) { negOther = true; }
Check(negOther, "service rejects Other without reason text");
var stillThere = await svc.GetRecordAsync(dv1);
Check(stillThere != null && !stillThere.Deleted, "rejected delete leaves record intact");

// ---- restore ----
Check(await svc.RestoreRecordsAsync(new[] { d1, d2 }, "harness") == 2, "restore 2 records");
var restored = await svc.GetRecordAsync(d1);
Check(restored != null && !restored.Deleted && restored.DeleteReason == null, "restored flags cleared");

// ---- containers ----
var nc = new Container { ContainerName = "TEST-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "001", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson" };
var ccRes = await svc.SaveContainerAsync(nc, "harness");
Check(ccRes.Ok && nc.Barcode == "CON000007", "container create CON000007", $"{ccRes.Error} {nc.Barcode}");
var dupC = new Container { ContainerName = "TEST-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "002", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson" };
bool dupThrew = false;
try { await svc.SaveContainerAsync(dupC, "harness"); } catch (DbUpdateException) { dupThrew = true; }
Check(dupThrew, "duplicate container (type+name) rejected by unique index");
Check(await svc.DeleteContainersAsync(new[] { nc.Id }, "harness") == 1, "container hard delete");
Check((await svc.GetContainersAsync()).All(c => c.Id != nc.Id), "container gone after delete");
var cAudit = await svc.GetAuditAsync("Container", nc.Id);
Check(cAudit.Any(a => a.Action == "Deleted"), "audit container Deleted");

// ---- container delete guard: parent with children is refused ----
var pc = new Container { ContainerName = "PARENT-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "010", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson" };
Check((await svc.SaveContainerAsync(pc, "harness")).Ok, "parent container create");
var kc = new Container { ContainerName = "CHILD-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "011", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", ParentContainerId = pc.Id };
Check((await svc.SaveContainerAsync(kc, "harness")).Ok, "child container create");
var blocked = false;
try { await svc.DeleteContainersAsync(new[] { pc.Id }, "harness"); }
catch (InvalidOperationException) { blocked = true; }
Check(blocked, "delete refused for container with children");
Check(await svc.DeleteContainersAsync(new[] { kc.Id }, "harness") == 1, "child container deleted");
Check(await svc.DeleteContainersAsync(new[] { pc.Id }, "harness") == 1, "parent deleted after child removed");

// ---- container update + optimistic concurrency (same RowVersion pattern as records) ----
var cu = new Container { ContainerName = "CONC-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "020", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson" };
Check((await svc.SaveContainerAsync(cu, "harness")).Ok, "concurrency setup create");
var cStale = (await svc.GetContainersAsync()).First(c => c.Id == cu.Id);
var cFresh = (await svc.GetContainersAsync()).First(c => c.Id == cu.Id);
cFresh.Description = "fresh"; Check((await svc.SaveContainerAsync(cFresh, "harness")).Ok, "container update ok");
cStale.Description = "stale";
var cCc = await svc.SaveContainerAsync(cStale, "harness");
Check(!cCc.Ok && (cCc.Error ?? "").Contains("latest version"), "container concurrency rejects stale RowVersion", cCc.Error);

// ---- locations: hierarchy + duplicate prevention + self-parent ----
var shelf = (await svc.GetLocationsAsync()).First(l => l.LocationName == "SHELF 1");
var nl = new Location { LocationName = "Harness Shelf", LocationType = "Shelf", ParentId = shelf.ParentId, Description = "test" };
var lr = await svc.SaveLocationAsync(nl, "harness");
Check(lr.Ok && nl.Barcode == "LOC000007", "location create LOC000007", $"{lr.Error} {nl.Barcode}");
var dupL = new Location { LocationName = "Harness Shelf", LocationType = "Shelf", ParentId = shelf.ParentId };
var dupLr = await svc.SaveLocationAsync(dupL, "harness");
Check(!dupLr.Ok && dupLr.Error != null && dupLr.Error.Contains("already exists"), "duplicate location (type+parent+name) rejected", dupLr.Error);
// same name under different parent is allowed
var bldg = (await svc.GetLocationsAsync()).First(l => l.LocationType == "Building");
var okL = new Location { LocationName = "Harness Shelf", LocationType = "Shelf", ParentId = bldg.Id };
Check((await svc.SaveLocationAsync(okL, "harness")).Ok, "same name under different parent allowed");
okL.ParentId = okL.Id;
var selfP = await svc.SaveLocationAsync(okL, "harness");
Check(!selfP.Ok, "self-parent rejected");

// ---- users ----
var nu = new AppUser { UserId = "htest", DisplayName = "Harness Tester", Role = "Staff", Email = "htest@local" };
Check((await svc.SaveUserAsync(nu, "harness")).Ok, "user create");
var dupU = new AppUser { UserId = "htest", DisplayName = "Dup", Role = "Staff" };
var dupUr = await svc.SaveUserAsync(dupU, "harness");
Check(!dupUr.Ok && dupUr.Error != null && dupUr.Error.Contains("already exists"), "duplicate UserId rejected", dupUr.Error);

// ---- move items ----
var mvRec = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000005");
var mvN = await svc.MoveItemsAsync("Record", new[] { mvRec.Id }, "FREEZER A", "Location", null, "chawes", false, "harness");
var mvAfter = await svc.GetRecordAsync(mvRec.Id);
Check(mvN == 1 && mvAfter!.Home == "FREEZER A" && mvAfter.Assignee == "chawes", "move items home+assignee");
var mvAudit = await svc.GetAuditAsync("Record", mvRec.Id);
Check(mvAudit.Any(a => a.Action == "Moved" && (a.NewValue ?? "").Contains("FREEZER A")), "audit Moved event");

// ---- workspaces: five slots + favorites ----
string[] slots = { "Workspace 1", "Workspace 2", "Workspace 3", "Workspace 4", "Workspace 5", "Favorites" };
foreach (var s in slots) Check(await svc.AddToWorkspaceAsync("mtnelson", s, "Record", mvRec.Id, s) == true, $"add to {s}");
Check(await svc.AddToWorkspaceAsync("mtnelson", "Favorites", "Record", mvRec.Id, "x") == false, "duplicate workspace add returns false");
Check((await svc.GetSlotAsync("mtnelson", "Favorites")).Count == 1, "favorites slot has 1");
await svc.RemoveFromSlotAsync("mtnelson", "Workspace 5", "Record", mvRec.Id);
Check((await svc.GetSlotAsync("mtnelson", "Workspace 5")).Count == 0, "remove from workspace slot");

// ---- saved searches ----
await svc.SaveSearchAsync(new SavedSearch { OwnerUserId = "mtnelson", Name = "HQ active", ObjectKind = "Record", Criteria = "FieldOffice=HQ", FieldsCsv = "RecordNumber,CaseNumber" });
var ss = await svc.GetSavedSearchesAsync("mtnelson");
Check(ss.Count == 1 && ss[0].Name == "HQ active", "saved search saved+listed");
await svc.DeleteSavedSearchAsync(ss[0].Id);
Check((await svc.GetSavedSearchesAsync("mtnelson")).Count == 0, "saved search deleted");

// ---- announcements ----
await svc.SetAnnouncementAsync("Maintenance Sunday", true, "harness");
Check(await svc.GetAnnouncementAsync() == "Maintenance Sunday", "announcement set/get");
await svc.SetAnnouncementAsync("", false, "harness");
Check(await svc.GetAnnouncementAsync() == null, "announcement cleared");

// ---- reports ----
var counts = await svc.GetCountsAsync();
Check(counts["Records"] > 0 && counts["Containers"] == 7 && counts["Users"] == 5, "report counts", string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}")));
var grouped = await svc.GroupRecordsAsync(r => r.State);
Check(grouped.Any(g => g.Label == "Active" && g.Count >= 3), "group by State", string.Join(",", grouped.Select(g => $"{g.Label}={g.Count}")));

// ---- container name preview (TIS-1278 simplified) ----
var preview = await svc.PreviewContainerNameAsync("Box", "HQ", "SHIP");
Check(!string.IsNullOrWhiteSpace(preview), "container name preview non-empty", preview);

Console.WriteLine($"--- {pass} passed, {fail} failed ---");
try { File.Delete(dbPath); File.Delete(dbPath + "-shm"); File.Delete(dbPath + "-wal"); } catch { }
return fail == 0 ? 0 : 1;

sealed class TestFactory(DbContextOptions<PrimDbContext> opts) : IDbContextFactory<PrimDbContext>
{
    public PrimDbContext CreateDbContext() => new(opts);
}
