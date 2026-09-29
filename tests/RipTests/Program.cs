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
Check((await svc.GetUsersAsync()).Count == 3, "seed users=3 (admin01, recordsmgr01, mtnelson)");

// ---- quick wildcard search ----
var q1 = await svc.SearchRecordsAsync(new Dictionary<string, string> { ["Case Number"] = "123*" });
Check(q1.Count == 2, "quick wildcard CaseNumber 123* -> 2", $"got {q1.Count}");
var q2 = await svc.SearchRecordsAsync(new Dictionary<string, string> { ["CaseNumber"] = "*88710" });
Check(q2.Count == 1 && q2[0].RecordNumber == "R-000002", "quick wildcard *88710 -> R-000002");
var q3 = await svc.SearchRecordsAsync(new Dictionary<string, string> { ["FieldOffice"] = "HQ", ["State"] = "Active" });
Check(q3.Count == 3, "quick multi-filter HQ+Active -> 3", $"got {q3.Count}");

// ---- advanced search AND / OR ----
var rows = new List<(string, string, string)> { ("CaseNumber", "=", "12345"), ("RecordType", "=", "Case File") };
var advAnd = await svc.AdvancedSearchRecordsAsync(rows, "AND");
Check(advAnd.Count == 1 && advAnd[0].RecordNumber == "R-000001", "advanced AND -> R-000001");
var advOr = await svc.AdvancedSearchRecordsAsync(rows, "OR");
Check(advOr.Count == 3, "advanced OR -> 3", $"got {advOr.Count}");
var advWild = await svc.AdvancedSearchRecordsAsync(new List<(string, string, string)> { ("Home", "=", "HQ-SHIP-*") }, "AND");
Check(advWild.Count == 2, "advanced wildcard Home HQ-SHIP-* -> 2", $"got {advWild.Count}");

// ---- record create: system numbers, uppercase normalization ----
var nr = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "hq", CaseNumber = "abc123", Volume = "1", Subject = "harness test", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
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
    var r = new RecordItem { RecordType = "Case File", CaseClassification = "99", FieldOffice = "HQ", CaseNumber = "DEL" + Guid.NewGuid().ToString("N")[..6].ToUpper(), Volume = "1", Subject = subj, Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
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
var nc = new Container { ContainerName = "TEST-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "001", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
var ccRes = await svc.SaveContainerAsync(nc, "harness");
Check(ccRes.Ok && nc.Barcode == "CON000007", "container create CON000007", $"{ccRes.Error} {nc.Barcode}");
var dupC = new Container { ContainerName = "TEST-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "002", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
bool dupThrew = false;
try { await svc.SaveContainerAsync(dupC, "harness"); } catch (DbUpdateException) { dupThrew = true; }
Check(dupThrew, "duplicate container (type+name) rejected by unique index");
Check(await svc.DeleteContainersAsync(new[] { nc.Id }, "harness") == 1, "container hard delete");
Check((await svc.GetContainersAsync()).All(c => c.Id != nc.Id), "container gone after delete");
var cAudit = await svc.GetAuditAsync("Container", nc.Id);
Check(cAudit.Any(a => a.Action == "Deleted"), "audit container Deleted");

// ---- container delete guard: parent with children is refused ----
var pc = new Container { ContainerName = "PARENT-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "010", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
Check((await svc.SaveContainerAsync(pc, "harness")).Ok, "parent container create");
var kc = new Container { ContainerName = "CHILD-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "011", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", ParentContainerId = pc.Id };
Check((await svc.SaveContainerAsync(kc, "harness")).Ok, "child container create");
var blocked = false;
try { await svc.DeleteContainersAsync(new[] { pc.Id }, "harness"); }
catch (InvalidOperationException) { blocked = true; }
Check(blocked, "delete refused for container with children");
Check(await svc.DeleteContainersAsync(new[] { kc.Id }, "harness") == 1, "child container deleted");
Check(await svc.DeleteContainersAsync(new[] { pc.Id }, "harness") == 1, "parent deleted after child removed");

// ---- container update + optimistic concurrency (same RowVersion pattern as records) ----
var cu = new Container { ContainerName = "CONC-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "020", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
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
var mvN = await svc.MoveItemsAsync("Record", new[] { mvRec.Id }, "FREEZER A", "Location", null, "recordsmgr01", "User", false, "harness");
var mvAfter = await svc.GetRecordAsync(mvRec.Id);
Check(mvN == 1 && mvAfter!.Home == "FREEZER A" && mvAfter.Assignee == "recordsmgr01" && mvAfter.AssigneeKind == "User", "move items home+assignee");
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
Check(counts["Records"] > 0 && counts["Containers"] == 7 && counts["Users"] == 4, "report counts", string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}")));
var grouped = await svc.GroupRecordsAsync(r => r.State);
Check(grouped.Any(g => g.Label == "Active" && g.Count >= 3), "group by State", string.Join(",", grouped.Select(g => $"{g.Label}={g.Count}")));

// ---- container name preview (TIS-1278 simplified) ----
var preview = await svc.PreviewContainerNameAsync("Box", "HQ", "SHIP");
Check(!string.IsNullOrWhiteSpace(preview), "container name preview non-empty", preview);

// ---- field office registry: exact user-supplied list, typo corrections ----
Check(FieldOffices.Offices.Count == 61, "61 field offices (exact user list)", FieldOffices.Offices.Count.ToString());
Check(FieldOffices.Offices.Select(o => o.Code).OrderBy(c => c).SequenceEqual(
    new[] { "AL","AQ","AX","AN","AT","BA","BH","BS","BQ","BU","BT","CE","CG","CI","CV","CO","DL","DN","DE","EP","HN","HO","IP","JN","JK","KC","KX","LV","LR","LA","LS","ME","MM","MI","MP","MO","NK","NH","NO","NR","NY","NF","OC","OM","PH","PX","PG","PD","RH","SC","SL","SU","SA","SD","SF","SJ","SV","SE","SI","TP","WF" }.OrderBy(c => c)),
    "office codes match user list exactly");
Check(!FieldOffices.Offices.Any(o => o.Name == "Cincinnatti" || o.Name == "Las" || o.Name == "Minneapolios"),
    "typo office names corrected");
Check(FieldOffices.Offices.Any(o => o.Name == "Cincinnati") &&
      FieldOffices.Offices.Any(o => o.Name == "Las Vegas") &&
      FieldOffices.Offices.Any(o => o.Name == "Minneapolis"),
    "corrected office names present");
Check(FieldOffices.Offices.All(o => o.Code.Length == 2 && o.Code == o.Code.ToUpperInvariant()),
    "office codes are 2-letter uppercase");
Check(FieldOffices.IsKnownCode("WF") && FieldOffices.IsKnownCode("wf") && !FieldOffices.IsKnownCode("XX"),
    "office code validation");

// ---- homing rules: service-level validation ----
Check(HomeRules.ValidateHome("Record", "User") == null, "record homed to user ok");
Check(HomeRules.ValidateHome("Container", "Location") == null, "container homed to location ok");
Check(HomeRules.ValidateHome("Record", "Compressed") != null, "record homed to compressed rejected");
Check(HomeRules.ValidateHome("Container", "Record") != null, "container homed to record rejected");
var badHome = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "BADHOME1", Volume = "1", Home = "X", HomeKind = "Compressed", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
Check(!(await svc.SaveRecordAsync(badHome, "harness")).Ok, "save record with compressed home rejected");
var badAssignee = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "BADASSIGN1", Volume = "1", Home = "SHELF 1", HomeKind = "Location", Assignee = "X", AssigneeKind = "Compressed", State = "Active" };
Check(!(await svc.SaveRecordAsync(badAssignee, "harness")).Ok, "save record with compressed assignee rejected");
var badContainer = new Container { ContainerName = "BADHOME-BOX", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "099", Home = "X", HomeKind = "Record", Assignee = "mtnelson", AssigneeKind = "User" };
Check(!(await svc.SaveContainerAsync(badContainer, "harness")).Ok, "save container with record home rejected");

// ---- compressed record children ----
var parent = new RecordItem { RecordType = "Compressed", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "PAR001", Volume = "1", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
Check((await svc.SaveRecordAsync(parent, "harness")).Ok, "compressed parent created");
var child = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "CHD001", Volume = "1", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", ParentRecordId = parent.Id, State = "Active" };
Check((await svc.SaveRecordAsync(child, "harness")).Ok, "child of compressed record ok");
var kids = await svc.GetChildRecordsAsync(parent.Id);
Check(kids.Count == 1 && kids[0].Id == child.Id, "compressed children listed");
var nonCompressed = (await svc.GetRecordsAsync()).First(r => r.RecordType == "Case File" && r.RecordNumber == "R-000001");
var badChild = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "CHD002", Volume = "1", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", ParentRecordId = nonCompressed.Id, State = "Active" };
Check(!(await svc.SaveRecordAsync(badChild, "harness")).Ok, "child of non-compressed record rejected");
var selfParent = await svc.GetRecordAsync(parent.Id);
selfParent!.ParentRecordId = selfParent.Id;
Check(!(await svc.SaveRecordAsync(selfParent, "harness")).Ok, "self-parent rejected");

// ---- user location membership ----
var locUser = new AppUser { UserId = "locuser", DisplayName = "Location User", Role = "Staff", LocationId = 1 };
Check((await svc.SaveUserAsync(locUser, "harness")).Ok, "user with location membership");
var locUserReload = (await svc.GetUsersAsync()).First(u => u.UserId == "locuser");
Check(locUserReload.LocationId == 1, "location membership persisted");
var pwLoc = await svc.SetUserPasswordAsync("locuser", "secret99", "harness");
Check(pwLoc.Ok, "set user password");

// ---- authentication via IAuthProvider (dev passwords) ----
var auth = new DevPasswordAuthProvider(factory);
var a1 = await auth.AuthenticateAsync("admin01", "admin01");
Check(a1.Ok && a1.Role == "Admin" && a1.UserId == "admin01", "admin01 login ok");
var a2 = await auth.AuthenticateAsync("recordsmgr01", "recordsmgr01");
Check(a2.Ok && a2.Role == "Records Manager", "recordsmgr01 login ok");
var a3 = await auth.AuthenticateAsync("mtnelson", "mtnelson");
Check(a3.Ok && a3.Role == "Staff", "mtnelson login ok");
var a4 = await auth.AuthenticateAsync("admin01", "wrong");
Check(!a4.Ok, "wrong password rejected");
var a5 = await auth.AuthenticateAsync("nobody", "nobody");
Check(!a5.Ok, "unknown user rejected");
var a6 = await auth.AuthenticateAsync("locuser", "secret99");
Check(a6.Ok, "changed password accepted");

// ---- per-user grid layouts persist ----
await svc.SaveGridLayoutAsync("mtnelson", "records", new List<string> { "Barcode", "RecordNumber" });
var layout = await svc.GetGridLayoutAsync("mtnelson", "records");
Check(layout != null && layout.SequenceEqual(new[] { "Barcode", "RecordNumber" }), "grid layout round-trip");
await svc.SaveGridLayoutAsync("mtnelson", "records", new List<string> { "RecordNumber" });
var layout2 = await svc.GetGridLayoutAsync("mtnelson", "records");
Check(layout2 != null && layout2.SequenceEqual(new[] { "RecordNumber" }), "grid layout update");
Check(await svc.GetGridLayoutAsync("mtnelson", "no-such-grid") == null, "missing grid layout is null");

Console.WriteLine($"--- {pass} passed, {fail} failed ---");
try { File.Delete(dbPath); File.Delete(dbPath + "-shm"); File.Delete(dbPath + "-wal"); } catch { }
return fail == 0 ? 0 : 1;

sealed class TestFactory(DbContextOptions<PrimDbContext> opts) : IDbContextFactory<PrimDbContext>
{
    public PrimDbContext CreateDbContext() => new(opts);
}
