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

public partial class Records : ComponentBase, IDisposable
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    private RecordGrid? _grid;
    private string _filter = "";

    // Search-session tabs: one persisted descriptor per open tab. Switching
    // tabs hands the grid a new SearchTabState, which re-runs the query
    // server-side from the first 500-row chunk.
    private List<SearchSession> _tabs = new();
    private SearchSession? _activeTab;
    private SearchTabState? _tabState;

    private Task<GridPageResult<RecordItem>> ProvideRecords(GridPageRequest req)
        => Prim.GetRecordsPageAsync(req with { Filter = _filter });

    private async Task OnFilterChanged(string v)
    {
        _filter = v;
        if (_grid != null) await _grid.ResetAsync();
        await SaveActiveTabAsync();
    }

    protected override void OnInitialized()
    {
        Hotkeys.PushScope("records");
        Hotkeys.Register("records", "Ctrl+N", NewRecord);
        Hotkeys.Register("records", "F9", Refresh);
        Hotkeys.Register("records", "Ctrl+F", async () => await PrimJs.TryInvokeVoidAsync(JS, "prim.focus", "#records-filter input"));
    }

    protected override async Task OnInitializedAsync()
    {
        _tabs = await Prim.GetOpenSessionsAsync(App.CurrentUserId, "records");
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Prim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "records",
                Title = "All records", Filter = "",
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.FirstOrDefault();
        if (_activeTab != null)
        {
            _filter = _activeTab.Filter;
            _tabState = _activeTab.ToTabState();
        }
    }

    public void Dispose()
    {
        Hotkeys.UnregisterScope("records");
        // Best-effort: persist the active tab's state when leaving the page.
        _ = SaveActiveTabAsync().ContinueWith(t => { var _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task ActivateTab(SearchSession tab)
    {
        if (_activeTab?.Id == tab.Id) return;
        await SaveActiveTabAsync();
        _activeTab = tab;
        _filter = tab.Filter;
        _tabState = tab.ToTabState();
    }

    private async Task NewTab()
    {
        await SaveActiveTabAsync();
        var (ok, err, s) = await Prim.CreateSessionAsync(new SearchSession
        {
            OwnerUserId = App.CurrentUserId, PageKind = "records",
            Title = $"Search {_tabs.Count + 1}", Filter = "",
        });
        if (!ok || s == null) { Snackbar.Add(err ?? "Could not open a tab.", Severity.Warning); return; }
        _tabs.Add(s);
        _activeTab = s;
        _filter = "";
        _tabState = s.ToTabState();
    }

    private async Task CloseTab(SearchSession tab)
    {
        await Prim.DeleteSessionAsync(tab.Id, App.CurrentUserId);
        _tabs.RemoveAll(t => t.Id == tab.Id);
        if (_activeTab?.Id == tab.Id)
            await ActivateMostRecentAsync("records", "All records", "");
    }

    // After a close, activates the most-recently-used remaining tab, creating
    // a fresh default tab when none remain.
    private async Task ActivateMostRecentAsync(string pageKind, string defaultTitle, string defaultFilter)
    {
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Prim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = pageKind,
                Title = defaultTitle, Filter = defaultFilter,
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.OrderByDescending(t => t.LastUsedUtc).FirstOrDefault();
        if (_activeTab != null)
        {
            _filter = _activeTab.Filter;
            _tabState = _activeTab.ToTabState();
        }
    }

    private Task OnTabStateChanged() => SaveActiveTabAsync();

    private async Task SaveActiveTabAsync()
    {
        if (_activeTab == null || _tabState == null) return;
        _activeTab.Filter = _filter;
        _activeTab.Title = string.IsNullOrWhiteSpace(_filter) ? "All records" : Truncate(_filter, 40);
        _activeTab.ApplyTabState(_tabState);
        await Prim.SaveSessionAsync(_activeTab);
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s[..(n - 1)] + "…";

    private async Task Refresh() { await OnGridChanged(); Snackbar.Add("Records refreshed.", Severity.Info); }

    private async Task OnGridChanged()
    {
        if (_grid != null) await _grid.ResetAsync();
    }

    private async Task NewRecord()
    {
        var d = await DialogService.ShowAsync<RecordDialog>("New Record",
            new DialogParameters { ["Model"] = new RecordItem() },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        var res = await d.Result;
        if (res is { Canceled: false }) await OnGridChanged();
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
