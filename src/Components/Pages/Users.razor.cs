using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using MudBlazor;
using Rim.Components.Dialogs;
using Rim.Components.Layout;
using Rim.Components.Shared;
using Rim.Data;
using Rim.Services;

namespace Rim.Components.Pages;

public partial class Users : ComponentBase, IDisposable
{
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;

    private UserGrid? _grid;
    private bool _showInactive;
    // Search-session tabs: one persisted descriptor per open tab (see Records.razor).
    private List<SearchSession> _tabs = new();
    private SearchSession? _activeTab;
    private SearchTabState? _tabState;


    private Task<GridPageResult<AppUser>> ProvideUsers(GridPageRequest req)
        => Rim.GetUsersPageAsync(req, _showInactive);

    private async Task OnShowInactiveChanged(bool v)
    {
        _showInactive = v;
        if (_grid != null) await _grid.ResetAsync();
        await SaveActiveTabAsync();
    }

    protected override void OnInitialized()
    {
        Hotkeys.PushScope("users");
        Hotkeys.Register("users", "F9", Refresh);
    }

    protected override async Task OnInitializedAsync()
    {
        _tabs = await Rim.GetOpenSessionsAsync(App.CurrentUserId, "users");
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Rim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "users",
                Title = "All users", Filter = "",
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.FirstOrDefault();
        if (_activeTab != null)
        {
        _showInactive = _activeTab.Filter == "ShowInactive";
            _tabState = _activeTab.ToTabState();
        }
    }

    public void Dispose()
    {
        Hotkeys.UnregisterScope("users");
        // Best-effort: persist the active tab's state when leaving the page.
        _ = SaveActiveTabAsync().ContinueWith(t => { var _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted);
    }


    private async Task ActivateTab(SearchSession tab)
    {
        if (_activeTab?.Id == tab.Id) return;
        await SaveActiveTabAsync();
        _activeTab = tab;
        _showInactive = tab.Filter == "ShowInactive";
        _tabState = tab.ToTabState();
    }

    private async Task NewTab()
    {
        await SaveActiveTabAsync();
        var (ok, err, s) = await Rim.CreateSessionAsync(new SearchSession
        {
            OwnerUserId = App.CurrentUserId, PageKind = "users",
            Title = "All users", Filter = "",
        });
        if (!ok || s == null) { Snackbar.Add(err ?? "Could not open a tab.", Severity.Warning); return; }
        _tabs.Add(s);
        _activeTab = s;
        _showInactive = false;
        _tabState = s.ToTabState();
    }

    private async Task CloseTab(SearchSession tab)
    {
        await Rim.DeleteSessionAsync(tab.Id, App.CurrentUserId);
        _tabs.RemoveAll(t => t.Id == tab.Id);
        if (_activeTab?.Id == tab.Id)
            await ActivateMostRecentAsync();
    }

    private async Task ActivateMostRecentAsync()
    {
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Rim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "users",
                Title = "All users", Filter = "",
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.OrderByDescending(t => t.LastUsedUtc).FirstOrDefault();
        if (_activeTab != null)
        {
        _showInactive = _activeTab.Filter == "ShowInactive";
            _tabState = _activeTab.ToTabState();
        }
    }

    private Task OnTabStateChanged() => SaveActiveTabAsync();

    private async Task SaveActiveTabAsync()
    {
        if (_activeTab == null || _tabState == null) return;
        _activeTab.Filter = _showInactive ? "ShowInactive" : "";
        _activeTab.Title = _showInactive ? "Users (incl. inactive)" : "All users";
        _activeTab.ApplyTabState(_tabState);
        await Rim.SaveSessionAsync(_activeTab);
    }

    private async Task Refresh() { await OnGridChanged(); Snackbar.Add("Users refreshed.", Severity.Info); }

    private async Task OnGridChanged()
    {
        if (_grid != null) await _grid.ResetAsync();
    }

    private async Task NewUser()
    {
        var d = await DialogService.ShowAsync<UserDialog>("New User",
            new DialogParameters { ["Model"] = new AppUser() },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        if ((await d.Result) is { Canceled: false }) await OnGridChanged();
    }

    private async Task<bool> EditUser(AppUser u)
    {
        var d = await DialogService.ShowAsync<UserDialog>("Edit User",
            new DialogParameters { ["Model"] = u },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        return (await d.Result) is { Canceled: false };
    }
}
