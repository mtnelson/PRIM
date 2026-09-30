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

public partial class Containers : ComponentBase, IDisposable
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    private ContainerGrid? _grid;
    private string _filter = "";
    // Search-session tabs: one persisted descriptor per open tab (see Records.razor).
    private List<SearchSession> _tabs = new();
    private SearchSession? _activeTab;
    private SearchTabState? _tabState;


    private Task<GridPageResult<Container>> ProvideContainers(GridPageRequest req)
        => Prim.GetContainersPageAsync(req with { Filter = _filter });

    private async Task OnFilterChanged(string v)
    {
        _filter = v;
        if (_grid != null) await _grid.ResetAsync();
        await SaveActiveTabAsync();
    }

    protected override void OnInitialized()
    {
        Hotkeys.PushScope("containers");
        Hotkeys.Register("containers", "Ctrl+N", NewContainer);
        Hotkeys.Register("containers", "F9", Refresh);
        Hotkeys.Register("containers", "Ctrl+F", async () => await PrimJs.TryInvokeVoidAsync(JS, "prim.focus", "#containers-filter input"));
    }

    protected override async Task OnInitializedAsync()
    {
        _tabs = await Prim.GetOpenSessionsAsync(App.CurrentUserId, "containers");
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Prim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "containers",
                Title = "All containers", Filter = "",
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
        Hotkeys.UnregisterScope("containers");
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
            OwnerUserId = App.CurrentUserId, PageKind = "containers",
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
            await ActivateMostRecentAsync();
    }

    private async Task ActivateMostRecentAsync()
    {
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Prim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "containers",
                Title = "All containers", Filter = "",
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
        _activeTab.Title = string.IsNullOrWhiteSpace(_filter) ? "All containers" : Truncate(_filter, 40);
        _activeTab.ApplyTabState(_tabState);
        await Prim.SaveSessionAsync(_activeTab);
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s[..(n - 1)] + "\u2026";

    private async Task Refresh() { await OnGridChanged(); Snackbar.Add("Containers refreshed.", Severity.Info); }

    private async Task OnGridChanged()
    {
        if (_grid != null) await _grid.ResetAsync();
    }

    private async Task NewContainer()
    {
        var d = await DialogService.ShowAsync<ContainerDialog>("New Container",
            new DialogParameters { ["Model"] = new Container() },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        var res = await d.Result;
        if (res is { Canceled: false }) await OnGridChanged();
    }

    private async Task<bool> EditContainer(Container c)
    {
        var d = await DialogService.ShowAsync<ContainerDialog>("Edit Container",
            new DialogParameters { ["Model"] = c },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        return (await d.Result) is { Canceled: false };
    }

    private async Task<bool> DeleteContainers(List<int> ids)
    {
        bool? ok = await DialogService.ShowMessageBox("Delete containers?",
            $"Delete {ids.Count} container(s)? This cannot be undone.", yesText: "Delete", cancelText: "Cancel");
        if (ok != true) return false;
        try
        {
            var n = await Prim.DeleteContainersAsync(ids, App.CurrentUserId);
            Snackbar.Add($"Deleted {n} container(s).", Severity.Success);
            var names = new List<string>();
            foreach (var id in ids) names.Add(await Prim.GetObjectLabelAsync("Container", id));
            App.LogItems("Deleted containers", names);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
            return false;
        }
    }
}
