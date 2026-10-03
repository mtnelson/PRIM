// Integration tests for Rim services: runs against a temp SQLite database,
// exercising the same RimService the Blazor UI uses. Exit code 0 = all pass.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using System.Reflection;
using Rim.Data;
using Rim.Services;

var dbPath = Path.Combine(Path.GetTempPath(), $"riptest-{Guid.NewGuid():N}.db");
var factory = new TestFactory(
    new DbContextOptionsBuilder<RimDbContext>().UseSqlite($"Data Source={dbPath}").Options);
using (var db = factory.CreateDbContext()) { db.Database.EnsureCreated(); SeedData.EnsureSeeded(db); }
var svc = new RimService(factory);

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
var cr = await svc.SaveRecordAsync(nr, "harness", "Records Manager");
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
var up = await svc.SaveRecordAsync(loaded, "harness", "Records Manager");
Check(up.Ok, "record update ok", up.Error);
var auditUpd = await svc.GetAuditAsync("Record", loaded.Id);
var subjAudit = auditUpd.FirstOrDefault(a => a.FieldName == "Subject");
Check(subjAudit != null && subjAudit.OldValue == "Quarterly review file" && subjAudit.NewValue == "Updated subject",
    "field-level audit Subject old/new", subjAudit == null ? "missing" : $"{subjAudit.OldValue}->{subjAudit.NewValue}");

// ---- optimistic concurrency ----
var stale = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000002");
var staleRv = stale.RowVersion;
var fresh = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000002");
fresh.Notes = "fresh change"; await svc.SaveRecordAsync(fresh, "harness", "Records Manager");
stale.Notes = "stale change";
var cc = await svc.SaveRecordAsync(stale, "harness", "Records Manager");
Check(!cc.Ok && cc.Error != null && cc.Error.Contains("latest version"), "concurrency rejects stale RowVersion", cc.Error);
Check(staleRv == fresh.RowVersion - 1 || stale.RowVersion != (await svc.GetRecordAsync(stale.Id))!.RowVersion, "rowversion advanced");

// ---- deletion reasons (all five) ----
async Task<int> NewDeletable(string subj)
{
    var r = new RecordItem { RecordType = "Case File", CaseClassification = "99", FieldOffice = "HQ", CaseNumber = "DEL" + Guid.NewGuid().ToString("N")[..6].ToUpper(), Volume = "1", Subject = subj, Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
    var res = await svc.SaveRecordAsync(r, "harness", "Records Manager");
    if (!res.Ok) throw new Exception("setup create failed: " + res.Error);
    return r.Id;
}
var d1 = await NewDeletable("dup"); var d2 = await NewDeletable("merged");
var d3 = await NewDeletable("invalid"); var d4 = await NewDeletable("dispo"); var d5 = await NewDeletable("other");
var dr1 = await svc.DeleteRecordsAsync(new[] { d1 }, "Duplicate entry", null, null, "harness");
Check(dr1.Ok && dr1.Count == 1, "delete reason: Duplicate entry", dr1.Error);
var dr2 = await svc.DeleteRecordsAsync(new[] { d2 }, "Merged with case file", "REC000001", null, "harness");
Check(dr2.Ok && dr2.Count == 1, "delete reason: Merged with case file", dr2.Error);
var dr3 = await svc.DeleteRecordsAsync(new[] { d3 }, "Invalid Case Number", null, null, "harness");
Check(dr3.Ok && dr3.Count == 1, "delete reason: Invalid Case Number", dr3.Error);
var dr4 = await svc.DeleteRecordsAsync(new[] { d4 }, "All files within range dispositioned", null, null, "harness");
Check(dr4.Ok && dr4.Count == 1, "delete reason: dispositioned", dr4.Error);
var dr5 = await svc.DeleteRecordsAsync(new[] { d5 }, "Other", null, "entered in error", "harness");
Check(dr5.Ok && dr5.Count == 1, "delete reason: Other", dr5.Error);
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
var negMergeRes = await svc.DeleteRecordsAsync(new[] { dv1 }, "Merged with case file", null, null, "harness");
Check(!negMergeRes.Ok && negMergeRes.Count == 0 && negMergeRes.Error != null, "service rejects merge without barcode", negMergeRes.Error);
var dv2 = await NewDeletable("neg-other");
var negOtherRes = await svc.DeleteRecordsAsync(new[] { dv2 }, "Other", null, "   ", "harness");
Check(!negOtherRes.Ok && negOtherRes.Count == 0 && negOtherRes.Error != null, "service rejects Other without reason text", negOtherRes.Error);
var stillThere = await svc.GetRecordAsync(dv1);
Check(stillThere != null && !stillThere.Deleted, "rejected delete leaves record intact");

// ---- restore ----
var rr = await svc.RestoreRecordsAsync(new[] { d1, d2 }, "harness");
Check(rr.Ok && rr.Count == 2, "restore 2 records", rr.Error);
var restored = await svc.GetRecordAsync(d1);
Check(restored != null && !restored.Deleted && restored.DeleteReason == null, "restored flags cleared");

// ---- containers ----
var nc = new Container { ContainerName = "TEST-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "001", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
var ccRes = await svc.SaveContainerAsync(nc, "harness");
Check(ccRes.Ok && nc.Barcode == "CON000007", "container create CON000007", $"{ccRes.Error} {nc.Barcode}");
var dupC = new Container { ContainerName = "TEST-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "002", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
var dupCRes = await svc.SaveContainerAsync(dupC, "harness");
Check(!dupCRes.Ok, "duplicate container (type+name) rejected as clean tuple", dupCRes.Error);
var dcNc = await svc.DeleteContainersAsync(new[] { nc.Id }, "harness");
Check(dcNc.Ok && dcNc.Count == 1, "container hard delete", dcNc.Error);
Check((await svc.GetContainersAsync()).All(c => c.Id != nc.Id), "container gone after delete");
var cAudit = await svc.GetAuditAsync("Container", nc.Id);
Check(cAudit.Any(a => a.Action == "Deleted"), "audit container Deleted");

// ---- container delete guard: parent with children is refused ----
var pc = new Container { ContainerName = "PARENT-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "010", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
Check((await svc.SaveContainerAsync(pc, "harness")).Ok, "parent container create");
var kc = new Container { ContainerName = "CHILD-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "011", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", ParentContainerId = pc.Id };
Check((await svc.SaveContainerAsync(kc, "harness")).Ok, "child container create");
var blockedRes = await svc.DeleteContainersAsync(new[] { pc.Id }, "harness");
Check(!blockedRes.Ok && blockedRes.Count == 0, "delete refused for container with children", blockedRes.Error);
var dcKc = await svc.DeleteContainersAsync(new[] { kc.Id }, "harness");
Check(dcKc.Ok && dcKc.Count == 1, "child container deleted", dcKc.Error);
var dcPc = await svc.DeleteContainersAsync(new[] { pc.Id }, "harness");
Check(dcPc.Ok && dcPc.Count == 1, "parent deleted after child removed", dcPc.Error);

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
Check((await svc.SaveUserAsync(nu, "harness", "Admin")).Ok, "user create");
var dupU = new AppUser { UserId = "htest", DisplayName = "Dup", Role = "Staff" };
var dupUr = await svc.SaveUserAsync(dupU, "harness", "Admin");
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
Check(!(await svc.SaveRecordAsync(badHome, "harness", "Records Manager")).Ok, "save record with compressed home rejected");
var badAssignee = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "BADASSIGN1", Volume = "1", Home = "SHELF 1", HomeKind = "Location", Assignee = "X", AssigneeKind = "Compressed", State = "Active" };
Check(!(await svc.SaveRecordAsync(badAssignee, "harness", "Records Manager")).Ok, "save record with compressed assignee rejected");
var badContainer = new Container { ContainerName = "BADHOME-BOX", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "099", Home = "X", HomeKind = "Record", Assignee = "mtnelson", AssigneeKind = "User" };
Check(!(await svc.SaveContainerAsync(badContainer, "harness")).Ok, "save container with record home rejected");

// ---- compressed record children ----
var parent = new RecordItem { RecordType = "Compressed", CompressedRole = "Parent", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "PAR001", Volume = "1", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
Check((await svc.SaveRecordAsync(parent, "harness", "Records Manager")).Ok, "compressed parent created");
var child = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "CHD001", Volume = "1", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", ParentRecordId = parent.Id, State = "Active" };
Check((await svc.SaveRecordAsync(child, "harness", "Records Manager")).Ok, "child of compressed record ok");
var kids = await svc.GetChildRecordsAsync(parent.Id);
Check(kids.Count == 1 && kids[0].Id == child.Id, "compressed children listed");
var nonCompressed = (await svc.GetRecordsAsync()).First(r => r.RecordType == "Case File" && r.RecordNumber == "R-000001");
var badChild = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "CHD002", Volume = "1", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", ParentRecordId = nonCompressed.Id, State = "Active" };
Check(!(await svc.SaveRecordAsync(badChild, "harness", "Records Manager")).Ok, "child of non-compressed record rejected");
var selfParent = await svc.GetRecordAsync(parent.Id);
selfParent!.ParentRecordId = selfParent.Id;
Check(!(await svc.SaveRecordAsync(selfParent, "harness", "Records Manager")).Ok, "self-parent rejected");

// ---- user location membership ----
var locUser = new AppUser { UserId = "locuser", DisplayName = "Location User", Role = "Staff", LocationId = 1 };
Check((await svc.SaveUserAsync(locUser, "harness", "Admin")).Ok, "user with location membership");
var locUserReload = (await svc.GetUsersAsync()).First(u => u.UserId == "locuser");
Check(locUserReload.LocationId == 1, "location membership persisted");
var pwLoc = await svc.SetUserPasswordAsync("locuser", "secret99", "harness", "Admin");
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
Check((await svc.SaveRecordAsync(refRec, "harness", "Records Manager")).Ok, "ref record create ok");
Check(refRec.AssigneeRefId == mtUser.Id, "record save resolves assignee name to ref id", refRec.AssigneeRefId?.ToString());
var refCont = new Container { ContainerName = "REFASSIGN-BOX", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "TEST", FormattedNumber = "030", Home = "SHELF 1", HomeKind = "Location", Assignee = "recordsmgr01", AssigneeKind = "User" };
Check((await svc.SaveContainerAsync(refCont, "harness")).Ok, "ref container create ok");
Check(refCont.AssigneeRefId == recMgrUser.Id, "container save resolves assignee name to ref id", refCont.AssigneeRefId?.ToString());

// ---- MoveItemsAsync: explicit assignee ref id + assignee-follows-home ----
var mvT = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "MVREF1", Volume = "1", Subject = "move ref test", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
Check((await svc.SaveRecordAsync(mvT, "harness", "Records Manager")).Ok, "move-ref record create ok");
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
// Guard: if the parent/child link above failed, kidsOfParent is empty —
// record the failure without aborting the whole run.
Check(kidsOfParent.Count > 0, "compressed parent returned its child row");
var kidRec = kidsOfParent.Count > 0 ? kidsOfParent[0] : null;
Check(kidRec != null && kidRec.Columns!.Count == GridColumns.RecordColumns().Count
      && kidRec!.Cells!["RecordNumber"] == kidRec.Label
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
    var su = await svc.SaveUserAsync(inact, "harness", "Admin");
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
    var (seed25Ok, seed25Err, seededNums) = await svc.SeedTestRecordsAsync(25, "harness", "Admin");
Check(seed25Ok, "SeedTestRecordsAsync(25) ok", seed25Err);
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

// ---- advanced search: Contains genuinely contains (no asterisks needed) ----
{
    var contains = await svc.AdvancedSearchRecordsPageAsync(
        new List<(string, string, string)> { ("CaseNumber", "Contains", "234") }, "AND", new GridPageRequest { Take = 500 });
    Check(contains.Rows.Any(r => r.RecordNumber == "R-000001"),
        "adv-search Contains 234 (no asterisks) matches R-000001 (CaseNumber 12345)");
    var containsWild = await svc.AdvancedSearchRecordsPageAsync(
        new List<(string, string, string)> { ("CaseNumber", "Contains", "*234*") }, "AND", new GridPageRequest { Take = 500 });
    Check(containsWild.Rows.Any(r => r.RecordNumber == "R-000001"),
        "adv-search Contains *234* (explicit wildcards) still matches R-000001");
    var eq = await svc.AdvancedSearchRecordsCountAsync(
        new List<(string, string, string)> { ("CaseNumber", "Equals", "12345") }, "AND");
    Check(eq == 2, "adv-search Equals 12345 unwrapped -> R-000001 + R-000003", $"got {eq}");
    var swNoAst = await svc.AdvancedSearchRecordsPageAsync(
        new List<(string, string, string)> { ("CaseNumber", "StartsWith", "123") }, "AND", new GridPageRequest { Take = 500 });
    Check(swNoAst.Rows.Count == 2, "adv-search StartsWith 123 unchanged -> 2", $"got {swNoAst.Rows.Count}");
    var ewNoAst = await svc.AdvancedSearchRecordsCountAsync(
        new List<(string, string, string)> { ("CaseNumber", "EndsWith", "88710") }, "AND");
    Check(ewNoAst == 1, "adv-search EndsWith 88710 unchanged -> 1", $"got {ewNoAst}");
}

// ---- advanced search: containers / locations / users ----
{
    var ccnt = await svc.AdvancedSearchContainersCountAsync(
        new List<(string, string, string)> { ("ContainerName", "Contains", "SHIP") }, "AND");
    Check(ccnt == 2, "adv-search containers Contains SHIP -> 2", $"got {ccnt}");
    var cIds = await svc.AdvancedSearchContainerIdsAsync(
        new List<(string, string, string)> { ("ContainerName", "Contains", "SHIP") }, "AND");
    Check(cIds.Count == ccnt && cIds.SequenceEqual(cIds.OrderBy(x => x)),
        "adv-search container ids match count, ordered ascending");
    var cp = await svc.AdvancedSearchContainersPageAsync(
        new List<(string, string, string)> { ("ContainerName", "Contains", "SHIP") }, "AND", new GridPageRequest { Take = 500 });
    Check(cp.Rows.Count == 2 && cp.Rows.All(c => c.ContainerName.Contains("SHIP")) && !cp.HasMore,
        "adv-search containers page -> both SHIP rows, no more");
    var cOr = await svc.AdvancedSearchContainersCountAsync(
        new List<(string, string, string)> { ("ContainerName", "Equals", "BIN464611"), ("ContainerName", "Equals", "TOTE02015") }, "OR");
    Check(cOr == 2, "adv-search containers OR -> 2", $"got {cOr}");
    var cUnk = await svc.AdvancedSearchContainersCountAsync(
        new List<(string, string, string)> { ("Nope", "=", "x") }, "AND");
    Check(cUnk == (await svc.GetContainersAsync()).Count, "adv-search containers unknown field ignored");

    var lc = await svc.AdvancedSearchLocationsCountAsync(
        new List<(string, string, string)> { ("LocationName", "Contains", "Harness") }, "AND");
    Check(lc == 2, "adv-search locations Contains Harness -> 2", $"got {lc}");
    var lIds = await svc.AdvancedSearchLocationIdsAsync(
        new List<(string, string, string)> { ("LocationName", "Contains", "Harness") }, "AND");
    Check(lIds.Count == lc && lIds.SequenceEqual(lIds.OrderBy(x => x)),
        "adv-search location ids match count, ordered ascending");
    var lp = await svc.AdvancedSearchLocationsPageAsync(
        new List<(string, string, string)> { ("LocationName", "Contains", "Harness") }, "AND", new GridPageRequest { Take = 500 });
    Check(lp.Rows.Count == 2 && lp.Rows.All(l => l.LocationName.Contains("Harness", StringComparison.OrdinalIgnoreCase)) && !lp.HasMore,
        "adv-search locations page -> both Harness Shelf rows, no more");

    var uc = await svc.AdvancedSearchUsersCountAsync(
        new List<(string, string, string)> { ("DisplayName", "Contains", "Manager") }, "AND");
    Check(uc == 1, "adv-search users Contains Manager -> 1", $"got {uc}");
    var uIds = await svc.AdvancedSearchUserIdsAsync(
        new List<(string, string, string)> { ("DisplayName", "Contains", "Manager") }, "AND");
    Check(uIds.Count == uc && uIds.SequenceEqual(uIds.OrderBy(x => x)),
        "adv-search user ids match count, ordered ascending");
    var upg = await svc.AdvancedSearchUsersPageAsync(
        new List<(string, string, string)> { ("UserId", "StartsWith", "admin") }, "AND", new GridPageRequest { Take = 500 });
    Check(upg.Rows.Count == 1 && upg.Rows[0].UserId == "admin01" && !upg.HasMore,
        "adv-search users StartsWith admin -> admin01, no more");
    var uUnk = await svc.AdvancedSearchUsersCountAsync(
        new List<(string, string, string)> { ("Nope", "=", "x") }, "AND");
    Check(uUnk == (await svc.GetUsersAsync()).Count, "adv-search users unknown field ignored");
}

// ---- label members as full entities (backs the label detail grids) ----
{
    var hlab = await svc.GetOrCreateLabelAsync("HarnessLabel", "harness");
    var hrec = (await svc.GetRecordsAsync()).First(r => (r.Subject ?? "").StartsWith("[TEST]"));
    var hcont = (await svc.GetContainersAsync()).First(c => c.ContainerName == "HQ-SHIP-LD263S");
    await svc.SetObjectLabelsAsync("Record", hrec.Id, new[] { "HarnessLabel" }, "harness");
    await svc.SetObjectLabelsAsync("Container", hcont.Id, new[] { "HarnessLabel" }, "harness");
    var hrecs = await svc.GetLabelRecordsAsync(hlab.Id);
    Check(hrecs.Count == 1 && hrecs[0].Id == hrec.Id, "GetLabelRecordsAsync -> the labeled record");
    var hconts = await svc.GetLabelContainersAsync(hlab.Id);
    Check(hconts.Count == 1 && hconts[0].Id == hcont.Id, "GetLabelContainersAsync -> the labeled container");
    Check((await svc.GetLabelLocationsAsync(hlab.Id)).Count == 0, "GetLabelLocationsAsync empty when none labeled");
    Check((await svc.GetLabelUsersAsync(hlab.Id)).Count == 0, "GetLabelUsersAsync empty when none labeled");
}

// ---- full 1500-record seed: chunked reads, no dupes/omissions ----
{
    int maxIdBefore = 0;
    {
        int? c = null; bool m = true;
        while (m) { var p = await svc.GetRecordsPageAsync(new GridPageRequest { Take = 500, AfterId = c });
            if (p.Rows.Count > 0) { maxIdBefore = Math.Max(maxIdBefore, p.Rows[^1].Id); c = p.Rows[^1].Id; } m = p.HasMore; }
    }
    var (seed1500Ok, seed1500Err, addedNums) = await svc.SeedTestRecordsAsync(1500, "harness", "Admin");
Check(seed1500Ok, "SeedTestRecordsAsync(1500) ok", seed1500Err);
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
Check((await svc.ArchiveAuditIfNeededAsync("Admin", maxHotRows: 1_000_000)).Moved == 0, "archival no-op under cap");
var archDenied = await svc.ArchiveAuditIfNeededAsync("Staff", maxHotRows: 10);
Check(!archDenied.Ok && archDenied.Moved == 0 && archDenied.Error != null, "archival denied for Staff role", archDenied.Error);
var archRes = await svc.ArchiveAuditIfNeededAsync("Admin", maxHotRows: 10);
Check(archRes.Ok && archRes.Moved == hotBefore + 30 - 10, "archival moves overflow oldest-first", archRes.Error ?? $"moved {archRes.Moved}");
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
var svc2 = new RimService(factory, null, tmpAudit);
long archBefore;
using (var db = factory.CreateDbContext()) archBefore = await db.ArchivedAuditEvents.LongCountAsync();
Check((await svc2.ExportAuditArchiveIfNeededAsync("Admin", maxArchiveRows: 1_000_000)).Path == null, "export no-op under cap");
var expRes = await svc2.ExportAuditArchiveIfNeededAsync("Admin", maxArchiveRows: 5);
Check(expRes.Ok, "export ok", expRes.Error);
var expDenied = await svc2.ExportAuditArchiveIfNeededAsync("Staff", maxArchiveRows: 5);
Check(!expDenied.Ok && expDenied.Path == null, "export denied for Staff role", expDenied.Error);
var expPath = expRes.Path;
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

// retention runs fire-and-forget off the write path: writes must not block on
// it, so assert on the deterministic direct call instead of background timing.
long hookHotBefore;
using (var db = factory.CreateDbContext()) hookHotBefore = await db.AuditEvents.LongCountAsync();
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
await svc.MoveItemsAsync("Record", Array.Empty<int>(), null, null, null, null, null, null, false, "tester");
var hookRes = await svc.ArchiveAuditIfNeededAsync("Admin", maxHotRows: 5);
using (var db = factory.CreateDbContext())
    Check(hookRes.Ok && await db.AuditEvents.LongCountAsync() == 5,
        "direct archival call trims hot table to cap", hookRes.Error ?? hookRes.Moved.ToString());

// ---- advanced search: SQL tab query log ----
{
    var log = new SearchQueryLog();
    var svcLog = new RimService(factory, queryLog: log);
    var crit = new List<(string, string, string)> { ("CaseNumber", "Contains", "123") };
    var runId = Guid.NewGuid();
    var n = await svcLog.AdvancedSearchRecordsCountAsync(crit, "AND", $"AdvancedSearch:Record:{runId:N}:count");
    Check(n > 0, "tagged count executes normally", $"got {n}");
    var entries = log.GetForRun(runId);
    Check(entries.Count == 1 && entries[0].Role == "count" && entries[0].Kind == "Record",
        "query log records one entry for tagged count", $"got {entries.Count}");
    Check(entries.Count == 1 && entries[0].Sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase),
        "logged SQL contains LIKE for Contains op", entries.Count == 1 ? entries[0].Sql : "none");
    Check(entries.Count == 1 && entries[0].DurationMs >= 0, "logged entry carries a duration");

    var before = log.Count;
    await svcLog.AdvancedSearchRecordsCountAsync(crit, "AND");
    Check(log.Count == before, "untagged query records nothing");

    var run2 = Guid.NewGuid();
    await svcLog.AdvancedSearchRecordsCountAsync(crit, "AND", $"AdvancedSearch:Record:{run2:N}:count");
    Check(log.GetForRun(runId).Count == 1 && log.GetForRun(run2).Count == 1,
        "run-id filtering returns only that run's entries");

    await svcLog.AdvancedSearchRecordsPageAsync(crit, "AND",
        new GridPageRequest { Take = 10 }, $"AdvancedSearch:Record:{runId:N}:page");
    var pageEntries = log.GetForRun(runId).Where(e => e.Role == "page").ToList();
    Check(pageEntries.Count == 1 && pageEntries[0].Sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase),
        "page query logs composed SQL with LIMIT", pageEntries.Count == 1 ? pageEntries[0].Sql : "none");

    await svcLog.AdvancedSearchRecordIdsAsync(crit, "AND", $"AdvancedSearch:Record:{runId:N}:ids");
    Check(log.GetForRun(runId).Any(e => e.Role == "ids"), "ids query logged with role=ids");

    Check(SearchQueryLog.TryParseTag($"AdvancedSearch:User:{Guid.NewGuid():N}:page", out var r, out var k, out var role)
        && k == "User" && role == "page" && r != Guid.Empty, "TryParseTag accepts well-formed tag");
    Check(!SearchQueryLog.TryParseTag("AdvancedSearch:User:notaguid:page", out _, out _, out _),
        "TryParseTag rejects bad guid");
    Check(!SearchQueryLog.TryParseTag(null, out _, out _, out _), "TryParseTag rejects null");
    Check(!SearchQueryLog.TryParseTag("SELECT 1", out _, out _, out _), "TryParseTag rejects non-tag");

    var log2 = new SearchQueryLog();
    var rid = Guid.NewGuid();
    for (int i = 0; i < 210; i++) log2.Record(rid, "Record", "count", "SELECT 1", 0.1);
    Check(log2.Count == 200, "query log caps at 200 entries", $"got {log2.Count}");
    Check(log2.GetForRun(Guid.NewGuid()).Count == 0, "GetForRun unknown run -> empty");
}

// ---- search activity log ----
{
    var before = await svc.GetSearchActivityAsync("tester");
    await svc.LogSearchActivityAsync(new SearchActivity
    {
        UserId = "tester", TimestampUtc = DateTime.UtcNow, ObjectKind = "Record",
        Logic = "AND", CriteriaSummary = "CaseNumber Contains '123'", ResultCount = 2, DurationMs = 3.5
    });
    var after = await svc.GetSearchActivityAsync("tester");
    Check(after.Count == before.Count + 1 && after[0].ObjectKind == "Record" && after[0].ResultCount == 2,
        "search activity row written and read newest-first");

    for (int i = 0; i < 205; i++)
        await svc.LogSearchActivityAsync(new SearchActivity
        {
            UserId = "capuser", TimestampUtc = DateTime.UtcNow, ObjectKind = "Record",
            Logic = "AND", CriteriaSummary = "x", ResultCount = i, DurationMs = 0
        });
    var capped = await svc.GetSearchActivityAsync("capuser", 500);
    Check(capped.Count == 200, "search activity pruned at 200/user cap", $"got {capped.Count}");
}

// ---- upgrade: SearchActivities created on table-less DB ----
{
    var dbPath2 = Path.Combine(Path.GetTempPath(), $"riptest-upg-{Guid.NewGuid():N}.db");
    var opts = new DbContextOptionsBuilder<RimDbContext>().UseSqlite($"Data Source={dbPath2}").Options;
    await using (var db = new RimDbContext(opts))
    {
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE SearchActivities");
    }
    await using (var db = new RimDbContext(opts))
    {
        SeedData.UpgradeSchema(db);
        Check(await db.SearchActivities.CountAsync() == 0, "UpgradeSchema recreates missing SearchActivities table");
    }
    try { File.Delete(dbPath2); } catch { }
}

// ---- v0.13.0: UpgradeSchema backfills user barcodes on legacy DBs ----
// Pre-v0.12.0 databases hold NULL in the (nullable, ALTER-added) Barcode
// column; the old backfill crashed materializing those rows. Simulate it.
{
    var dbPath4 = Path.Combine(Path.GetTempPath(), $"riptest-upg-{Guid.NewGuid():N}.db");
    var opts4 = new DbContextOptionsBuilder<RimDbContext>().UseSqlite($"Data Source={dbPath4}").Options;
    await using (var db = new RimDbContext(opts4))
    {
        await db.Database.EnsureCreatedAsync();
        // v0.13.1 added a UNIQUE model index on Users.Barcode; SQLite will not
        // DROP a column still referenced by an index, so drop it first.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_Users_Barcode");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Users DROP COLUMN Barcode");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Users ADD COLUMN Barcode TEXT");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Users (UserId, DisplayName, Role, Email, Active, CreatedUtc, RowVersion, Barcode) " +
            "VALUES ('legacy01','Legacy User','Staff',NULL,1,'2026-01-01',1,NULL)");
    }
    await using (var db = new RimDbContext(opts4))
    {
        SeedData.UpgradeSchema(db); // must not throw on NULL Barcode
        // The v0.13.1 M13 cheap-skip ("Any(u.Barcode == null || Barcode == '')")
        // cannot see NULLs: Barcode is [Required], so EF translates "== null" to
        // WHERE 0 and the backfill is skipped, leaving the NULL in place. A plain
        // EF read of that row then throws ("The data is NULL at ordinal 0").
        // Guard the throw so the suite records the failure instead of aborting.
        List<string> barcodes = new();
        Exception? readEx = null;
        try { barcodes = await db.Users.Select(u => u.Barcode).ToListAsync(); }
        catch (Exception ex) { readEx = ex; }
        Check(readEx == null,
            "v0.13.0 UpgradeSchema backfills NULL user barcodes without crashing",
            readEx == null ? "" : $"EF read still crashes: {readEx.GetType().Name}: {readEx.Message}");
        if (readEx == null)
            Check(barcodes.Count > 0 && barcodes.All(b => b != null && b.StartsWith("USR")),
                "v0.13.0 UpgradeSchema backfills NULL user barcodes without crashing",
                string.Join(",", barcodes));
    }
    try { File.Delete(dbPath4); } catch { }
}

// ---- v0.12.0: user barcodes ----
var usersNow = await svc.GetUsersAsync();
Check(usersNow.First(u => u.UserId == "admin01").Barcode == "USR000001" &&
      usersNow.First(u => u.UserId == "recordsmgr01").Barcode == "USR000002" &&
      usersNow.First(u => u.UserId == "mtnelson").Barcode == "USR000003",
    "v0.12.0 seed users have sequential USR barcodes");
var expectedUsr = "USR" + (usersNow.Count + 1).ToString("D6");
var nusr = new AppUser { UserId = "scantest01", DisplayName = "Scan Test", Role = "Staff", Email = "scan@rim.local", PasswordHash = "x", PasswordSalt = "y" };
var nusrRes = await svc.SaveUserAsync(nusr, "harness", "Admin");
Check(nusrRes.Ok && nusr.Barcode == expectedUsr, "v0.12.0 new user gets next USR barcode", $"{nusrRes.Error}/{nusr.Barcode}");
nusr.DisplayName = "Scan Test 2";
var nusrUpd = await svc.SaveUserAsync(nusr, "harness", "Admin");
Check(nusrUpd.Ok && (await svc.GetUsersAsync()).First(u => u.UserId == "scantest01").Barcode == expectedUsr,
    "v0.12.0 user barcode survives update", nusrUpd.Error);

// ---- v0.12.0: theme preference ----
Check(await svc.GetThemePreferenceAsync("mtnelson") == null, "v0.12.0 theme pref null by default");
bool themeThrew = false;
try { await svc.SetThemePreferenceAsync("mtnelson", "Blue"); }
catch (ArgumentException) { themeThrew = true; }
Check(themeThrew && await svc.GetThemePreferenceAsync("mtnelson") == null, "v0.12.0 invalid theme rejected");
await svc.SetThemePreferenceAsync("mtnelson", "Dark");
Check(await svc.GetThemePreferenceAsync("mtnelson") == "Dark", "v0.12.0 theme round-trip Dark");
await svc.SetThemePreferenceAsync("mtnelson", "Light");
Check(await svc.GetThemePreferenceAsync("mtnelson") == "Light", "v0.12.0 theme round-trip Light");
Check(await svc.GetThemePreferenceAsync("nosuchuser") == null, "v0.12.0 theme pref null for unknown user");

// ---- v0.12.0: compressed records ----
RecordItem NewRec(string type, string caseNo, string? role = null, int? parentId = null) => new()
{
    RecordType = type, CompressedRole = role, ParentRecordId = parentId,
    CaseClassification = "149", FieldOffice = "HQ", CaseNumber = caseNo, Volume = "1",
    Subject = "harness compressed", Home = "SHELF 1", HomeKind = "Location",
    Assignee = "mtnelson", AssigneeKind = "User", State = "Active"
};
var seedParent = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000003");
Check(seedParent.RecordType == "Compressed" && seedParent.CompressedRole == "Parent",
    "v0.12.0 seed R-000003 is a Compressed Parent");

var noRole = await svc.SaveRecordAsync(NewRec("Compressed", "CMP001"), "harness", "Records Manager");
Check(!noRole.Ok && noRole.Error != null && noRole.Error.Contains("Parent or a Child"),
    "v0.12.0 Compressed requires a role", noRole.Error);

var pRec = NewRec("Compressed", "CMP002", "Parent");
var pRes = await svc.SaveRecordAsync(pRec, "harness", "Records Manager");
Check(pRes.Ok, "v0.12.0 Compressed Parent creates", pRes.Error);

var childNoParent = await svc.SaveRecordAsync(NewRec("Compressed", "CMP003", "Child"), "harness", "Records Manager");
Check(!childNoParent.Ok && childNoParent.Error != null && childNoParent.Error.Contains("compressed parent"),
    "v0.12.0 Compressed Child without parent rejected", childNoParent.Error);

var nonParent = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000001");
var childBadParent = await svc.SaveRecordAsync(NewRec("Compressed", "CMP004", "Child", nonParent.Id), "harness", "Records Manager");
Check(!childBadParent.Ok && childBadParent.Error != null && childBadParent.Error.Contains("compressed parent"),
    "v0.12.0 Child under non-compressed record rejected", childBadParent.Error);

var parentAsChild = await svc.SaveRecordAsync(NewRec("Compressed", "CMP005", "Parent", seedParent.Id), "harness", "Records Manager");
Check(!parentAsChild.Ok && parentAsChild.Error != null && parentAsChild.Error.Contains("cannot be placed inside"),
    "v0.12.0 Parent cannot be filed under another (no nesting)", parentAsChild.Error);

// Happy path: child filed under seed parent — Home/Assignee become the parent record itself.
var cchild = NewRec("Compressed", "CMP006", "Child", seedParent.Id);
var childRes = await svc.SaveRecordAsync(cchild, "harness", "Records Manager");
Check(childRes.Ok, "v0.12.0 Compressed Child files under parent", childRes.Error);
Check(cchild.Home == "R-000003" && cchild.HomeKind == "Record" && cchild.HomeRefId == seedParent.Id,
    "v0.12.0 child's Home is the parent record", $"{cchild.Home}/{cchild.HomeKind}/{cchild.HomeRefId}");
Check(cchild.Assignee == "R-000003" && cchild.AssigneeKind == "Record" && cchild.AssigneeRefId == seedParent.Id,
    "v0.12.0 child's Assignee is the parent record", $"{cchild.Assignee}/{cchild.AssigneeKind}/{cchild.AssigneeRefId}");

// Any record type can be filed under a parent and keeps its type.
var filedCase = NewRec("Case File", "CMP007", null, seedParent.Id);
var filedRes = await svc.SaveRecordAsync(filedCase, "harness", "Records Manager");
Check(filedRes.Ok && filedCase.RecordType == "Case File" && filedCase.HomeKind == "Record",
    "v0.12.0 non-compressed record filed keeps type, homes to parent", filedRes.Error);

// Legacy (null-role) compressed record cannot accept children until edited.
int legacyId;
using (var db = factory.CreateDbContext())
{
    var legacy = NewRec("Compressed", "CMP008");
    legacy.CompressedRole = null;
    legacy.RecordNumber = "R-CMP008"; legacy.Barcode = "REC-CMP008";
    db.Records.Add(legacy);
    await db.SaveChangesAsync();
    legacyId = legacy.Id;
}
var childLegacy = await svc.SaveRecordAsync(NewRec("Case File", "CMP009", null, legacyId), "harness", "Records Manager");
Check(!childLegacy.Ok && childLegacy.Error != null && childLegacy.Error.Contains("compressed parent"),
    "v0.12.0 legacy null-role compressed cannot accept children", childLegacy.Error);

// Parent with children cannot change type.
var parentEdit = (await svc.GetRecordsAsync()).First(r => r.Id == seedParent.Id);
parentEdit.RecordType = "Case File";
var typeChg = await svc.SaveRecordAsync(parentEdit, "harness", "Records Manager");
Check(!typeChg.Ok && typeChg.Error != null && typeChg.Error.Contains("children"),
    "v0.12.0 parent-with-children type change blocked", typeChg.Error);

// A child cannot have children of its own.
var grandChild = await svc.SaveRecordAsync(NewRec("Case File", "CMP010", null, cchild.Id), "harness", "Records Manager");
Check(!grandChild.Ok && grandChild.Error != null && grandChild.Error.Contains("compressed parent"),
    "v0.12.0 child cannot accept children", grandChild.Error);

// Bulk move refuses filed records.
bool moveThrew = false;
string? moveMsg = null;
try
{
    await svc.MoveItemsAsync("Record", new[] { cchild.Id }, "BLDG CRC", "Location", 1, null, null, null, false, "harness");
}
catch (InvalidOperationException ex) { moveThrew = true; moveMsg = ex.Message; }
Check(moveThrew && moveMsg != null && moveMsg.Contains(cchild.RecordNumber),
    "v0.12.0 MoveItemsAsync refuses filed records", moveMsg);

// UpgradeSchema backfills roles for legacy rows: a null-role compressed
// record with children becomes a Parent, and legacy filed children are
// re-homed to the parent record itself.
int backfillLegacyId, backfillChildId;
using (var db = factory.CreateDbContext())
{
    var leg = NewRec("Compressed", "CMP011");
    leg.CompressedRole = null;
    leg.RecordNumber = "R-CMP011"; leg.Barcode = "REC-CMP011";
    db.Records.Add(leg);
    await db.SaveChangesAsync();
    backfillLegacyId = leg.Id;
    var legChild = NewRec("Case File", "CMP012", null, leg.Id);
    legChild.RecordNumber = "R-CMP012"; legChild.Barcode = "REC-CMP012";
    db.Records.Add(legChild);
    await db.SaveChangesAsync();
    backfillChildId = legChild.Id;
    SeedData.UpgradeSchema(db);
    // Raw-SQL backfills bypass the change tracker — re-read untracked.
    var legAfter = await db.Records.AsNoTracking().FirstAsync(r => r.Id == backfillLegacyId);
    var childAfter = await db.Records.AsNoTracking().FirstAsync(r => r.Id == backfillChildId);
    Check(legAfter.CompressedRole == "Parent",
        "v0.12.0 UpgradeSchema backfills Parent role for compressed-with-children", legAfter.CompressedRole);
    Check(childAfter.HomeKind == "Record" && childAfter.HomeRefId == backfillLegacyId &&
          childAfter.AssigneeKind == "Record" && childAfter.AssigneeRefId == backfillLegacyId,
        "v0.12.0 UpgradeSchema re-homes legacy filed child to parent record",
        $"{childAfter.HomeKind}/{childAfter.AssigneeKind}");
    var lone = await db.Records.AsNoTracking().FirstAsync(r => r.Id == legacyId);
    Check(lone.CompressedRole == null,
        "v0.12.0 legacy compressed without children stays null until next edit", lone.CompressedRole);
}

// ---- v0.12.0: barcode service ----
var hitRec = await svc.ResolveBarcodeAsync("REC000001");
Check(hitRec != null && hitRec.Kind == "Record" && hitRec.Name == "R-000001", "v0.12.0 resolve REC000001");
var hitCon = await svc.ResolveBarcodeAsync("CON000001");
Check(hitCon != null && hitCon.Kind == "Container" && hitCon.Name == "HQ-SHIP-LD263S", "v0.12.0 resolve CON000001");
var hitLoc = await svc.ResolveBarcodeAsync("LOC000001");
Check(hitLoc != null && hitLoc.Kind == "Location" && hitLoc.Name == "BLDG CRC", "v0.12.0 resolve LOC000001");
var hitUsr = await svc.ResolveBarcodeAsync("USR000001");
Check(hitUsr != null && hitUsr.Kind == "User" && hitUsr.Name == "RIM Administrator", "v0.12.0 resolve USR000001");
Check(await svc.ResolveBarcodeAsync("ZZZ999999") == null, "v0.12.0 resolve unknown -> null");
Check(await svc.ResolveBarcodeAsync("") == null, "v0.12.0 resolve empty -> null");

var slotRes = await svc.BarcodeAddToSlotAsync("mtnelson", "Workspace 3", new[] { "REC000001", "NOPE000000" });
Check(slotRes.SuccessCount == 1 && slotRes.FailCount == 1, "v0.12.0 add-to-slot mixed result");
var ws = await svc.GetSlotAsync("mtnelson", "Workspace 3");
Check(ws.Any(w => w.ObjectKind == "Record" && w.Label == "R-000001 · REC000001"), "v0.12.0 slot actually gained the record");
var slotDup = await svc.BarcodeAddToSlotAsync("mtnelson", "Workspace 3", new[] { "REC000001" });
Check(slotDup.SuccessCount == 1 && slotDup.FailCount == 0 && slotDup.Outcomes[0].Message.Contains("Already"),
    "v0.12.0 add-to-slot duplicate is idempotent", slotDup.Outcomes[0].Message);

var homeRes = await svc.BarcodeSetHomeAsync(new[] { "REC000001" }, "LOC000001", "harness");
Check(homeRes.SuccessCount == 1, "v0.12.0 barcode set-home ok", string.Join("; ", homeRes.Outcomes.Select(o => o.Message)));
var movedRec = (await svc.GetRecordsAsync()).First(r => r.RecordNumber == "R-000001");
Check(movedRec.Home == "BLDG CRC" && movedRec.HomeKind == "Location", "v0.12.0 set-home changed Home", $"{movedRec.Home}/{movedRec.HomeKind}");

var badDest = await svc.BarcodeSetHomeAsync(new[] { "REC000001" }, "REC000002", "harness");
Check(badDest.FailCount == 1 && badDest.Outcomes[0].Message.Contains("home must be a location, container, or user"),
    "v0.12.0 set-home rejects record destination", badDest.Outcomes[0].Message);

var badObj = await svc.BarcodeSetHomeAsync(new[] { "LOC000002" }, "LOC000001", "harness");
Check(badObj.FailCount == 1 && badObj.Outcomes[0].Message.Contains("only records and containers can be moved"),
    "v0.12.0 set-home rejects location object", badObj.Outcomes[0].Message);

var badAssign = await svc.BarcodeSetAssigneeAsync(new[] { "REC000001" }, "REC000002", "harness");
Check(badAssign.FailCount == 1 && badAssign.Outcomes[0].Message.Contains("must be a user"),
    "v0.12.0 set-assignee rejects non-user", badAssign.Outcomes[0].Message);

var okAssign = await svc.BarcodeSetAssigneeAsync(new[] { "REC000001" }, "USR000003", "harness");
Check(okAssign.SuccessCount == 1, "v0.12.0 set-assignee ok", string.Join("; ", okAssign.Outcomes.Select(o => o.Message)));

var filedChildBarcode = cchild.Barcode;
var refused = await svc.BarcodeSetHomeAsync(new[] { filedChildBarcode }, "LOC000001", "harness");
Check(refused.FailCount == 1 && refused.Outcomes[0].Message.Contains("compressed parent"),
    "v0.12.0 barcode move refuses filed record", refused.Outcomes[0].Message);

var unknownObj = await svc.BarcodeSetHomeAsync(new[] { "ZZZ999999" }, "LOC000001", "harness");
Check(unknownObj.FailCount == 1 && unknownObj.Outcomes[0].Message.Contains("not found"),
    "v0.12.0 barcode move reports unknown object", unknownObj.Outcomes[0].Message);

var haRes = await svc.BarcodeSetHomeAndAssigneeAsync(new[] { "REC000002" }, "LOC000001", "USR000003", "harness");
Check(haRes.SuccessCount == 1, "v0.12.0 set-home-and-assignee ok", string.Join("; ", haRes.Outcomes.Select(o => o.Message)));


// ================= v0.13.1 review-fix regression tests =================

// The v0.12.0 legacy-simulation rows above used deliberately non-conforming
// numbers ("R-CMP008" etc.). They poison NextIntAsync for every later service
// create (string MAX "R-CMP012" > "R-0015xx" -> TryParse fails -> returns 1 ->
// "duplicate number"). Their assertions have all run; remove the artifacts so
// the sections below exercise the service on a clean number sequence. The
// underlying service defect has its own regression test on a separate DB.
using (var cdb = factory.CreateDbContext())
{
    var poison = await cdb.Records.Where(r => r.RecordNumber == "R-CMP008" || r.RecordNumber == "R-CMP011" || r.RecordNumber == "R-CMP012").ToListAsync();
    cdb.Records.RemoveRange(poison);
    await cdb.SaveChangesAsync();
    Console.WriteLine($"cleaned up {poison.Count} legacy R-CMPxxx rows");
}
// ---- H1: admin-only gates on user management, passwords, seeding, type changes ----
var h1User = new AppUser { UserId = "h1user", DisplayName = "H1 User", Role = "Staff", Email = "h1@rim.local" };
var h1Denied = await svc.SaveUserAsync(h1User, "harness", "Staff");
Check(!h1Denied.Ok && h1Denied.Error != null && h1Denied.Error.Contains("administrators"),
    "H1 SaveUserAsync denied for Staff", h1Denied.Error);
Check(!(await svc.GetUsersAsync()).Any(u => u.UserId == "h1user"), "H1 denied user create left no row");
Check((await svc.SaveUserAsync(h1User, "harness", "Admin")).Ok, "H1 SaveUserAsync allowed for Admin");

var pwStaff = await svc.SetUserPasswordAsync("h1user", "newpassword99", "harness", "Staff");
Check(!pwStaff.Ok && pwStaff.Error != null, "H1 SetUserPasswordAsync denied for Staff", pwStaff.Error);
var pwShort = await svc.SetUserPasswordAsync("h1user", "short", "harness", "Admin");
Check(!pwShort.Ok && pwShort.Error != null && pwShort.Error.Contains("8"),
    "H1 password below 8 chars rejected", pwShort.Error);
var pwOk = await svc.SetUserPasswordAsync("h1user", "newpassword99", "harness", "Admin");
Check(pwOk.Ok, "H1 SetUserPasswordAsync allowed for Admin", pwOk.Error);
var aH1 = await new DevPasswordAuthProvider(factory).AuthenticateAsync("h1user", "newpassword99");
Check(aH1.Ok, "H1 admin-set password authenticates");

var (seedStaffOk, seedStaffErr, seedStaffNums) = await svc.SeedTestRecordsAsync(3, "harness", "Staff");
Check(!seedStaffOk && seedStaffErr != null && seedStaffNums.Count == 0,
    "H1 SeedTestRecordsAsync denied for Staff", seedStaffErr);

// Record-type change requires Records Manager or Admin; ordinary edits do not.
var typeRec = (await svc.GetRecordsAsync()).First(r => r.RecordType == "Case File" && r.ParentRecordId == null);
typeRec.RecordType = "Abstract";
var typeStaff = await svc.SaveRecordAsync(typeRec, "harness", "Staff");
Check(!typeStaff.Ok && typeStaff.Error != null && typeStaff.Error.Contains("Records Manager"),
    "H1 record type change denied for Staff", typeStaff.Error);
Check((await svc.GetRecordAsync(typeRec.Id))!.RecordType == "Case File",
    "H1 denied type change left the row untouched");
typeRec.RecordType = "Case File"; // restore, then prove non-type edits are still Staff-legal
typeRec.Subject = "H1 staff subject edit";
Check((await svc.SaveRecordAsync(typeRec, "harness", "Staff")).Ok, "H1 non-type edit allowed for Staff");
var typeRec2 = await svc.GetRecordAsync(typeRec.Id);
typeRec2!.RecordType = "Abstract";
var typeMgr = await svc.SaveRecordAsync(typeRec2, "harness", "Records Manager");
Check(typeMgr.Ok, "H1 record type change allowed for Records Manager", typeMgr.Error);
Check((await svc.GetRecordAsync(typeRec.Id))!.RecordType == "Abstract", "H1 type change persisted");
var typeAudit = await svc.GetAuditAsync("Record", typeRec.Id);
Check(typeAudit.Any(a => a.Action == "Type Changed" && a.FieldName == "RecordType"),
    "H1 type change audited as Type Changed");

// ---- H3: duplicate barcodes return clean tuples; sequential creates get distinct numbers ----
var h3c1 = new Container { ContainerName = "H3-BOX-001", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "H3", FormattedNumber = "001", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
var h3c2 = new Container { ContainerName = "H3-BOX-002", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "H3", FormattedNumber = "002", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User" };
Check((await svc.SaveContainerAsync(h3c1, "harness")).Ok, "H3 container 1 created");
Check((await svc.SaveContainerAsync(h3c2, "harness")).Ok, "H3 container 2 created");
Check(h3c1.Barcode != null && h3c2.Barcode != null && h3c1.Barcode != h3c2.Barcode
      && h3c1.Barcode.StartsWith("CON") && h3c2.Barcode.StartsWith("CON"),
    "H3 sequential container creates get distinct numbers", $"{h3c1.Barcode}/{h3c2.Barcode}");
var h3c2Dup = (await svc.GetContainersAsync()).First(c => c.Id == h3c2.Id);
h3c2Dup.Barcode = h3c1.Barcode!;
var h3DupRes = await svc.SaveContainerAsync(h3c2Dup, "harness");
Check(!h3DupRes.Ok && h3DupRes.Error != null, "H3 duplicate container barcode -> clean tuple, no exception", h3DupRes.Error);
Check((await svc.GetContainersAsync()).First(c => c.Id == h3c2.Id).Barcode == h3c2.Barcode,
    "H3 rejected barcode update left the row untouched");

var h3l1 = new Location { LocationName = "H3 Shelf 1", LocationType = "Shelf", ParentId = shelfLoc.Id };
var h3l2 = new Location { LocationName = "H3 Shelf 2", LocationType = "Shelf", ParentId = shelfLoc.Id };
Check((await svc.SaveLocationAsync(h3l1, "harness")).Ok, "H3 location 1 created");
Check((await svc.SaveLocationAsync(h3l2, "harness")).Ok, "H3 location 2 created");
Check(h3l1.Barcode != null && h3l2.Barcode != null && h3l1.Barcode != h3l2.Barcode,
    "H3 sequential location creates get distinct numbers", $"{h3l1.Barcode}/{h3l2.Barcode}");
var h3l2Dup = (await svc.GetLocationsAsync()).First(l => l.Id == h3l2.Id);
h3l2Dup.Barcode = h3l1.Barcode!;
var h3lDupRes = await svc.SaveLocationAsync(h3l2Dup, "harness");
Check(!h3lDupRes.Ok && h3lDupRes.Error != null, "H3 duplicate location barcode -> clean tuple, no exception", h3lDupRes.Error);

// User barcodes are system-assigned on create and never copied on update,
// so no duplicate-barcode collision path exists there.
var h3u1 = new AppUser { UserId = "h3user1", DisplayName = "H3 One", Role = "Staff" };
var h3u2 = new AppUser { UserId = "h3user2", DisplayName = "H3 Two", Role = "Staff" };
Check((await svc.SaveUserAsync(h3u1, "harness", "Admin")).Ok, "H3 user 1 created");
Check((await svc.SaveUserAsync(h3u2, "harness", "Admin")).Ok, "H3 user 2 created");
Check(h3u1.Barcode != null && h3u2.Barcode != null && h3u1.Barcode != h3u2.Barcode && h3u1.Barcode.StartsWith("USR"),
    "H3 sequential user creates get distinct USR barcodes", $"{h3u1.Barcode}/{h3u2.Barcode}");
var h3u2e = (await svc.GetUsersAsync()).First(u => u.UserId == "h3user2");
h3u2e.Barcode = h3u1.Barcode!;
var h3uUpd = await svc.SaveUserAsync(h3u2e, "harness", "Admin");
Check(h3uUpd.Ok && (await svc.GetUsersAsync()).First(u => u.UserId == "h3user2").Barcode == h3u2.Barcode,
    "H3 user update ignores caller-supplied barcode", h3uUpd.Error);

// ---- B2: ancestor paths over location -> container -> compressed parent -> child ----
var b2Bldg = new Location { LocationName = "B2 BLDG", LocationType = "Building" };
Check((await svc.SaveLocationAsync(b2Bldg, "harness")).Ok, "B2 building created");
var b2Shelf = new Location { LocationName = "B2 SHELF", LocationType = "Shelf", ParentId = b2Bldg.Id };
Check((await svc.SaveLocationAsync(b2Shelf, "harness")).Ok, "B2 shelf created");
var b2Box = new Container { ContainerName = "B2-BOX", ContainerType = "Box", FieldOffice = "HQ", ContainerCode = "B2", FormattedNumber = "001", Home = b2Shelf.LocationName, HomeKind = "Location", HomeRefId = b2Shelf.Id, Assignee = "mtnelson", AssigneeKind = "User" };
Check((await svc.SaveContainerAsync(b2Box, "harness")).Ok, "B2 container created");
var b2Parent = NewRec("Compressed", "B2PAR001", "Parent");
b2Parent.Home = b2Box.ContainerName; b2Parent.HomeKind = "Container"; b2Parent.HomeRefId = b2Box.Id;
b2Parent.Assignee = "mtnelson"; b2Parent.AssigneeKind = "User";
var b2ParentRes = await svc.SaveRecordAsync(b2Parent, "harness", "Records Manager");
Check(b2ParentRes.Ok, "B2 compressed parent created", b2ParentRes.Error);
var b2ChildRes = await svc.SaveRecordAsync(NewRec("Compressed", "B2CHD001", "Child", b2Parent.Id), "harness", "Records Manager");
var b2Child = b2ChildRes.Ok ? (await svc.GetRecordsAsync()).First(r => r.CaseNumber == "B2CHD001") : null;
Check(b2ChildRes.Ok, "B2 compressed child created", b2ChildRes.Error);
var b2Path = b2Child == null ? new List<Rim.Services.PathSeg>()
    : (await svc.GetAncestorPathsAsync("Record", new[] { b2Child.Id }))[b2Child.Id];
Check(b2Path.Count == 5, "B2 ancestor path has 5 segments", string.Join(" > ", b2Path.Select(p => p.Label)));
Check(b2Path.Select(p => p.Kind).SequenceEqual(new[] { "Location", "Location", "Container", "Record", "Record" }),
    "B2 ancestor kinds are root-first");
Check(b2Path.Count == 5 && b2Path[0].Label == "B2 BLDG" && b2Path[1].Label == "B2 SHELF" && b2Path[2].Label == "B2-BOX",
    "B2 ancestor labels are root-first");
Check(b2Child != null && b2Path.Count == 5 && b2Path[3].Id == b2Parent.Id && b2Path[4].Id == b2Child.Id && b2Path[4].Label == b2Child.RecordNumber,
    "B2 path ends [parent, child]");
// Deliberately introduce a parent<->child cycle behind the service's back:
// the path walk must still terminate.
if (b2Child != null)
{
    using (var cdb = factory.CreateDbContext())
    {
        var prow = cdb.Records.First(r => r.Id == b2Parent.Id);
        prow.ParentRecordId = b2Child.Id; cdb.SaveChanges();
    }
    var b2Cyc = (await svc.GetAncestorPathsAsync("Record", new[] { b2Child.Id }))[b2Child.Id];
    Check(b2Cyc.Count >= 2 && b2Cyc.Count <= 50 && b2Cyc.Any(p => p.Id == b2Parent.Id) && b2Cyc.Last().Id == b2Child.Id,
        "B2 ancestor path terminates on a cycle", b2Cyc.Count.ToString());
    using (var cdb = factory.CreateDbContext())
    {
        var prow = cdb.Records.First(r => r.Id == b2Parent.Id);
        prow.ParentRecordId = null; cdb.SaveChanges();
    }
}
else Check(false, "B2 ancestor path terminates on a cycle", "child was not created");

// ---- M3: filing under a soft-deleted parent is refused ----
var m3Parent = NewRec("Compressed", "M3PAR001", "Parent");
Check((await svc.SaveRecordAsync(m3Parent, "harness", "Records Manager")).Ok, "M3 compressed parent created");
var m3Del = await svc.DeleteRecordsAsync(new[] { m3Parent.Id }, "Duplicate entry", null, null, "harness");
Check(m3Del.Ok && m3Del.Count == 1, "M3 parent soft-deleted", m3Del.Error);
var m3Child = await svc.SaveRecordAsync(NewRec("Compressed", "M3CHD001", "Child", m3Parent.Id), "harness", "Records Manager");
Check(!m3Child.Ok && m3Child.Error != null && m3Child.Error.Contains("deleted"),
    "M3 filing under a soft-deleted parent refused", m3Child.Error);

// ---- M4: soft-deleting a compressed Parent with live children is refused ----
var m4Parent = NewRec("Compressed", "M4PAR001", "Parent");
Check((await svc.SaveRecordAsync(m4Parent, "harness", "Records Manager")).Ok, "M4 compressed parent created");
var m4Child = NewRec("Compressed", "M4CHD001", "Child", m4Parent.Id);
Check((await svc.SaveRecordAsync(m4Child, "harness", "Records Manager")).Ok, "M4 compressed child filed");
var m4DelBlocked = await svc.DeleteRecordsAsync(new[] { m4Parent.Id }, "Duplicate entry", null, null, "harness");
Check(!m4DelBlocked.Ok && m4DelBlocked.Count == 0 && m4DelBlocked.Error != null && m4DelBlocked.Error.Contains("children"),
    "M4 delete of a parent with live children refused", m4DelBlocked.Error);
var m4ParentAfter = m4Parent.Id == 0 ? null : await svc.GetRecordAsync(m4Parent.Id);
Check(m4ParentAfter != null && !m4ParentAfter.Deleted, "M4 refused delete left the parent intact");
var m4DelChild = await svc.DeleteRecordsAsync(new[] { m4Child.Id }, "Duplicate entry", null, null, "harness");
Check(m4DelChild.Ok && m4DelChild.Count == 1, "M4 child deleted", m4DelChild.Error);
var m4DelParent = await svc.DeleteRecordsAsync(new[] { m4Parent.Id }, "Duplicate entry", null, null, "harness");
Check(m4DelParent.Ok && m4DelParent.Count == 1, "M4 parent deleted after children removed", m4DelParent.Error);

// ---- M5: leaving the Compressed type clears ParentRecordId ----
var m5Parent = NewRec("Compressed", "M5PAR001", "Parent");
Check((await svc.SaveRecordAsync(m5Parent, "harness", "Records Manager")).Ok, "M5 compressed parent created");
var m5Child = NewRec("Compressed", "M5CHD001", "Child", m5Parent.Id);
Check((await svc.SaveRecordAsync(m5Child, "harness", "Records Manager")).Ok, "M5 compressed child filed");
var m5Edit = m5Child.Id == 0 ? null : await svc.GetRecordAsync(m5Child.Id);
if (m5Edit == null) Check(false, "M5 child type change accepted with a fresh home", "child was not created");
else
{
m5Edit.RecordType = "Case File";
m5Edit.Home = "SHELF 1"; m5Edit.HomeKind = "Location"; m5Edit.HomeRefId = shelfLoc.Id;
m5Edit.Assignee = "mtnelson"; m5Edit.AssigneeKind = "User";
var m5Res = await svc.SaveRecordAsync(m5Edit, "harness", "Records Manager");
Check(m5Res.Ok, "M5 child type change accepted with a fresh home", m5Res.Error);
var m5After = m5Child.Id == 0 ? null : await svc.GetRecordAsync(m5Child.Id);
Check(m5After != null && m5After.ParentRecordId == null && m5After.CompressedRole == null,
    "M5 leaving Compressed clears ParentRecordId and role");
Check(m5After != null && m5After.HomeKind == "Location" && m5After.Home == "SHELF 1",
    "M5 record keeps the supplied home", m5After == null ? "no row" : $"{m5After.HomeKind}/{m5After.Home}");
}

// ---- review gap: MoveItemsAsync refuses a batch containing filed children (atomically) ----
var mvFiled = NewRec("Compressed", "MVFCHD01", "Child", m5Parent.Id);
Check((await svc.SaveRecordAsync(mvFiled, "harness", "Records Manager")).Ok, "gap filed child created");
var mvPlain = (await svc.GetRecordsAsync()).First(r => r.RecordType == "Case File" && r.ParentRecordId == null);
var mvPlainHomeBefore = mvPlain.Home;
var mvGapThrew = false;
try { await svc.MoveItemsAsync("Record", new[] { mvPlain.Id, mvFiled.Id }, "BLDG CRC", "Location", 1, null, null, null, false, "harness"); }
catch (InvalidOperationException) { mvGapThrew = true; }
Check(mvGapThrew, "gap mixed batch with a filed child refused");
Check((await svc.GetRecordAsync(mvPlain.Id))!.Home == mvPlainHomeBefore,
    "gap refused batch moved nothing");

// ---- review gap: homing a record to a compressed CHILD is refused ----
var hcChild = NewRec("Compressed", "HCCHD001", "Child", m5Parent.Id);
Check((await svc.SaveRecordAsync(hcChild, "harness", "Records Manager")).Ok, "gap compressed child created");
var hcFile = await svc.SaveRecordAsync(NewRec("Case File", "HCFILE01", null, hcChild.Id), "harness", "Records Manager");
Check(!hcFile.Ok && hcFile.Error != null && hcFile.Error.Contains("compressed parent"),
    "gap filing under a compressed child refused", hcFile.Error);


// ---- review gap: NextIntAsync must ignore non-conforming legacy numbers ----
// A legacy row whose number has the right prefix+length but a non-numeric
// suffix ("R-CMP008" sorts ABOVE "R-001505" as a string) makes NextIntAsync's
// string-MAX unparseable -> it returns 1 -> every later service create fails
// with "duplicate number". Runs on its own DB so the poison cannot leak into
// the shared suite database.
{
    var dbPathN = Path.Combine(Path.GetTempPath(), $"riptest-numn-{Guid.NewGuid():N}.db");
    var optsN = new DbContextOptionsBuilder<RimDbContext>().UseSqlite($"Data Source={dbPathN}").Options;
    var factoryN = new TestFactory(optsN);
    using (var db = factoryN.CreateDbContext()) { db.Database.EnsureCreated(); SeedData.EnsureSeeded(db); }
    var svcN = new RimService(factoryN);
    using (var db = factoryN.CreateDbContext())
    {
        db.Records.Add(new RecordItem
        {
            RecordNumber = "R-CMP008", Barcode = "REC-CMP008", RecordType = "Compressed",
            CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "NUMP008", Volume = "1",
            Subject = "legacy poison row", Home = "SHELF 1", HomeKind = "Location",
            Assignee = "mtnelson", AssigneeKind = "User", State = "Active",
            CreatedUtc = DateTime.UtcNow, LastUpdatedUtc = DateTime.UtcNow, CreatedBy = "harness", LastUpdatedBy = "harness"
        });
        await db.SaveChangesAsync();
    }
    var numRec = new RecordItem { RecordType = "Case File", CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "NUMNEW1", Volume = "1", Subject = "numbering probe", Home = "SHELF 1", HomeKind = "Location", Assignee = "mtnelson", AssigneeKind = "User", State = "Active" };
    // The H3 mitigation's guarantee: a persistent collision surfaces as a
    // clean tuple, never an exception (SaveRecordAsync catches it internally).
    Exception? numEx = null;
    (bool Ok, string? Error) numRes = (false, null);
    try { numRes = await svcN.SaveRecordAsync(numRec, "harness", "Records Manager"); }
    catch (Exception ex) { numEx = ex; }
    Check(numEx == null, "gap persistent collision never throws", numEx?.Message ?? "");
    Check(numRes.Ok, "gap create succeeds despite a non-conforming legacy number", numRes.Error);
    if (numRes.Ok)
        Check(numRec.RecordNumber == "R-000006",
            "gap numbering skips the non-conforming legacy number", numRec.RecordNumber);
    try { File.Delete(dbPathN); } catch { }
}

// ---- B1: multi-generation legacy DB upgrade ----
// Simulates a database from several generations back: it HAS ParentRecordId
// (with a real filed child) but is MISSING AssigneeKind/AssigneeRefId,
// CompressedRole, and the user Barcode column. The old code ran the child-
// homing backfill before those columns were added and crashed with
// "no such column" at startup; the fix adds every column first.
{
    var dbPathB1 = Path.Combine(Path.GetTempPath(), $"riptest-b1-{Guid.NewGuid():N}.db");
    var optsB1 = new DbContextOptionsBuilder<RimDbContext>().UseSqlite($"Data Source={dbPathB1}").Options;
    int b1UserId, b1ParentId, b1ChildId;
    await using (var db = new RimDbContext(optsB1))
    {
        await db.Database.EnsureCreatedAsync();
        var u = new AppUser { UserId = "b1user", DisplayName = "B1 User", Role = "Staff", Active = true, CreatedUtc = DateTime.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        var par = new RecordItem
        {
            RecordNumber = "R-B10001", Barcode = "REC-B10001", RecordType = "Compressed",
            CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "B1PAR", Volume = "1",
            Subject = "b1 parent", Home = "SHELF 1", HomeKind = "Location",
            Assignee = "b1user", AssigneeKind = null, AssigneeRefId = null, State = "Active",
            CreatedUtc = DateTime.UtcNow, LastUpdatedUtc = DateTime.UtcNow, CreatedBy = "b1", LastUpdatedBy = "b1"
        };
        db.Records.Add(par);
        await db.SaveChangesAsync();
        var ch = new RecordItem
        {
            RecordNumber = "R-B10002", Barcode = "REC-B10002", RecordType = "Case File", ParentRecordId = par.Id,
            CaseClassification = "149", FieldOffice = "HQ", CaseNumber = "B1CHD", Volume = "1",
            Subject = "b1 child", Home = "SHELF 1", HomeKind = "Location",
            Assignee = "b1user", AssigneeKind = null, AssigneeRefId = null, State = "Active",
            CreatedUtc = DateTime.UtcNow, LastUpdatedUtc = DateTime.UtcNow, CreatedBy = "b1", LastUpdatedBy = "b1"
        };
        db.Records.Add(ch);
        await db.SaveChangesAsync();
        b1UserId = u.Id; b1ParentId = par.Id; b1ChildId = ch.Id;
        // Drop the model-created indexes that reference the columns first:
        // SQLite refuses DROP COLUMN while an index names the column.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_Records_ParentRecordId");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_Users_Barcode");
        foreach (var ddl in new[]
        {
            "ALTER TABLE Records DROP COLUMN AssigneeKind",
            "ALTER TABLE Records DROP COLUMN AssigneeRefId",
            "ALTER TABLE Records DROP COLUMN CompressedRole",
            "ALTER TABLE Users DROP COLUMN Barcode",
            "ALTER TABLE Containers DROP COLUMN AssigneeKind",
            "ALTER TABLE Containers DROP COLUMN AssigneeRefId",
        })
            await db.Database.ExecuteSqlRawAsync(ddl);
    }
    await using (var db = new RimDbContext(optsB1))
    {
        // NOTE: the M13 cheap-skip in BackfillUserBarcodes cannot see NULL
        // barcodes (Barcode is [Required], so EF turns "== null" into WHERE 0).
        // On this legacy DB the re-added Barcode column holds NULL, the
        // backfill is skipped, and BackfillRefIds then crashes materializing
        // the user row ("The data is NULL at ordinal 0"). Guard the throw so
        // the suite records it instead of aborting; the column-ordering
        // assertions below still verify the B1 fix itself.
        Exception? b1UpgEx = null;
        try { SeedData.UpgradeSchema(db); }
        catch (Exception ex) { b1UpgEx = ex; }
        Check(b1UpgEx == null, "B1 UpgradeSchema completes without crashing on a legacy DB",
            b1UpgEx == null ? "" : $"{b1UpgEx.GetType().Name}: {b1UpgEx.Message}");
        var recCols = await db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Records')").ToListAsync();
        foreach (var c in new[] { "ParentRecordId", "CompressedRole", "AssigneeKind", "AssigneeRefId" })
            Check(recCols.Contains(c), $"B1 UpgradeSchema restores Records.{c} on a legacy DB");
        var userCols = await db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Users')").ToListAsync();
        Check(userCols.Contains("Barcode"), "B1 UpgradeSchema restores Users.Barcode on a legacy DB");
        var contCols = await db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Containers')").ToListAsync();
        Check(contCols.Contains("AssigneeKind") && contCols.Contains("AssigneeRefId"),
            "B1 UpgradeSchema restores Containers assignee columns on a legacy DB");
        // Read the barcode via raw SQL: EF materialization of the NULL row throws.
        var b1Barcode = await db.Database.SqlQueryRaw<string>(
            "SELECT COALESCE(Barcode,'<null>') AS Value FROM Users WHERE Id = " + b1UserId).SingleAsync();
        Check(b1Barcode.StartsWith("USR"), "B1 legacy user got a USR barcode", b1Barcode);
        var b1Parent = await db.Records.AsNoTracking().FirstAsync(x => x.Id == b1ParentId);
        Check(b1Parent.CompressedRole == "Parent",
            "B1 legacy compressed-with-children backfilled to Parent", b1Parent.CompressedRole);
        // AssigneeKind defaulting runs before the crash; ref-id resolution
        // (BackfillRefIds) does not, so only assert the default here.
        Check(b1Parent.AssigneeKind == "User",
            "B1 legacy assignee defaulted to User", b1Parent.AssigneeKind);
        var b1Child = await db.Records.AsNoTracking().FirstAsync(x => x.Id == b1ChildId);
        Check(b1Child.HomeKind == "Record" && b1Child.HomeRefId == b1ParentId
              && b1Child.AssigneeKind == "Record" && b1Child.AssigneeRefId == b1ParentId,
            "B1 legacy filed child re-homed to the parent record",
            $"{b1Child.HomeKind}/{b1Child.AssigneeKind}");
    }
    try { File.Delete(dbPathB1); } catch { }
}

// ---- B3: DDL provider branching ----
// SQLite branch: the upgrade backfills the EF-model indexes on existing DBs.
{
    var dbPathB3 = Path.Combine(Path.GetTempPath(), $"riptest-b3-{Guid.NewGuid():N}.db");
    var optsB3 = new DbContextOptionsBuilder<RimDbContext>().UseSqlite($"Data Source={dbPathB3}").Options;
    var b3Indexes = new[] { "IX_Containers_Barcode", "IX_Locations_Barcode", "IX_Users_Barcode",
        "IX_Records_ParentRecordId", "IX_Records_Home", "IX_Containers_ParentContainerId",
        "IX_Containers_Home", "IX_Locations_ParentId" };
    await using (var db = new RimDbContext(optsB3))
    {
        await db.Database.EnsureCreatedAsync();
        // Simulate a pre-v0.13.1 database that never got the backfilled indexes.
        foreach (var ix in b3Indexes)
        {
#pragma warning disable EF1002 // fixed literal index names, never user input
            await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS {ix}");
#pragma warning restore EF1002
        }
        var before = await db.Database.SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='index'").ToListAsync();
        Check(!b3Indexes.Any(ix => before.Contains(ix)), "B3 setup dropped the backfilled indexes");
        SeedData.UpgradeSchema(db);
        var after = await db.Database.SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='index'").ToListAsync();
        foreach (var ix in b3Indexes)
            Check(after.Contains(ix), $"B3 SQLite upgrade creates {ix}");
        // Idempotent: a second run changes nothing and throws nothing.
        SeedData.UpgradeSchema(db);
        var twice = await db.Database.SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='index'").ToListAsync();
        Check(b3Indexes.All(ix => twice.Contains(ix)), "B3 SQLite upgrade is idempotent");
    }
    try { File.Delete(dbPathB3); } catch { }
}

// SQL Server branch: the T-SQL builders are private, so extract their SQL
// string literals from the IL and assert the dialect is clean T-SQL.
{
    static List<string> LdStrings(MethodInfo m)
    {
        var sizes = new Dictionary<short, int>();
        foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var oc = (System.Reflection.Emit.OpCode)f.GetValue(null)!;
            int sz = oc.OperandType switch
            {
                System.Reflection.Emit.OperandType.InlineNone => 0,
                System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar or System.Reflection.Emit.OperandType.ShortInlineBrTarget => 1,
                System.Reflection.Emit.OperandType.InlineVar => 2,
                System.Reflection.Emit.OperandType.InlineI or System.Reflection.Emit.OperandType.InlineBrTarget or System.Reflection.Emit.OperandType.InlineField or
                System.Reflection.Emit.OperandType.InlineMethod or System.Reflection.Emit.OperandType.InlineSig or System.Reflection.Emit.OperandType.InlineString or
                System.Reflection.Emit.OperandType.InlineTok or System.Reflection.Emit.OperandType.InlineType or System.Reflection.Emit.OperandType.ShortInlineR => 4,
                System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                System.Reflection.Emit.OperandType.InlineSwitch => -1,
                _ => 0,
            };
            sizes[oc.Value] = sz;
        }
        var il = m.GetMethodBody()!.GetILAsByteArray()!;
        var list = new List<string>();
        int i = 0;
        while (i < il.Length)
        {
            short code = il[i++];
            if (code == 0xFE) code = (short)(0xFE00 | il[i++]);
            if (code == 0x72) // ldstr: metadata token for the string literal
            {
                int token = BitConverter.ToInt32(il, i);
                try { var lit = m.Module.ResolveString(token); if (lit != null) list.Add(lit); } catch { }
            }
            int sz = sizes.TryGetValue(code, out var s) ? s : 0;
            if (sz == -1) // InlineSwitch: count followed by N 4-byte targets
            {
                int n = BitConverter.ToInt32(il, i);
                i += 4 + 4 * n;
            }
            else i += sz;
        }
        return list;
    }
    var serverBuilder = typeof(SeedData).GetMethod("CreateMissingTablesSqlServer", BindingFlags.NonPublic | BindingFlags.Static);
    var indexBuilder = typeof(SeedData).GetMethod("CreateIndexSqlServer", BindingFlags.NonPublic | BindingFlags.Static);
    Check(serverBuilder != null && indexBuilder != null, "B3 T-SQL builders found via reflection");
    var tsql = string.Join("\n", LdStrings(serverBuilder!).Concat(LdStrings(indexBuilder!)));
    Check(tsql.Length > 500, "B3 extracted T-SQL literals from the builders", $"chars={tsql.Length}");
    foreach (var sqliteism in new[] { "AUTOINCREMENT", "PRAGMA", "CREATE TABLE IF NOT EXISTS",
        "CREATE INDEX IF NOT EXISTS", "CREATE UNIQUE INDEX IF NOT EXISTS", "LIMIT" })
        Check(!tsql.Contains(sqliteism, StringComparison.OrdinalIgnoreCase),
            $"B3 T-SQL contains no SQLite-ism '{sqliteism}'");
    Check(tsql.Contains("IDENTITY(1,1)"), "B3 T-SQL uses IDENTITY keys");
    Check(tsql.Contains("sys.indexes"), "B3 T-SQL guards index creation via sys.indexes");
    Check(tsql.Contains("OBJECT_ID"), "B3 T-SQL guards table creation via OBJECT_ID");
    Check(tsql.Contains("NVARCHAR"), "B3 T-SQL uses NVARCHAR types");
}

// Shift+click range selection (v0.14.1): pure helper, all data grids.
{
    var gridRows = new List<string> { "a", "b", "c", "d", "e" };
    var cmp = StringComparer.Ordinal;
    var fwd = Rim.Components.Shared.GridSelection.Range(gridRows, "b", "d", cmp);
    Check(fwd != null && fwd.SetEquals(new[] { "b", "c", "d" }),
        "shift+click selects anchor..target range", $"got [{string.Join(",", fwd ?? new HashSet<string>())}]");
    var rev = Rim.Components.Shared.GridSelection.Range(gridRows, "d", "b", cmp);
    Check(rev != null && rev.SetEquals(new[] { "b", "c", "d" }),
        "range works when the click precedes the anchor");
    var single = Rim.Components.Shared.GridSelection.Range(gridRows, "c", "c", cmp);
    Check(single != null && single.SetEquals(new[] { "c" }),
        "anchor == target selects one row");
    Check(Rim.Components.Shared.GridSelection.Range(gridRows, null, "d", cmp) is null,
        "no anchor -> null (caller falls back to single-select)");
    Check(Rim.Components.Shared.GridSelection.Range(gridRows, "zzz", "d", cmp) is null,
        "anchor scrolled out -> null (caller falls back to single-select)");
    Check(Rim.Components.Shared.GridSelection.Range(new List<string>(), "a", "b", cmp) is null,
        "empty row window -> null");
}

Console.WriteLine($"--- {pass} passed, {fail} failed ---");

// ---- CM 24.3 third-party integrations (v0.14.0) ----
// LabelPdfService: PDFsharp (the same PDF library Content Manager bundles)
// renders inventory labels with real ZXing Code 128 barcodes.
//
// v0.14.1 regression: PDFsharp 6.x ships with NO default font resolver, so
// new XFont(...) throws InvalidOperationException on every machine unless
// the host installs one. v0.14.0 only worked in this harness because of a
// test-only resolver; production crashed on the first PDF click. The tests
// below therefore use the PRODUCTION resolver (PdfSharpFontResolver), which
// finds DejaVu/Liberation/Noto on Linux and Arial on Windows.
{
    PdfSharp.Fonts.GlobalFontSettings.FontResolver = new PdfSharpFontResolver();
    var labelSvc = new LabelPdfService { FontFamily = "Noto Sans" };
    var labels = new List<LabelItem>
    {
        new("R-000001", "Case File / 149", "REC000001"),
        new("R-000002", "Case File / 149", "REC000002"),
    };
    var pdf = labelSvc.RenderLabels(labels, "harness", new DateTime(2026, 10, 2, 12, 0, 0));
    Check(pdf.Length > 1000 && pdf[0] == '%' && pdf[1] == 'P' && pdf[2] == 'D' && pdf[3] == 'F',
        "label PDF has %PDF magic bytes", $"len={pdf.Length}");
    using var ms = new MemoryStream(pdf);
    using var reopened = PdfSharp.Pdf.IO.PdfReader.Open(ms, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
    Check(reopened.PageCount == 2, "label PDF has one page per label", $"pages={reopened.PageCount}");
    Check(Math.Abs(reopened.Pages[0].Width.Point - LabelPdfService.LabelWidthPt) < 0.5 &&
          Math.Abs(reopened.Pages[0].Height.Point - LabelPdfService.LabelHeightPt) < 0.5,
        "label page is 4x2in (288x144pt)");

    // Production configuration: default FontFamily "Arial" through the
    // production resolver. Must not throw (v0.14.0 crash).
    var prodSvc = new LabelPdfService();
    byte[] prodPdf = Array.Empty<byte>();
    Exception? prodEx = null;
    try { prodPdf = prodSvc.RenderLabels(labels, "harness", new DateTime(2026, 10, 2, 12, 0, 0)); }
    catch (Exception ex) { prodEx = ex; }
    Check(prodEx is null && prodPdf.Length > 1000,
        "production resolver renders labels with default Arial (no throw)",
        prodEx is null ? $"len={prodPdf.Length}" : prodEx.GetType().Name + ": " + prodEx.Message.Split('\n')[0]);

    var svg = LabelPdfService.BarcodeSvg("REC000001");
    Check(svg.Contains("<svg") && svg.Contains("<rect"), "barcode SVG preview renders vector bars");
    Check(svg.Contains("width=\"100%\""), "barcode SVG scales to its container (no 600px overflow)");
    Check(LabelPdfService.BarcodeSvg("") == "", "empty barcode -> empty SVG, no exception");
    var pdfEmpty = labelSvc.RenderLabels(new List<LabelItem> { new("T", "L", "") }, "harness");
    Check(pdfEmpty.Length > 500, "label PDF renders with empty barcode (text fallback)");
}

// ---- FastReport template-driven labels (v0.15.0) ----
// Layout lives in Reports/Label4x2.frx (editable in FastReport Designer
// Community Edition); the engine binds data and computes geometry, and
// FastReportLabelService redraws the prepared pages as vector PDF via
// PDFsharp so barcodes stay sharp. (The open-source FastReport PDF export
// rasterizes pages to bitmaps, which would soften thermal-printer output.)
{
    var frxPath = Path.Combine(AppContext.BaseDirectory, "Reports", "Label4x2.frx");
    Check(File.Exists(frxPath), "label template ships next to the app", frxPath);

    // Template structure: 4x2in page, Code 128 barcode object, bound fields.
    using var tpl = new FastReport.Report();
    tpl.Load(frxPath);
    var tplPage = (FastReport.ReportPage)tpl.Pages[0];
    Check(Math.Abs(tplPage.PaperWidth - 384) < 0.5 && Math.Abs(tplPage.PaperHeight - 192) < 0.5,
        "template page is 4x2in (384x192 units)", $"{tplPage.PaperWidth}x{tplPage.PaperHeight}");
    var tplBarcode = tplPage.AllObjects.OfType<FastReport.Barcode.BarcodeObject>().FirstOrDefault();
    Check(tplBarcode != null, "template contains a barcode object");
    Check(tplBarcode != null && tplBarcode.Expression == "[Labels.Barcode]",
        "barcode object bound to Labels.Barcode", tplBarcode?.Expression);
    Check(tplBarcode != null && tplBarcode.Barcode.GetType().Name.Contains("128"),
        "barcode symbology is Code 128", tplBarcode?.Barcode.GetType().Name);
    Check(tplBarcode != null && !tplBarcode.ShowText,
        "barcode object hides built-in text (separate text object renders it)");

    // End-to-end through the real engine: data binding, expression
    // evaluation, vector PDF output.
    var frSvc = new FastReportLabelService();
    var frLabels = new List<LabelItem>
    {
        new("R-000001", "Case File / 149", "REC000001"),
        new("R-000002", "Case File / 149", "REC000002"),
    };
    var frPdf = frSvc.RenderLabels(frLabels, "harness", new DateTime(2026, 10, 2, 12, 0, 0));
    Check(frPdf.Length > 1000 && frPdf[0] == '%' && frPdf[1] == 'P' && frPdf[2] == 'D' && frPdf[3] == 'F',
        "template-driven label PDF has %PDF magic bytes", $"len={frPdf.Length}");
    using var frMs = new MemoryStream(frPdf);
    using var frReopened = PdfSharp.Pdf.IO.PdfReader.Open(frMs, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
    Check(frReopened.PageCount == 2, "template PDF has one page per label", $"pages={frReopened.PageCount}");
    Check(Math.Abs(frReopened.Pages[0].Width.Point - 288) < 0.5 &&
          Math.Abs(frReopened.Pages[0].Height.Point - 144) < 0.5,
        "template PDF page is 4x2in (288x144pt)");

    // Data actually reaches the page: prepare directly and inspect the
    // evaluated objects (title text, footer parameters, barcode value).
    using var rep2 = new FastReport.Report();
    rep2.Load(frxPath);
    var dt = new System.Data.DataTable("Labels");
    dt.Columns.Add("Title", typeof(string));
    dt.Columns.Add("Line2", typeof(string));
    dt.Columns.Add("Barcode", typeof(string));
    dt.Rows.Add("R-000001", "Case File / 149", "REC000001");
    rep2.RegisterData(dt, "Labels");
    rep2.SetParameterValue("PrintedBy", "harness");
    rep2.SetParameterValue("PrintedAt", "2026-10-02 12:00");
    Check(rep2.Prepare(), "report prepares against bound data");
    var pg0 = rep2.PreparedPages.GetPage(0);
    var texts = pg0.AllObjects.OfType<FastReport.TextObject>().Select(t => t.Text).ToList();
    Check(texts.Any(t => t.Contains("R-000001")), "template binds Labels.Title");
    Check(texts.Any(t => t.Contains("Printed 2026-10-02 12:00 by harness")),
        "footer parameters evaluated", string.Join(" | ", texts));
    var bc0 = pg0.AllObjects.OfType<FastReport.Barcode.BarcodeObject>().FirstOrDefault();
    Check(bc0 != null && bc0.Text == "REC000001", "barcode object evaluated to row value", bc0?.Text);

    // Missing template -> clear FileNotFoundException, not a null-ref.
    var missingSvc = new FastReportLabelService(Path.Combine(Path.GetTempPath(), "no-such-template.frx"));
    Exception? missingEx = null;
    try { missingSvc.RenderLabels(frLabels, "harness"); } catch (Exception ex) { missingEx = ex; }
    Check(missingEx is FileNotFoundException, "missing template throws FileNotFoundException",
        missingEx?.GetType().Name ?? "no exception");
}

// ZXing encode -> decode round-trip through raw pixel data (no image files).
{
    var writer = new ZXing.BarcodeWriterPixelData
    {
        Format = ZXing.BarcodeFormat.CODE_128,
        Options = new ZXing.Common.EncodingOptions { Width = 600, Height = 120, Margin = 0, PureBarcode = true }
    };
    var pd = writer.Write("REC000042");
    Check(pd is { Width: 600, Height: 120 }, "ZXing encodes Code 128 pixel data");
    var rgb = new byte[pd.Width * pd.Height * 3];
    for (int p = 0; p < pd.Width * pd.Height; p++)
    {
        // Bars are pure black/white; channel order is irrelevant here.
        rgb[p * 3] = pd.Pixels[p * 4];
        rgb[p * 3 + 1] = pd.Pixels[p * 4 + 1];
        rgb[p * 3 + 2] = pd.Pixels[p * 4 + 2];
    }
    var lum = new ZXing.RGBLuminanceSource(rgb, pd.Width, pd.Height,
        ZXing.RGBLuminanceSource.BitmapFormat.RGB24);
    var decoded = new ZXing.BarcodeReaderGeneric().Decode(lum);
    Check(decoded?.Text == "REC000042", "ZXing decode round-trips encoded barcode", $"got '{decoded?.Text}'");
}

Console.WriteLine($"--- {pass} passed, {fail} failed ---");
try { File.Delete(dbPath); File.Delete(dbPath + "-shm"); File.Delete(dbPath + "-wal"); } catch { }
return fail == 0 ? 0 : 1;

sealed class TestFactory(DbContextOptions<RimDbContext> opts) : IDbContextFactory<RimDbContext>
{
    public RimDbContext CreateDbContext() => new(opts);
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
