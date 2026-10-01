using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using MudBlazor;
using Rim.Components.Dialogs;
using Rim.Components.Layout;
using Rim.Components.Shared;
using Rim.Data;
using Rim.Services;

namespace Rim.Components.Dialogs;

public partial class AuditDialog : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public IDbContextFactory<RimDbContext> DbFactory { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public string Kind { get; set; } = "";
    [Parameter] public int Id { get; set; }
    [Parameter] public string Label { get; set; } = "";

    private List<AuditEvent> _events = new();
    private string? _error;

    // M14 manifest: export file name -> the set of "Kind:ObjectId" keys it
    // contains. Each export file is scanned fully exactly once per process to
    // build its entry; afterwards opening an item's audit log reads ONLY the
    // files that can contain rows for that item. Per-open work stays bounded
    // no matter how many audit-archive-*.jsonl.gz files accumulate.
    // (RimService.GetAuditAsync line-scans every file on every call — the
    // hot/archive tiers below replicate its queries, and the file-tier row
    // mapping replicates its Id mapping; keep in sync if that changes.)
    private static readonly ConcurrentDictionary<string, HashSet<string>> _manifest = new();

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _events = await LoadAuditAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private static string Key(string kind, int id) => $"{kind}:{id}";

    private async Task<List<AuditEvent>> LoadAuditAsync()
    {
        List<AuditEvent> hot;
        List<AuditEvent> archived;
        using (var db = DbFactory.CreateDbContext())
        {
            hot = await db.AuditEvents
                .Where(a => a.ObjectKind == Kind && a.ObjectId == Id).ToListAsync();
            archived = (await db.ArchivedAuditEvents
                .Where(a => a.ObjectKind == Kind && a.ObjectId == Id).ToListAsync())
                .Select(a => new AuditEvent
                {
                    Id = -a.Id, ObjectKind = a.ObjectKind, ObjectId = a.ObjectId,
                    ObjectLabel = a.ObjectLabel, Action = a.Action, FieldName = a.FieldName,
                    OldValue = a.OldValue, NewValue = a.NewValue, Actor = a.Actor,
                    TimestampUtc = a.TimestampUtc
                }).ToList();
        }

        var all = hot.Concat(archived).ToList();
        var want = Key(Kind, Id);
        var files = await Rim.GetAuditExportFilesAsync();
        // Drop manifest entries for files that no longer exist.
        var live = files.Select(f => f.FileName).ToHashSet();
        foreach (var stale in _manifest.Keys.Where(k => !live.Contains(k)).ToList())
            _manifest.TryRemove(stale, out _);
        foreach (var f in files)
        {
            // Build the manifest entry once per file (full read, keys only).
            // Concurrent duplicate builds are idempotent (same content).
            // The build read is reused when the file turns out to be relevant.
            List<AuditEventArchive> rows;
            if (!_manifest.TryGetValue(f.FileName, out var keys))
            {
                rows = await Rim.ReadAuditExportAsync(f.FileName, int.MaxValue);
                keys = rows.Select(r => Key(r.ObjectKind, r.ObjectId)).ToHashSet();
                _manifest[f.FileName] = keys;
            }
            else
            {
                rows = null!;
            }
            if (!keys.Contains(want)) continue; // this file cannot hold the item's rows
            rows ??= await Rim.ReadAuditExportAsync(f.FileName, int.MaxValue);
            foreach (var r in rows)
            {
                if (r.ObjectKind != Kind || r.ObjectId != Id) continue;
                all.Add(new AuditEvent
                {
                    Id = -r.Id - 1_000_000_000, ObjectKind = r.ObjectKind, ObjectId = r.ObjectId,
                    ObjectLabel = r.ObjectLabel, Action = r.Action, FieldName = r.FieldName,
                    OldValue = r.OldValue, NewValue = r.NewValue, Actor = r.Actor,
                    TimestampUtc = r.TimestampUtc
                });
            }
        }
        return all.OrderByDescending(a => a.TimestampUtc).ThenByDescending(a => a.Id).ToList();
    }
}
