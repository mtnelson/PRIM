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

public partial class AdvancedSearch : ComponentBase, IDisposable
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;

    private class AdvRow
    {
        public string Field { get; set; } = "CaseNumber";
        public string Op { get; set; } = "Contains";
        public string Value { get; set; } = "";
    }

    protected override void OnInitialized()
    {
        Hotkeys.PushScope("advanced");
        Hotkeys.Register("advanced", "F9", RunSearch);
    }

    protected override async Task OnInitializedAsync()
    {
        _tabs = await Prim.GetOpenSessionsAsync(App.CurrentUserId, "advanced");
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Prim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "advanced",
                Title = "Advanced search", CriteriaJson = "",
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.FirstOrDefault();
        if (_activeTab != null)
        {
            _tabState = _activeTab.ToTabState();
            // Restore the most-recent search: re-run it server-side.
            if (TryParseCriteria(_activeTab.CriteriaJson))
                await ExecuteSearchAsync(restore: true);
        }
    }

    public void Dispose()
    {
        Hotkeys.UnregisterScope("advanced");
        // Best-effort: persist the active tab's state when leaving the page.
        _ = SaveActiveTabAsync().ContinueWith(t => { var _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // Search-session tabs: one persisted descriptor per open tab. Switching
    // tabs restores the criteria and re-runs the search server-side.
    private List<SearchSession> _tabs = new();
    private SearchSession? _activeTab;
    private SearchTabState? _tabState;

    private async Task ActivateTab(SearchSession tab)
    {
        if (_activeTab?.Id == tab.Id) return;
        await SaveActiveTabAsync();
        _activeTab = tab;
        _tabState = tab.ToTabState();
        if (TryParseCriteria(tab.CriteriaJson))
            await ExecuteSearchAsync(restore: true);
        else
        {
            _rows = new() { new AdvRow() };
            _logic = "AND";
            _searched = false;
        }
    }

    private async Task NewTab()
    {
        await SaveActiveTabAsync();
        var (ok, err, s) = await Prim.CreateSessionAsync(new SearchSession
        {
            OwnerUserId = App.CurrentUserId, PageKind = "advanced",
            Title = $"Advanced {_tabs.Count + 1}", CriteriaJson = "",
        });
        if (!ok || s == null) { Snackbar.Add(err ?? "Could not open a tab.", Severity.Warning); return; }
        _tabs.Add(s);
        _activeTab = s;
        _tabState = s.ToTabState();
        _rows = new() { new AdvRow() };
        _logic = "AND";
        _criteria = new();
        _searched = false;
    }

    private async Task CloseTab(SearchSession tab)
    {
        await Prim.DeleteSessionAsync(tab.Id, App.CurrentUserId);
        _tabs.RemoveAll(t => t.Id == tab.Id);
        if (_activeTab?.Id != tab.Id) return;
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Prim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "advanced",
                Title = "Advanced search", CriteriaJson = "",
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.OrderByDescending(t => t.LastUsedUtc).FirstOrDefault();
        if (_activeTab == null) return;
        _tabState = _activeTab.ToTabState();
        if (TryParseCriteria(_activeTab.CriteriaJson))
            await ExecuteSearchAsync(restore: true);
        else
        {
            _rows = new() { new AdvRow() };
            _logic = "AND";
            _searched = false;
        }
    }

    private Task OnTabStateChanged() => SaveActiveTabAsync();

    private async Task SaveActiveTabAsync()
    {
        if (_activeTab == null || _tabState == null) return;
        _activeTab.CriteriaJson = SerializeCriteria();
        _activeTab.Title = DeriveTitle();
        _activeTab.ApplyTabState(_tabState);
        await Prim.SaveSessionAsync(_activeTab);
    }

    private string DeriveTitle()
    {
        var first = _rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Value));
        if (first == null) return "Advanced search";
        var label = _advFields.FirstOrDefault(f => f.Key == first.Field).Label ?? first.Field;
        var t = $"{label} {first.Op.ToLower()} {first.Value}".Trim();
        return t.Length <= 40 ? t : t[..39] + "…";
    }

    private string SerializeCriteria()
    {
        var rows = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => new { field = r.Field, op = r.Op, value = r.Value });
        return System.Text.Json.JsonSerializer.Serialize(new { logic = _logic, rows });
    }

    private bool TryParseCriteria(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            _logic = root.GetProperty("logic").GetString() ?? "AND";
            _rows = root.GetProperty("rows").EnumerateArray()
                .Select(e => new AdvRow
                {
                    Field = e.GetProperty("field").GetString() ?? "CaseNumber",
                    Op = e.GetProperty("op").GetString() ?? "Contains",
                    Value = e.GetProperty("value").GetString() ?? "",
                })
                .Where(r => r.Value != null)
                .ToList();
            if (_rows.Count == 0) _rows.Add(new AdvRow());
            return _rows.Any(r => !string.IsNullOrWhiteSpace(r.Value));
        }
        catch
        {
            return false;
        }
    }

    private List<AdvRow> _rows = new() { new AdvRow() };
    private string _logic = "AND";
    private bool _searched = false;
    private bool _searching = false;
    private string _resultsTitle = "";
    private List<(string Field, string Op, string Value)> _criteria = new();
    private RecordGrid? _grid;

    // Server-side provider: filtering and paging both happen in the database;
    // the grid fetches 500-row chunks as the user scrolls.
    private Task<GridPageResult<RecordItem>> ProvideSearchResults(GridPageRequest req) =>
        Prim.AdvancedSearchRecordsPageAsync(_criteria, _logic, req);

    private List<(string Key, string Label)> _advFields = new()
    {
        ("RecordNumber","Record Number"), ("RecordType","Record Type"),
        ("CaseClassification","Case Classification"), ("FieldOffice","Field Office"),
        ("CaseNumber","Case Number"), ("SubfileId","Subfile ID"),
        ("Volume","Volume"), ("SerialStart","Serial Start"), ("SerialEnd","Serial End"),
        ("Barcode","Barcode"), ("Home","Home"), ("Assignee","Assignee"),
        ("Subject","Subject"), ("State","State"),
    };

    private async Task RunSearch()
    {
        var rows = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (rows.Count == 0) { Snackbar.Add("Enter at least one criterion.", Severity.Info); return; }
        await ExecuteSearchAsync(restore: false);
        App.Log("Advanced search", _resultsTitle);
    }

    // Commits the criteria editor and runs the search. On restore (tab
    // switch) the grid picks up the new TabState and re-fetches by itself;
    // on a manual run the existing grid is reset explicitly.
    private async Task ExecuteSearchAsync(bool restore)
    {
        _criteria = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => (r.Field, r.Op, r.Value)).ToList();
        if (_criteria.Count == 0) { _searched = false; return; }
        _searching = true;
        try
        {
            var count = await Prim.AdvancedSearchRecordsCountAsync(_criteria, _logic);
            _resultsTitle = $"{count} record(s) found";
            _searched = true;
            if (!restore && _grid != null) await _grid.ResetAsync();
            await SaveActiveTabAsync();
        }
        finally { _searching = false; }
    }

    private async Task<bool> EditRecord(RecordItem r)
    {
        var d = await DialogService.ShowAsync<RecordDialog>("Edit Record",
            new DialogParameters { ["Model"] = r },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        return (await d.Result) is { Canceled: false };
    }

    private async Task<bool> DeleteRecords(List<int> ids)
    {
        var d = await DialogService.ShowAsync<DeleteDialog>("Delete Records",
            new DialogParameters { ["Ids"] = ids }, new DialogOptions { MaxWidth = MaxWidth.Medium });
        return (await d.Result) is { Canceled: false };
    }
}
