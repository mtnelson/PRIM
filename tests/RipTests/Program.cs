// Integration tests for Prim services: runs against a temp SQLite database,
// exercising the same PrimService the Blazor UI uses. Exit code 0 = all pass.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
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
var mvN = await svc.MoveItemsAsync("Record", new[] { mvRec.Id }, "FREEZER A", "Location", null, "recordsmgr01", "User", null, false, "harness");
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

// ---- hierarchy: ref-id backfill on seed data ----
var recMgrUser = (await svc.GetUsersAsync()).First(u => u.UserId == "recordsmgr01");
var mtUser = (await svc.GetUsersAsync()).First(u => u.UserId == "mtnelson");
var seedR1 = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000001");
Check(seedR1.AssigneeRefId == recMgrUser.Id, "seed record assignee backfilled to user ref", seedR1.AssigneeRefId?.ToString());
var seedR4 = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000004");
Check(seedR4.HomeRefId == recMgrUser.Id && seedR4.HomeKind == "User", "seed user-home backfilled to ref id", seedR4.HomeRefId?.ToString());
var seedBox = (await svc.GetContainersAsync()).First(c => c.Barcode == "CON000001");
Check(seedBox.AssigneeRefId == recMgrUser.Id && seedBox.HomeRefId != null, "seed container assignee/home refs backfilled");

// ---- record/container create resolves assignee name to ref id ----
var refRec = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "REFASSIGN1", Volume = "1", Subject = "ref test", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
Check((await svc.SaveRecordAsync(refRec, "harness")).Ok, "ref record create ok");
Check(refRec.AssigneeRefId == mtUser.Id, "record save resolves assignee name to ref id", refRec.AssigneeRefId?.ToString());
var refCont = new Container { ContainerName = "REFASSIGN-BOX", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "030", Home = "SHELF 1", HomeKind = "Location", Assignee = "recordsmgr01", AssigneeKind = "User" };
Check((await svc.SaveContainerAsync(refCont, "harness")).Ok, "ref container create ok");
Check(refCont.AssigneeRefId == recMgrUser.Id, "container save resolves assignee name to ref id", refCont.AssigneeRefId?.ToString());

// ---- MoveItemsAsync: explicit assignee ref id + assignee-follows-home ----
var mvT = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "MVREF1", Volume = "1", Subject = "move ref test", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
Check((await svc.SaveRecordAsync(mvT, "harness")).Ok, "move-ref record create ok");
await svc.MoveItemsAsync("Record", new[] { mvT.Id }, null, null, null, "recordsmgr01", "User", recMgrUser.Id, false, "harness");
var mvT2 = await svc.GetRecordAsync(mvT.Id);
Check(mvT2!.AssigneeRefId == recMgrUser.Id && mvT2.Assignee == "recordsmgr01", "move sets explicit assignee ref id", mvT2.AssigneeRefId?.ToString());
var shelfLoc = (await svc.GetLocationsAsync()).First(l => l.LocationName == "SHELF 1");
await svc.MoveItemsAsync("Record", new[] { mvT.Id }, "SHELF 1", "Location", shelfLoc.Id, null, null, null, true, "harness");
var mvT3 = await svc.GetRecordAsync(mvT.Id);
Check(mvT3!.AssigneeRefId == shelfLoc.Id && mvT3.AssigneeKind == "Location" && mvT3.Assignee == "SHELF 1",
    "assignee-follows-home copies home ref id", mvT3.AssigneeRefId?.ToString());
var mvC = new Container { ContainerName = "MOVEREF-BOX", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "031", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
Check((await svc.SaveContainerAsync(mvC, "harness")).Ok, "move-ref container create ok");
await svc.MoveItemsAsync("Container", new[] { mvC.Id }, null, null, null, "recordsmgr01", "User", recMgrUser.Id, false, "harness");
var mvC2 = (await svc.GetContainersAsync()).First(c => c.Id == mvC.Id);
Check(mvC2.AssigneeRefId == recMgrUser.Id, "container move sets assignee ref id", mvC2.AssigneeRefId?.ToString());

// ---- GetHasChildrenAsync across the four kinds ----
var hasKids = await svc.GetHasChildrenAsync("Record", new[] { parent.Id, nonCompressed.Id });
Check(hasKids.Contains(parent.Id) && !hasKids.Contains(nonCompressed.Id), "record has-children: compressed parent true, plain false");
var hasKidsC = await svc.GetHasChildrenAsync("Container", new[] { seedBox.Id, mvC.Id });
Check(hasKidsC.Contains(seedBox.Id) && !hasKidsC.Contains(mvC.Id), "container has-children: box true, empty box false");
var hasKidsL = await svc.GetHasChildrenAsync("Location", new[] { shelfLoc.Id });
Check(hasKidsL.Contains(shelfLoc.Id), "location has-children: shelf true");
var htestUser = (await svc.GetUsersAsync()).First(u => u.UserId == "htest");
var hasKidsU = await svc.GetHasChildrenAsync("User", new[] { recMgrUser.Id, htestUser.Id });
Check(hasKidsU.Contains(recMgrUser.Id) && !hasKidsU.Contains(htestUser.Id), "user has-children: recordsmgr01 true, htest false");

// ---- GetChildItemsAsync ----
var kidsOfParent = await svc.GetChildItemsAsync("Record", parent.Id);
Check(kidsOfParent.Count == 1 && kidsOfParent[0].Kind == "Record" && kidsOfParent[0].Id == child.Id
      && kidsOfParent[0].Label == child.RecordNumber && !kidsOfParent[0].HasChildren,
    "child items of compressed record", string.Join(",", kidsOfParent.Select(k => k.Label)));
var kidsOfBox = await svc.GetChildItemsAsync("Container", seedBox.Id);
Check(kidsOfBox.Any(k => k.Kind == "Record" && k.Label == "R-000001")
      && kidsOfBox.Any(k => k.Kind == "Record" && k.Label == "R-000003"),
    "child items of container include homed records", string.Join(",", kidsOfBox.Select(k => k.Label)));
var subShelf = new Location { LocationName = "Shelf 1-A", LocationType = "Shelf", ParentId = shelfLoc.Id };
Check((await svc.SaveLocationAsync(subShelf, "harness")).Ok, "child location under shelf");
var kidsOfShelf = await svc.GetChildItemsAsync("Location", shelfLoc.Id);Check(kidsOfShelf.Any(k => k.Kind == "Container" && k.Label == "HQ-SHIP-LD263S")
      && kidsOfShelf.Any(k => k.Kind == "Location"),
    "child items of location include containers and child locations", string.Join(",", kidsOfShelf.Select(k => k.Kind + ":" + k.Label)));
var kidsOfMgr = await svc.GetChildItemsAsync("User", recMgrUser.Id);
Check(kidsOfMgr.Any(k => k.Kind == "Record" && k.Label == "R-000004"),
    "child items of user include homed records", string.Join(",", kidsOfMgr.Select(k => k.Label)));
// ---- child rows carry every grid column ----
Check(kidsOfParent.All(k => k.Cells != null && k.Columns != null && k.Columns.Count > 0),
    "child items carry Cells + Columns");
var kidRec = kidsOfParent[0];
Check(kidRec.Columns!.Count == GridColumns.RecordColumns().Count
      && kidRec.Cells!["RecordNumber"] == kidRec.Label
      && kidRec.Cells!["Barcode"] == child.Barcode
      && kidRec.Columns.Select(c => c.Key).SequenceEqual(GridColumns.RecordColumns().Select(c => c.Key)),
    "record child exposes every record column");
var kidBoxRec = kidsOfBox.First(k => k.Kind == "Record");
Check(kidBoxRec.Columns!.Select(c => c.Key).SequenceEqual(GridColumns.RecordColumns().Select(c => c.Key))
      && kidBoxRec.Cells!.Count == GridColumns.RecordColumns().Count,
    "container child records expose every record column");
var kidLoc = kidsOfShelf.First(k => k.Kind == "Location");
Check(kidLoc.Cells!["LocationName"] == kidLoc.Label
      && kidLoc.Columns!.Count == GridColumns.LocationColumns().Count,
    "location child exposes every location column");

// ---- GetAncestorPathsAsync: full chain + cycle guard ----
var paths = await svc.GetAncestorPathsAsync("Record", new[] { seedR1.Id });
var p1 = paths[seedR1.Id];
Check(p1.Count == 7 && p1.First().Label == "BLDG CRC" && p1.Last().Label == "R-000001"
      && p1.Select(s => s.Kind).SequenceEqual(new[] { "Location", "Location", "Location", "Location", "Location", "Container", "Record" }),
    "ancestor path R-000001 -> building ... box -> record",
    string.Join(" > ", p1.Select(s => s.Label)));
var childPath = (await svc.GetAncestorPathsAsync("Record", new[] { child.Id }))[child.Id];
Check(childPath.Count >= 4 && childPath[^1].Id == child.Id
      && childPath[^2].Id == parent.Id && childPath[^2].Kind == "Record"
      && childPath[^3].Label == "SHELF 1",
    "ancestor path of compressed child ends [.., SHELF 1, parent, child]",
    string.Join(" > ", childPath.Select(s => s.Label)));
// cycle guard: force a parent/child cycle directly in the db, path must terminate
using (var cdb = factory.CreateDbContext())
{
    var pRow = cdb.Records.First(r => r.Id == parent.Id);
    pRow.ParentRecordId = child.Id; cdb.SaveChanges();
}
var cycPath = (await svc.GetAncestorPathsAsync("Record", new[] { child.Id }))[child.Id];
Check(cycPath.Count <= 50 && cycPath.Count >= 2, "ancestor path terminates on cycle", cycPath.Count.ToString());
using (var cdb = factory.CreateDbContext())
{
    var pRow = cdb.Records.First(r => r.Id == parent.Id);
    pRow.ParentRecordId = null; cdb.SaveChanges();
}

// ---- GetByIdsAsync round-trips (workspace resolution) ----
Check((await svc.GetRecordsByIdsAsync(new[] { seedR1.Id, 999999 })).Count == 1, "GetRecordsByIdsAsync round-trip");
Check((await svc.GetContainersByIdsAsync(new[] { seedBox.Id })).Count == 1, "GetContainersByIdsAsync round-trip");
Check((await svc.GetLocationsByIdsAsync(new[] { shelfLoc.Id })).Count == 1, "GetLocationsByIdsAsync round-trip");
Check((await svc.GetUsersByIdsAsync(new[] { recMgrUser.Id })).Count == 1, "GetUsersByIdsAsync round-trip");
Check(await svc.GetObjectLabelAsync("Record", seedR1.Id) == "R-000001", "GetObjectLabelAsync record");
Check(await svc.GetObjectLabelAsync("Bogus", 1) == "", "GetObjectLabelAsync unknown kind");

// ---- labels: named collections of objects ----
var lblA = await svc.GetOrCreateLabelAsync("Urgent", "harness");
var lblA2 = await svc.GetOrCreateLabelAsync("urgent", "harness");
Check(lblA.Id == lblA2.Id, "label dedupe is case-insensitive");
await svc.SetObjectLabelsAsync("Record", seedR1.Id, new[] { "Urgent", "Cold Case" }, "harness");
var lblNames = await svc.GetObjectLabelNamesAsync("Record", seedR1.Id);
Check(lblNames.Count == 2 && lblNames.Contains("Urgent") && lblNames.Contains("Cold Case"), "labels assigned to record");
var lblPairs = await svc.GetObjectLabelPairsAsync("Record", new[] { seedR1.Id, 999999 });
Check(lblPairs[seedR1.Id].Count == 2 && !lblPairs.ContainsKey(999999), "label pairs keyed by object id");
var lblMembers = await svc.GetLabelMembersAsync(lblA.Id);
Check(lblMembers.Any(m => m.Kind == "Record" && m.Id == seedR1.Id && m.Label == "R-000001"), "label members include record");
var lblCounts = await svc.GetLabelCountsAsync();
Check(lblCounts[lblA.Id] == 1, "label member counts");
await svc.SetObjectLabelsAsync("Record", seedR1.Id, new[] { "Cold Case" }, "harness");
var lblNames2 = await svc.GetObjectLabelNamesAsync("Record", seedR1.Id);
Check(lblNames2.Count == 1 && lblNames2[0] == "Cold Case", "label removed via set");
var lblAudit = await svc.GetAuditAsync("Record", seedR1.Id);
Check(lblAudit.Any(a => a.Action == "Updated" && a.FieldName == "Labels"), "audit Labels change event names the item");
var auditBefore = (await svc.GetAuditAsync("Record", seedR1.Id)).Count;
await svc.SetObjectLabelsAsync("Record", seedR1.Id, new[] { "Cold Case" }, "harness");
Check((await svc.GetAuditAsync("Record", seedR1.Id)).Count == auditBefore, "label no-op writes no audit");
var kidsLabeled = await svc.GetChildItemsAsync("Container", seedBox.Id);
Check(kidsLabeled.First(k => k.Label == "R-000001").Cells!["Labels"] == "Cold Case", "child row Labels cell filled");
await svc.SetObjectLabelsAsync("Container", seedBox.Id, new[] { "Urgent" }, "harness");
Check((await svc.GetObjectLabelNamesAsync("Container", seedBox.Id)).Contains("Urgent"), "labels on container");
await svc.RenameLabelAsync(lblA.Id, "Priority", "harness");
Check((await svc.GetLabelsAsync()).Any(l => l.Name == "Priority"), "label renamed");
bool dupRename = false;
try { await svc.RenameLabelAsync(lblA.Id, "cold case", "harness"); } catch { dupRename = true; }
Check(dupRename, "rename to existing name rejected");
await svc.DeleteLabelAsync(lblA.Id, "harness");
Check((await svc.GetLabelsAsync()).All(l => l.Id != lblA.Id)
      && (await svc.GetObjectLabelNamesAsync("Container", seedBox.Id)).Count == 0,
    "label delete removes assignments");
var seedR2 = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000002");
using (var cdb = factory.CreateDbContext())
{
    var rec = cdb.Records.First(r => r.Id == seedR2.Id);
    rec.Labels = "Alpha, Beta, alpha";
    cdb.SaveChanges();
}
using (var cdb = factory.CreateDbContext()) { SeedData.BackfillLabels(cdb); }
var back = await svc.GetObjectLabelNamesAsync("Record", seedR2.Id);
Check(back.Count == 2 && back.Contains("Alpha") && back.Contains("Beta"), "legacy CSV backfilled and deduped");
using (var cdb = factory.CreateDbContext()) { SeedData.BackfillLabels(cdb); }
Check((await svc.GetObjectLabelNamesAsync("Record", seedR2.Id)).Count == 2, "backfill idempotent");

// ---- HotkeyManager: stacked handlers per (scope, combo) ----
{
    var hm = new HotkeyManager();
    hm.PushScope("workspaces");
    int fired = 0;
    var regA = hm.Register("workspaces", "Ctrl+C", () => fired = 1);
    var regB = hm.Register("workspaces", "Ctrl+C", () => fired = 2);
    Check(await hm.Handle("Ctrl+C") && fired == 2, "hotkey last-registered wins");
    regB.Dispose(); // disposing one grid must not kill its sibling's handler
    Check(await hm.Handle("Ctrl+C") && fired == 1, "hotkey sibling survives dispose");
    regA.Dispose();
    Check(!await hm.Handle("Ctrl+C"), "hotkey silent when no handlers remain");
    hm.PopScope();
}

// ---- infinite-scroll paging ----
var allRecs = await svc.GetRecordsAsync();
{
    var pg1 = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 3 });
    Check(pg1.Rows.Count == 3 && pg1.HasMore, "records page 1: 3 rows, hasMore");
    Check(pg1.Rows.Select(r => r.Id).SequenceEqual(pg1.Rows.Select(r => r.Id).OrderBy(id => id)),
        "records page 1 keyed ascending by Id");
    var p2 = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 3, AfterId = pg1.Rows[^1].Id });
    Check(p2.Rows.Count == 3 && !p2.Rows.Select(r => r.Id).Intersect(pg1.Rows.Select(r => r.Id)).Any(),
        "records page 2: next 3 rows, no overlap");
    // walk every page: full coverage, no dupes
    var seen = new List<int>();
    int? after = null; bool more = true;
    while (more)
    {
        var pg = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 4, AfterId = after });
        seen.AddRange(pg.Rows.Select(r => r.Id));
        after = pg.Rows.Count == 0 ? after : pg.Rows[^1].Id;
        more = pg.HasMore;
    }
    Check(seen.Count == allRecs.Count && seen.Distinct().Count() == seen.Count,
        "records paging walks entire set without dupes", $"walked {seen.Count}, total {allRecs.Count}");
    var last = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 5000, AfterId = seen[^1] });
    Check(last.Rows.Count == 0 && !last.HasMore, "records page past end: empty, no more");
}
{
    var f = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, Filter = "R-000001" });
    Check(f.Rows.Count == 1 && f.Rows[0].RecordNumber == "R-000001" && !f.HasMore, "records filter R-000001 -> 1");
    var fNone = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, Filter = "ZZZ-NO-MATCH" });
    Check(fNone.Rows.Count == 0 && !fNone.HasMore, "records filter no match -> empty");
    var sDesc = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, SortColumn = "RecordNumber", SortDescending = true });
    var nums = sDesc.Rows.Select(r => r.RecordNumber).ToList();
    Check(nums.SequenceEqual(nums.OrderByDescending(n => n)), "records sort RecordNumber desc");
    var sAsc = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, SortColumn = "FieldOffice" });
    var fos = sAsc.Rows.Select(r => r.FieldOffice).ToList();
    Check(fos.SequenceEqual(fos.OrderBy(n => n)), "records sort FieldOffice asc");
    var sBad = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 3, SortColumn = "Nope" });
    Check(sBad.Rows.Select(r => r.Id).SequenceEqual(sBad.Rows.Select(r => r.Id).OrderBy(id => id)),
        "records unknown sort column falls back to Id order");
    var sDescKey = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 3, SortDescending = true });
    var ids = sDescKey.Rows.Select(r => r.Id).ToList();
    Check(ids.SequenceEqual(ids.OrderByDescending(id => id)), "records default sort desc is Id-desc keyset");
}
{
    var cp = await svc.GetContainersPageAsync(new GridPageRequest { Take = 500 });
    Check(cp.Rows.Count == (await svc.GetContainersAsync()).Count && !cp.HasMore, "containers page: all in one chunk");
    var cf = await svc.GetContainersPageAsync(new GridPageRequest { Take = 500, Filter = "BOX" });
    Check(cf.Rows.Count >= 1 && cf.Rows.All(c => c.ContainerName.Contains("BOX", StringComparison.OrdinalIgnoreCase)
        || c.Barcode.Contains("BOX", StringComparison.OrdinalIgnoreCase)),
        "containers filter BOX matches", $"got {cf.Rows.Count}");
    var lp = await svc.GetLocationsPageAsync(new GridPageRequest { Take = 500 });
    Check(lp.Rows.Count == (await svc.GetLocationsAsync()).Count && !lp.HasMore, "locations page: all in one chunk", $"got {lp.Rows.Count}");
}
{
    var inact = new AppUser { UserId = "inactive01", DisplayName = "Inactive One", Role = "Viewer", Active = false };
    var su = await svc.SaveUserAsync(inact, "harness");
    Check(su.Ok, "inactive user create ok", su.Error);
    var uActive = await svc.GetUsersPageAsync(new GridPageRequest { Take = 500 }, includeInactive: false);
    var uAll = await svc.GetUsersPageAsync(new GridPageRequest { Take = 500 }, includeInactive: true);
    Check(uActive.Rows.All(u => u.Active) && uAll.Rows.Count == uActive.Rows.Count + 1,
        "users page honors includeInactive", $"active={uActive.Rows.Count} all={uAll.Rows.Count}");
}

// ---- virtualization support: counts, id lists, jump fetches ----
{
    var allRecsNow = await svc.GetRecordsAsync();
    var n = allRecsNow.Count;
    Check(await svc.CountRecordsAsync(null) == n, "CountRecordsAsync(null) == full set", $"got {await svc.CountRecordsAsync(null)}, want {n}");
    Check(await svc.CountRecordsAsync("R-000001") == 1, "CountRecordsAsync(filter) honors filter");
    Check(await svc.CountRecordsAsync("ZZZ-NO-MATCH") == 0, "CountRecordsAsync(no match) == 0");
    var allIds = await svc.GetAllRecordIdsAsync(null);
    Check(allIds.Count == n && allIds.SequenceEqual(allIds.OrderBy(x => x)),
        "GetAllRecordIdsAsync ordered ascending by Id");
    Check(allIds.ToHashSet().SetEquals(allRecsNow.Select(r => r.Id)),
        "GetAllRecordIdsAsync covers the full set");
    Check((await svc.GetAllRecordIdsAsync("R-000001")).Count == 1, "GetAllRecordIdsAsync(filter) -> 1");
    Check((await svc.GetAllRecordIdsAsync("ZZZ-NO-MATCH")).Count == 0, "GetAllRecordIdsAsync(no match) -> empty");

    // Jump fetch (Skip, no keyset cursor) must return exactly the rows the
    // keyset walk produces at that offset — the grid relies on this when the
    // user scrolls to a chunk whose predecessor was evicted.
    var walk = new List<int>();
    int? cur = null; bool m = true;
    while (m)
    {
        var p = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 2, AfterId = cur });
        walk.AddRange(p.Rows.Select(r => r.Id));
        cur = p.Rows.Count == 0 ? null : p.Rows[^1].Id;
        m = p.HasMore;
    }
    var jump = await svc.GetRecordsPageAsync(new GridPageRequest { Skip = 2, Take = 2 });
    Check(jump.Rows.Select(r => r.Id).SequenceEqual(walk.Skip(2).Take(2)),
        "jump fetch (Skip, no AfterId) matches keyset walk at offset");
    var jumpF = await svc.GetRecordsPageAsync(new GridPageRequest { Skip = 1, Take = 2, Filter = "R-00000" });
    var fAll = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, Filter = "R-00000" });
    Check(jumpF.Rows.Select(r => r.Id).SequenceEqual(fAll.Rows.Skip(1).Take(2).Select(r => r.Id)),
        "filtered jump fetch matches filtered offset window");
    var sJump = await svc.GetRecordsPageAsync(new GridPageRequest { Skip = 2, Take = 2, SortColumn = "RecordNumber" });
    var sAll = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, SortColumn = "RecordNumber" });
    Check(sJump.Rows.Select(r => r.Id).SequenceEqual(sAll.Rows.Skip(2).Take(2).Select(r => r.Id)),
        "explicit-sort jump fetch matches offset window");

    Check(await svc.CountContainersAsync(null) == (await svc.GetContainersAsync()).Count,
        "CountContainersAsync(null) == full set");
    Check(await svc.CountContainersAsync("BOX") >= 1, "CountContainersAsync(filter) >= 1");
    Check(await svc.CountLocationsAsync(null) == (await svc.GetLocationsAsync()).Count,
        "CountLocationsAsync(null) == full set");
    var uCount = await svc.CountUsersAsync(false);
    var uPage = await svc.GetUsersPageAsync(new GridPageRequest { Take = 500 }, includeInactive: false);
    Check(uCount == uPage.Rows.Count, "CountUsersAsync(false) matches active page", $"count={uCount} page={uPage.Rows.Count}");
    var cIds = await svc.GetAllContainerIdsAsync(null);
    Check(cIds.Count == (await svc.GetContainersAsync()).Count && cIds.SequenceEqual(cIds.OrderBy(x => x)),
        "GetAllContainerIdsAsync ordered, full set");

    var crit = new List<(string, string, string)> { ("CaseNumber", "=", "12345"), ("RecordType", "=", "Case File") };
    var advCnt = await svc.AdvancedSearchRecordsCountAsync(crit, "AND");
    var advIds = await svc.AdvancedSearchRecordIdsAsync(crit, "AND");
    Check(advIds.Count == advCnt && advIds.SequenceEqual(advIds.OrderBy(x => x)),
        "adv-search ids match count, ordered ascending", $"count={advCnt} ids={advIds.Count}");
}

// ---- bulk test-data seeding ----
{
    var before = (await svc.GetRecordsAsync()).Count;
    var maxNumBefore = (await svc.GetRecordsAsync()).Select(r => r.RecordNumber).Max();
    var seededNums = await svc.SeedTestRecordsAsync(25, "harness");
    Check(seededNums.Count == 25, "SeedTestRecordsAsync(25) returns 25 record numbers");
    var afterRecs = await svc.GetRecordsAsync();
    Check(afterRecs.Count == before + 25, "seed adds exactly 25 records", $"before={before} after={afterRecs.Count}");
    var seeded = afterRecs.Where(r => string.Compare(r.RecordNumber, maxNumBefore, StringComparison.Ordinal) > 0).ToList();
    Check(seeded.Count == 25 && seeded.All(r => !string.IsNullOrWhiteSpace(r.RecordType) && !string.IsNullOrWhiteSpace(r.CaseClassification)
        && !string.IsNullOrWhiteSpace(r.FieldOffice) && !string.IsNullOrWhiteSpace(r.CaseNumber)
        && !string.IsNullOrWhiteSpace(r.Volume) && !string.IsNullOrWhiteSpace(r.Home) && !string.IsNullOrWhiteSpace(r.Assignee)
        && !string.IsNullOrWhiteSpace(r.State) && !string.IsNullOrWhiteSpace(r.Subject) && !string.IsNullOrWhiteSpace(r.Barcode)),
        "seeded records have all fields filled");
    Check(seeded.All(r => (r.Subject ?? "").StartsWith("[TEST]")), "seeded subjects carry [TEST] prefix");
    var compressed = seeded.Where(r => r.RecordType == "Compressed").ToList();
    Check(compressed.Count > 0 && seeded.Any(r => r.ParentRecordId != null),
        "seed includes compressed parents with children", $"parents={compressed.Count}");
    var kidsOf = seeded.Where(r => r.ParentRecordId != null).All(r => compressed.Any(p => p.Id == r.ParentRecordId));
    Check(kidsOf, "seeded children point at seeded compressed parents");
}

// ---- advanced search: SQL-side paged path ----
{
    var crit = new List<(string, string, string)> { ("CaseNumber", "=", "12345"), ("RecordType", "=", "Case File") };
    var cnt = await svc.AdvancedSearchRecordsCountAsync(crit, "AND");
    Check(cnt == 1, "adv-search count AND -> 1", $"got {cnt}");
    var pg = await svc.AdvancedSearchRecordsPageAsync(crit, "AND", new GridPageRequest { Take = 500 });
    Check(pg.Rows.Count == 1 && pg.Rows[0].RecordNumber == "R-000001" && !pg.HasMore,
        "adv-search page AND -> R-000001, no more");
    var cntOr = await svc.AdvancedSearchRecordsCountAsync(
        new List<(string, string, string)> { ("CaseNumber", "=", "12345"), ("CaseNumber", "=", "88710") }, "OR");
    Check(cntOr == 3, "adv-search count OR -> 3", $"got {cntOr}");
    var wild = await svc.AdvancedSearchRecordsPageAsync(
        new List<(string, string, string)> { ("Home", "=", "HQ-SHIP-*") }, "AND", new GridPageRequest { Take = 500 });
    Check(wild.Rows.Count == 2 && !wild.HasMore, "adv-search wildcard HQ-SHIP-* -> 2", $"got {wild.Rows.Count}");
    var sw = await svc.AdvancedSearchRecordsPageAsync(
        new List<(string, string, string)> { ("CaseNumber", "StartsWith", "123") }, "AND", new GridPageRequest { Take = 500 });
    Check(sw.Rows.Count == 2, "adv-search StartsWith 123 -> 2", $"got {sw.Rows.Count}");
    var ew = await svc.AdvancedSearchRecordsPageAsync(
        new List<(string, string, string)> { ("CaseNumber", "EndsWith", "88710") }, "AND", new GridPageRequest { Take = 500 });
    Check(ew.Rows.Count == 1 && ew.Rows[0].RecordNumber == "R-000002", "adv-search EndsWith 88710 -> R-000002");
    var unk = await svc.AdvancedSearchRecordsCountAsync(
        new List<(string, string, string)> { ("Nope", "=", "x") }, "AND");
    Check(unk == (await svc.GetRecordsAsync()).Count, "adv-search unknown field ignored");
    // walk pages of 1: full coverage, no dupes
    var allIds = new List<int>(); int? cur = null; bool hasMore = true;
    var orCrit = new List<(string, string, string)> { ("CaseNumber", "=", "12345"), ("CaseNumber", "=", "88710") };
    while (hasMore)
    {
        var p = await svc.AdvancedSearchRecordsPageAsync(orCrit, "OR", new GridPageRequest { Take = 1, AfterId = cur });
        allIds.AddRange(p.Rows.Select(r => r.Id));
        cur = p.Rows.Count > 0 ? p.Rows[^1].Id : cur;
        hasMore = p.HasMore;
    }
    Check(allIds.Count == 3 && allIds.Distinct().Count() == 3, "adv-search OR walks 3 rows across pages");
}

// ---- full 1500-record seed: chunked reads, no dupes/omissions ----
{
    int maxIdBefore = 0;
    {
        int? c = null; bool m = true;
        while (m) { var p = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, AfterId = c });
            if (p.Rows.Count > 0) { maxIdBefore = Math.Max(maxIdBefore, p.Rows[^1].Id); c = p.Rows[^1].Id; } m = p.HasMore; }
    }
    var addedNums = await svc.SeedTestRecordsAsync(1500, "harness");
    Check(addedNums.Count == 1500, "seed 1500 returns 1500 numbers", $"got {addedNums.Count}");

    var seen = new List<int>(); var chunkSizes = new List<int>();
    int? cur = null; bool more = true;
    List<RecordItem> allRows = new();
    while (more)
    {
        var p = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, AfterId = cur });
        chunkSizes.Add(p.Rows.Count);
        seen.AddRange(p.Rows.Select(r => r.Id));
        allRows.AddRange(p.Rows);
        cur = p.Rows.Count > 0 ? p.Rows[^1].Id : cur;
        more = p.HasMore;
    }
    Check(chunkSizes.Count >= 4 && chunkSizes[0] == 500 && chunkSizes[1] == 500 && chunkSizes[2] == 500,
        "first three 500-row chunks full", $"got [{string.Join(",", chunkSizes)}]");
    Check(seen.Count == allRows.Count && seen.Distinct().Count() == seen.Count, "page walk: no dupes/omissions");
    Check(seen.SequenceEqual(seen.OrderBy(id => id)), "page walk: ascending Id order");
    var tail = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, AfterId = cur });
    Check(tail.Rows.Count == 0 && !tail.HasMore, "fetch past end: empty, no more");

    var freshBatch = allRows.Where(r => r.Id > maxIdBefore).ToList();
    Check(freshBatch.Count == 1500, "exactly 1500 new records", $"got {freshBatch.Count}");
    Check(freshBatch.All(r => r.Subject != null && r.Subject.StartsWith("[TEST]")), "new records carry [TEST] prefix");
    Check(freshBatch.All(r => !string.IsNullOrWhiteSpace(r.RecordNumber) && !string.IsNullOrWhiteSpace(r.Barcode)
        && !string.IsNullOrWhiteSpace(r.CaseNumber) && !string.IsNullOrWhiteSpace(r.FieldOffice)
        && !string.IsNullOrWhiteSpace(r.Home) && !string.IsNullOrWhiteSpace(r.Assignee)
        && !string.IsNullOrWhiteSpace(r.State) && !string.IsNullOrWhiteSpace(r.RecordType)),
        "new records have all fields filled");
    var nums = allRows.Select(r => r.RecordNumber).ToList();
    Check(nums.Distinct().Count() == nums.Count, "record numbers unique across table");
    var bars = allRows.Select(r => r.Barcode).ToList();
    Check(bars.Distinct().Count() == bars.Count, "barcodes unique across table");
    var suffixes = freshBatch.Select(r => int.Parse(r.RecordNumber["R-".Length..])).OrderBy(n => n).ToList();
    Check(suffixes[^1] - suffixes[0] + 1 == 1500 && suffixes.Distinct().Count() == 1500,
        "new record numbers form a contiguous sequence");
    var newParents = freshBatch.Where(r => r.RecordType == "Compressed").ToList();
    var newKids = freshBatch.Where(r => r.ParentRecordId != null).ToList();
    Check(newParents.Count == 20, "1500 seed: 20 compressed parents", $"got {newParents.Count}");
    Check(newKids.Count > 0 && newKids.All(k => newParents.Any(p => p.Id == k.ParentRecordId)),
        "1500 seed: children point at new compressed parents", $"kids={newKids.Count}");
}

// ---- search sessions (tab persistence) ----
var (cok, cerr, sess) = await svc.CreateSessionAsync(new SearchSession { OwnerUserId = "u1", PageKind = "records", Title = "t1", Filter = "abc" });
Check(cok && sess != null, "session create ok", cerr);
var open1 = await svc.GetOpenSessionsAsync("u1", "records");
Check(open1.Count == 1 && open1[0].Filter == "abc", "session get returns created session");
Check((await svc.GetOpenSessionsAsync("u1", "containers")).Count == 0, "session page isolation");
Check((await svc.GetOpenSessionsAsync("u2", "records")).Count == 0, "session owner isolation");

sess!.Title = "renamed";
sess.SortColumn = "RecordNumber"; sess.SortDescending = true;
sess.ColumnKeysCsv = "RecordNumber,Subject"; sess.Filter = "xyz";
sess.SelectedIdsCsv = "1,2"; sess.ExpandedIdsCsv = "7"; sess.CriteriaJson = "{\"logic\":\"AND\"}";
var (sok, serr) = await svc.SaveSessionAsync(sess);
Check(sok, "session save ok", serr);
var open2 = await svc.GetOpenSessionsAsync("u1", "records");
Check(open2[0].Title == "renamed" && open2[0].SortColumn == "RecordNumber" && open2[0].SortDescending
    && open2[0].ColumnKeysCsv == "RecordNumber,Subject" && open2[0].Filter == "xyz"
    && open2[0].SelectedIdsCsv == "1,2" && open2[0].ExpandedIdsCsv == "7" && open2[0].CriteriaJson == "{\"logic\":\"AND\"}",
    "session save persists all descriptor fields");

var (sokOther, serrOther) = await svc.SaveSessionAsync(new SearchSession { Id = sess.Id, OwnerUserId = "u2", PageKind = "records", Title = "hijack" });
Check(!sokOther && serrOther != null, "session save by other owner rejected", serrOther);
await svc.DeleteSessionAsync(sess.Id, "u2");
Check((await svc.GetOpenSessionsAsync("u1", "records")).Count == 1, "session delete by other owner is a no-op");

var ts = open2[0].ToTabState();
Check(open2[0].Filter == "xyz" && ts.SortColumn == "RecordNumber" && ts.SortDescending
    && ts.ColumnKeys != null && ts.ColumnKeys.SequenceEqual(new[] { "RecordNumber", "Subject" })
    && ts.SelectedIds.SetEquals(new[] { 1, 2 }) && ts.ExpandedIds.SetEquals(new[] { 7 }),
    "ToTabState round-trips descriptor");
var ts2 = new SearchTabState { SortColumn = "Subject", SortDescending = false,
    ColumnKeys = new List<string> { "Subject" }, SelectedIds = new HashSet<int> { 9 }, ExpandedIds = new HashSet<int>() };
open2[0].ApplyTabState(ts2);
Check(open2[0].SortColumn == "Subject" && !open2[0].SortDescending
    && open2[0].ColumnKeysCsv == "Subject" && open2[0].SelectedIdsCsv == "9" && open2[0].ExpandedIdsCsv == "",
    "ApplyTabState writes descriptor fields");
var ts3 = new SearchTabState(); // nulls clear the descriptor (Filter is page-owned, untouched)
open2[0].ApplyTabState(ts3);
Check(open2[0].Filter == "xyz" && open2[0].SortColumn == null && open2[0].ColumnKeysCsv == ""
    && open2[0].SelectedIdsCsv == "" && open2[0].ExpandedIdsCsv == "",
    "ApplyTabState nulls clear descriptor");

// cap: 10 tabs per page per owner
for (var i = 2; i <= 10; i++)
    await svc.CreateSessionAsync(new SearchSession { OwnerUserId = "u1", PageKind = "records", Title = $"t{i}" });
var (c11ok, c11err, _) = await svc.CreateSessionAsync(new SearchSession { OwnerUserId = "u1", PageKind = "records", Title = "t11" });
Check(!c11ok && c11err != null && c11err.Contains("10"), "session cap: 11th tab refused", c11err);
// cap is per (owner, page): another page still accepts
var (cOtherPage, _, _) = await svc.CreateSessionAsync(new SearchSession { OwnerUserId = "u1", PageKind = "containers", Title = "c1" });
Check(cOtherPage, "session cap is per page kind");
// LastUsedUtc ordering: most recently used first
var open3 = await svc.GetOpenSessionsAsync("u1", "records");
Check(open3.Count == 10 && open3[0].Title == "t10", "sessions ordered by LastUsedUtc desc", open3.Count > 0 ? open3[0].Title : "none");
await svc.SaveSessionAsync(open3[^1]); // touch the oldest
var open4 = await svc.GetOpenSessionsAsync("u1", "records");
Check(open4[0].Id == open3[^1].Id, "save bumps LastUsedUtc to front");

await svc.DeleteSessionAsync(sess.Id, "u1");
Check((await svc.GetOpenSessionsAsync("u1", "records")).Count == 9, "session gone after delete");
var (cAfter, _, _) = await svc.CreateSessionAsync(new SearchSession { OwnerUserId = "u1", PageKind = "records", Title = "t-new" });
Check(cAfter, "session create works again after delete (under cap)");

// ---- audit retention: count-based hot cap + archive table + file export ----
long hotBefore;
using (var db = factory.CreateDbContext()) hotBefore = await db.AuditEvents.LongCountAsync();
using (var db = factory.CreateDbContext())
{
    for (int i = 0; i < 30; i++)
        db.AuditEvents.Add(new AuditEvent
        {
            ObjectKind = "Record", ObjectId = 999, ObjectLabel = $"R-RET-{i}",
            Action = "Updated", Actor = "tester", TimestampUtc = DateTime.UtcNow.AddMinutes(i)
        });
    await db.SaveChangesAsync();
}
Check(await svc.ArchiveAuditIfNeededAsync(maxHotRows: 1_000_000) == 0, "archival no-op under cap");
var moved = await svc.ArchiveAuditIfNeededAsync(maxHotRows: 10);
Check(moved == hotBefore + 30 - 10, "archival moves overflow oldest-first", $"moved {moved}");
using (var db = factory.CreateDbContext())
{
    Check(await db.AuditEvents.LongCountAsync() == 10, "hot table at cap after archival");
    Check(await db.ArchivedAuditEvents.LongCountAsync() == hotBefore + 20, "archive holds the rest");
    var hot999 = await db.AuditEvents.Where(a => a.ObjectKind == "Record" && a.ObjectId == 999)
        .OrderBy(a => a.TimestampUtc).ToListAsync();
    var archMaxTs = await db.ArchivedAuditEvents.Where(a => a.ObjectKind == "Record" && a.ObjectId == 999)
        .MaxAsync(a => a.TimestampUtc);
    Check(hot999.Count == 10, "newest 10 audit rows stay hot");
    Check(hot999.First().TimestampUtc > archMaxTs, "hot/archive split is oldest-first");
}
var all999 = await svc.GetAuditAsync("Record", 999);
Check(all999.Count == 30, "per-item audit log includes archived rows", $"got {all999.Count}");

var tmpAudit = Path.Combine(Path.GetTempPath(), $"audittest-{Guid.NewGuid():N}");
var svc2 = new PrimService(factory, null, tmpAudit);
long archBefore;
using (var db = factory.CreateDbContext()) archBefore = await db.ArchivedAuditEvents.LongCountAsync();
Check(await svc2.ExportAuditArchiveIfNeededAsync(maxArchiveRows: 1_000_000) == null, "export no-op under cap");
var expPath = await svc2.ExportAuditArchiveIfNeededAsync(maxArchiveRows: 5);
Check(expPath != null && File.Exists(expPath), "export writes .jsonl.gz");
Check(expPath != null && File.Exists(expPath + ".sha256"), "export writes .sha256 sidecar");
if (expPath != null)
{
    var sha = File.ReadAllText(expPath + ".sha256").Split(' ')[0].Trim();
    Check(sha.Length == 64 && sha.All(c => Uri.IsHexDigit(c)), "sha256 sidecar is 64 hex chars");
}
using (var db = factory.CreateDbContext())
    Check(await db.ArchivedAuditEvents.LongCountAsync() == 5, "archive table at cap after export");
var expName = Path.GetFileName(expPath)!;
var readBack = await svc2.ReadAuditExportAsync(expName);
Check(readBack.Count == archBefore - 5, "export file reads back all exported rows", $"got {readBack.Count}");
var expFiles = await svc2.GetAuditExportFilesAsync();
Check(expFiles.Count == 1 && expFiles[0].Rows == archBefore - 5, "export listed with row count");
var afterExport = await svc2.GetAuditAsync("Record", 999);
Check(afterExport.Count == 30, "per-item audit spans hot + archive + export files", $"got {afterExport.Count}");
// path-traversal guard
bool threw = false;
try { await svc2.ReadAuditExportAsync("../../evil.gz"); } catch { threw = true; }
Check(threw, "export reader rejects path traversal");
try { Directory.Delete(tmpAudit, true); } catch { }

// bulk-operation hook: archival runs automatically with a tiny configured cap
var tinyCfg = new TestConfig(new Dictionary<string, string?> { ["Audit:MaxHotRows"] = "5" });
var tmpAudit2 = Path.Combine(Path.GetTempPath(), $"audittest-{Guid.NewGuid():N}");
var svc3 = new PrimService(factory, tinyCfg, tmpAudit2);
using (var db = factory.CreateDbContext())
{
    for (int i = 0; i < 8; i++)
        db.AuditEvents.Add(new AuditEvent
        {
            ObjectKind = "Record", ObjectId = 998, ObjectLabel = $"R-HOOK-{i}",
            Action = "Moved", Actor = "tester", TimestampUtc = DateTime.UtcNow
        });
    await db.SaveChangesAsync();
}
await svc3.MoveItemsAsync("Record", Array.Empty<int>(), null, null, null, null, null, null, false, "tester");
using (var db = factory.CreateDbContext())
    Check(await db.AuditEvents.LongCountAsync() == 5, "bulk op triggers archival to configured cap");
try { Directory.Delete(tmpAudit2, true); } catch { }

Console.WriteLine($"--- {pass} passed, {fail} failed ---");
try { File.Delete(dbPath); File.Delete(dbPath + "-shm"); File.Delete(dbPath + "-wal"); } catch { }
return fail == 0 ? 0 : 1;

sealed class TestFactory(DbContextOptions<PrimDbContext> opts) : IDbContextFactory<PrimDbContext>
{
    public PrimDbContext CreateDbContext() => new(opts);
}

// Minimal IConfiguration for retention-cap tests (no extra packages).
sealed class NullToken : IChangeToken
{
    public static readonly NullToken Instance = new();
    public bool HasChanged => false;
    public bool ActiveChangeCallbacks => false;
    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
        => NoopDisposable.Instance;
    sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

sealed class TestConfig(Dictionary<string, string?> values) : IConfiguration
{
    public string? this[string key]
    {
        get => values.TryGetValue(key, out var v) ? v : null;
        set => values[key] = value;
    }
    public IEnumerable<IConfigurationSection> GetChildren() => Enumerable.Empty<IConfigurationSection>();
    public IChangeToken GetReloadToken() => NullToken.Instance;
    public IConfigurationSection GetSection(string key) => new TestSection(values, key);

    sealed class TestSection(Dictionary<string, string?> values, string key) : IConfigurationSection
    {
        public string? this[string k]
        {
            get => values.TryGetValue(key + ":" + k, out var v) ? v : null;
            set => values[key + ":" + k] = value;
        }
        public string Key => key;
        public string Path => key;
        public string? Value
        {
            get => values.TryGetValue(key, out var v) ? v : null;
            set => values[key] = value;
        }
        public IEnumerable<IConfigurationSection> GetChildren() => Enumerable.Empty<IConfigurationSection>();
        public IChangeToken GetReloadToken() => NullToken.Instance;
        public IConfigurationSection GetSection(string k) => new TestSection(values, key + ":" + k);
    }
}
