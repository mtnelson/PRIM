using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using MudBlazor;
using Prim.Components.Dialogs;
using Prim.Components.Layout;
using Prim.Components.Shared;
using Prim.Data;
using Prim.Services;

namespace Prim.Components.Pages;

public partial class Admin : ComponentBase, IDisposable
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;

    private List<RecordItem> _deleted = new();
    // Deleted rows rendered with every record grid column, plus the
    // deletion-specific fields (reason, merged-into, deleted by).
    private List<DeletedRow> _deletedRows = new();
    private List<(string Key, string Label)> _deletedCols = new();
    private string _announcement = "";
    private List<SavedSearch> _searches = new();

    private record DeletedRow(RecordItem Item, Dictionary<string, string> Cells);

    protected override void OnInitialized()
    {
        Hotkeys.PushScope("admin");
        Hotkeys.Register("admin", "F9", Refresh);
    }

    public void Dispose() => Hotkeys.UnregisterScope("admin");

    private async Task Refresh() { await Load(); Snackbar.Add("Admin data refreshed.", Severity.Info); }

    protected override async Task OnInitializedAsync() => await Load();

    private async Task Load()
    {
        _deleted = await Prim.GetRecordsAsync(includeDeleted: true);
        _deleted = _deleted.Where(r => r.Deleted).ToList();
        _deletedCols = GridColumns.RecordColumns();
        _deletedCols.Add(("DeleteReason", "Reason"));
        _deletedCols.Add(("MergedIntoBarcode", "Merged Into"));
        _deletedCols.Add(("LastUpdatedBy", "Deleted By"));
        _deletedRows = _deleted.Select(r =>
        {
            var cells = GridColumns.RecordRow(r);
            cells["DeleteReason"] = r.DeleteReason ?? "";
            cells["MergedIntoBarcode"] = r.MergedIntoBarcode ?? "";
            cells["LastUpdatedBy"] = r.LastUpdatedBy ?? "";
            return new DeletedRow(r, cells);
        }).ToList();
        _announcement = await Prim.GetAnnouncementAsync() ?? "";
        _searches = await Prim.GetSavedSearchesAsync(App.CurrentUserId);
        await LoadRetention();
        StateHasChanged();
    }

    private async Task Restore(RecordItem r)
    {
        await Prim.RestoreRecordsAsync(new[] { r.Id }, App.CurrentUserId);
        Snackbar.Add($"Restored {r.RecordNumber}.", Severity.Success);
        App.Log("Restored record", r.RecordNumber);
        await Load();
    }

    private async Task SaveAnnouncement()
    {
        await Prim.SetAnnouncementAsync(_announcement, !string.IsNullOrWhiteSpace(_announcement), App.CurrentUserId);
        App.ActiveAnnouncement = string.IsNullOrWhiteSpace(_announcement) ? null : _announcement;
        Snackbar.Add("Announcement updated.", Severity.Success);
        App.Log("Updated announcement", "");
    }

    private async Task ClearAnnouncement()
    {
        _announcement = "";
        await SaveAnnouncement();
    }

    private async Task DeleteSearch(SavedSearch s)
    {
        await Prim.DeleteSavedSearchAsync(s.Id);
        await Load();
    }

    private bool _seeding;

    private async Task SeedTestData()
    {
        var ok = await DialogService.ShowMessageBox("Generate test records",
            "Insert 1,500 test records into this database? They can be deleted afterwards via the Records grid.",
            yesText: "Generate", cancelText: "Cancel");
        if (ok != true) return;
        _seeding = true;
        try
        {
            var numbers = await Prim.SeedTestRecordsAsync(1500, App.CurrentUserId);
            Snackbar.Add($"Generated {numbers.Count} test records.", Severity.Success);
            App.LogItems("Generated test records", numbers);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Generation failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _seeding = false;
        }
    }

    // ---------------- audit retention ----------------
    private PrimService.AuditStats _auditStats = new(0, 0, 0, 0, 0, "");
    private List<PrimService.AuditExportInfo> _exportFiles = new();
    private string? _retentionOp; // "archive" | "export" | null
    private bool RetentionBusy => _retentionOp != null;

    private async Task LoadRetention()
    {
        _auditStats = await Prim.GetAuditStatsAsync();
        _exportFiles = await Prim.GetAuditExportFilesAsync();
    }

    private async Task ArchiveNow()
    {
        _retentionOp = "archive";
        try
        {
            var moved = await Prim.ArchiveAuditIfNeededAsync();
            Snackbar.Add(moved == 0
                ? "Hot audit table is under its row cap — nothing archived."
                : $"Archived {moved:N0} audit rows.", Severity.Success);
            App.Log("Ran audit archival", moved == 0 ? "no-op" : $"{moved:N0} rows");
            await LoadRetention();
        }
        finally { _retentionOp = null; }
    }

    private async Task ExportNow()
    {
        _retentionOp = "export";
        try
        {
            var path = await Prim.ExportAuditArchiveIfNeededAsync();
            Snackbar.Add(path == null
                ? "Archive table is under its row cap — nothing exported."
                : $"Exported archive to {Path.GetFileName(path)}.", Severity.Success);
            App.Log("Exported audit archive", path == null ? "no-op" : Path.GetFileName(path));
            await LoadRetention();
        }
        finally { _retentionOp = null; }
    }

    private async Task ViewExport(PrimService.AuditExportInfo f)
    {
        var parms = new DialogParameters { ["FileName"] = f.FileName };
        await DialogService.ShowAsync<ArchiveFileDialog>("Audit archive file", parms);
    }

    private static string FormatBytes(long b)
        => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:F1} KB" : $"{b / 1024.0 / 1024:F1} MB";
}
