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

public partial class Locations : ComponentBase, IDisposable
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;

    private LocationGrid? _grid;
    // Search-session tabs: one persisted descriptor per open tab (see Records.razor).
    private List<SearchSession> _tabs = new();
    private SearchSession? _activeTab;
    private SearchTabState? _tabState;


    private Task<GridPageResult<Location>> ProvideLocations(GridPageRequest req)
        => Prim.GetLocationsPageAsync(req);

    protected override void OnInitialized()
    {
        Hotkeys.PushScope("locations");
        Hotkeys.Register("locations", "Ctrl+N", NewLocation);
        Hotkeys.Register("locations", "F9", Refresh);
    }

    protected override async Task OnInitializedAsync()
    {
        _tabs = await Prim.GetOpenSessionsAsync(App.CurrentUserId, "locations");
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Prim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "locations",
                Title = "All locations", Filter = "",
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.FirstOrDefault();
        if (_activeTab != null)
        {

            _tabState = _activeTab.ToTabState();
        }
    }

    public void Dispose()
    {
        Hotkeys.UnregisterScope("locations");
        // Best-effort: persist the active tab's state when leaving the page.
        _ = SaveActiveTabAsync().ContinueWith(t => { var _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted);
    }


    private async Task ActivateTab(SearchSession tab)
    {
        if (_activeTab?.Id == tab.Id) return;
        await SaveActiveTabAsync();
        _activeTab = tab;

        _tabState = tab.ToTabState();
    }

    private async Task NewTab()
    {
        await SaveActiveTabAsync();
        var (ok, err, s) = await Prim.CreateSessionAsync(new SearchSession
        {
            OwnerUserId = App.CurrentUserId, PageKind = "locations",
            Title = $"Locations {_tabs.Count + 1}", Filter = "",
        });
        if (!ok || s == null) { Snackbar.Add(err ?? "Could not open a tab.", Severity.Warning); return; }
        _tabs.Add(s);
        _activeTab = s;

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
                OwnerUserId = App.CurrentUserId, PageKind = "locations",
                Title = "All locations", Filter = "",
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.OrderByDescending(t => t.LastUsedUtc).FirstOrDefault();
        if (_activeTab != null)
        {

            _tabState = _activeTab.ToTabState();
        }
    }

    private Task OnTabStateChanged() => SaveActiveTabAsync();

    private async Task SaveActiveTabAsync()
    {
        if (_activeTab == null || _tabState == null) return;

        _activeTab.ApplyTabState(_tabState);
        await Prim.SaveSessionAsync(_activeTab);
    }

    private async Task Refresh() { await OnGridChanged(); Snackbar.Add("Locations refreshed.", Severity.Info); }

    private async Task OnGridChanged()
    {
        if (_grid != null) await _grid.ResetAsync();
    }

    private async Task NewLocation()
    {
        var d = await DialogService.ShowAsync<LocationDialog>("New Location",
            new DialogParameters { ["Model"] = new Location() },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        if ((await d.Result) is { Canceled: false }) await OnGridChanged();
    }

    private async Task<bool> EditLocation(Location l)
    {
        var d = await DialogService.ShowAsync<LocationDialog>("Edit Location",
            new DialogParameters { ["Model"] = l },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        return (await d.Result) is { Canceled: false };
    }
}
