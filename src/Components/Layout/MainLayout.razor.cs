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

namespace Rim.Components.Layout;

public partial class MainLayout : IDisposable
{
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public NavigationManager Nav { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    private bool _leftOpen = true;
    private bool _viewOpen = true;
    private bool _logOpen = false;
    private DotNetObjectReference<MainLayout>? _self;
    private MudMenu? _newMenu;
    private IDisposable? _globalNewRegistration;
    // v0.12.0 dark/light mode: per-user stored preference; null preference
    // falls back to the OS prefers-color-scheme setting (resolved via JS).
    private bool _darkMode;
    private string? _themeLoadedFor;

    protected override async Task OnInitializedAsync()
    {
        App.Changed += OnAppChanged;
        // Push the browser's synchronous preventDefault set whenever the
        // registered combos change, even if this layout doesn't re-render.
        Hotkeys.CombosChanged += OnCombosChanged;
        App.ActiveAnnouncement = await Rim.GetAnnouncementAsync();
        await LoadThemeAsync();
        Hotkeys.PushScope("shell");
        Hotkeys.Register("shell", "Alt+1", () => Nav.NavigateTo(""));
        Hotkeys.Register("shell", "Alt+2", () => Nav.NavigateTo("advanced"));
        Hotkeys.Register("shell", "Alt+3", () => Nav.NavigateTo("records"));
        Hotkeys.Register("shell", "Alt+4", () => Nav.NavigateTo("containers"));
        Hotkeys.Register("shell", "Alt+5", () => Nav.NavigateTo("locations"));
        Hotkeys.Register("shell", "Alt+6", () => Nav.NavigateTo("users"));
        Hotkeys.Register("shell", "Alt+7", () => Nav.NavigateTo("workspaces"));
        Hotkeys.Register("shell", "Alt+8", () => Nav.NavigateTo("reports"));
        Hotkeys.Register("shell", "F1", ShowHelp);
        // Global New menu: one Ctrl+N handler for the whole app. Registered
        // under the "app" fallback scope so it fires from any page scope, and
        // inert on the login screen and inside dialogs (see OnGlobalNew).
        _globalNewRegistration = Hotkeys.Register("app", "Ctrl+N", OnGlobalNew);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            await RimJs.TryInvokeVoidAsync(JS, "rim.hotkeys.init", _self);
            // No stored preference: fall back to the OS color-scheme setting.
            // (JS is only available after the first render — never call it
            // from OnInitializedAsync, which also runs during prerendering.)
            if (App.IsAuthenticated && _themeLoadedFor == App.CurrentUserId
                && await Rim.GetThemePreferenceAsync(App.CurrentUserId) == null)
            {
                _darkMode = await RimJs.TryInvokeAsync<bool>(JS, "rim.theme.prefersDark") == true;
                StateHasChanged();
            }
            await RimJs.TryInvokeVoidAsync(JS, "rim.theme.setDark", _darkMode);
        }
        // Keep the browser's synchronous preventDefault set in sync. The
        // CombosChanged event (subscribed in OnInitializedAsync) re-renders
        // this layout whenever any component registers/unregisters, so the
        // push happens even for dialogs and tab switches that don't navigate.
        if (Hotkeys.CombosDirty)
            await RimJs.TryInvokeVoidAsync(JS, "rim.hotkeys.setCombos", Hotkeys.TakeCombos());
    }

    [JSInvokable]
    public Task<bool> OnHotkey(string combo) => Hotkeys.Handle(combo);

    private async Task ShowHelp()
    {
        await DialogService.ShowAsync<HelpDialog>("Help",
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        await Task.CompletedTask;
    }

    private void SignOut() => App.SignOut();

    private async Task OnGlobalNew()
    {
        // Preserve the old per-page behavior: Ctrl+N did nothing on the login
        // screen and while a dialog was open.
        if (!App.IsAuthenticated || Hotkeys.CurrentScope == "dialog") return;
        if (_newMenu != null) await _newMenu.OpenMenuAsync(EventArgs.Empty, false);
    }

    // Global creation menu. Same dialogs and parameters the object pages use;
    // nothing to refresh here, so the result is awaited and dropped.
    private async Task NewRecord()
    {
        var d = await DialogService.ShowAsync<RecordDialog>("New Record",
            new DialogParameters { ["Model"] = new RecordItem() },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        await d.Result;
    }

    private async Task NewContainer()
    {
        var d = await DialogService.ShowAsync<ContainerDialog>("New Container",
            new DialogParameters { ["Model"] = new Container() },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        await d.Result;
    }

    private async Task NewLocation()
    {
        var d = await DialogService.ShowAsync<LocationDialog>("New Location",
            new DialogParameters { ["Model"] = new Location() },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        await d.Result;
    }

    private async Task NewUser()
    {
        var d = await DialogService.ShowAsync<UserDialog>("New User",
            new DialogParameters { ["Model"] = new AppUser() },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        await d.Result;
    }

    private async Task NewLabel()
    {
        var d = await DialogService.ShowAsync<TextInputDialog>("New Label",
            new DialogParameters { ["Title"] = "New Label", ["Label"] = "Label name", ["Value"] = "" },
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        var res = await d.Result;
        if (res is { Canceled: false, Data: string name } && name.Trim().Length > 0)
        {
            try
            {
                var label = await Rim.GetOrCreateLabelAsync(name.Trim(), App.CurrentUserId);
                App.Log("Created label", label.Name);
                Snackbar.Add($"Label '{label.Name}' created.", Severity.Success);
            }
            catch (Exception ex) { Snackbar.Add(ex.Message, Severity.Error); }
        }
    }

    private async Task<string?> LoadThemeAsync()
    {
        if (!App.IsAuthenticated) return null;
        _themeLoadedFor = App.CurrentUserId;
        var pref = await Rim.GetThemePreferenceAsync(App.CurrentUserId);
        if (pref != null) _darkMode = pref == "Dark";
        // pref == null: OS default is resolved via JS in OnAfterRenderAsync
        // (first render) or in OnAppChanged (user switch) — never before the
        // interactive circuit exists, where JS interop is unavailable.
        return pref;
    }

    private async Task ToggleTheme()
    {
        _darkMode = !_darkMode;
        await RimJs.TryInvokeVoidAsync(JS, "rim.theme.setDark", _darkMode);
        if (App.IsAuthenticated)
            await Rim.SetThemePreferenceAsync(App.CurrentUserId, _darkMode ? "Dark" : "Light");
    }

    private void OnAppChanged() => InvokeAsync(async () =>
    {
        // Sign-in/out changes the user: reload their stored theme choice.
        if (App.IsAuthenticated && _themeLoadedFor != App.CurrentUserId)
        {
            var pref = await LoadThemeAsync();
            if (pref == null)
                _darkMode = await RimJs.TryInvokeAsync<bool>(JS, "rim.theme.prefersDark") == true;
            await RimJs.TryInvokeVoidAsync(JS, "rim.theme.setDark", _darkMode);
        }
        else if (!App.IsAuthenticated)
        {
            _themeLoadedFor = null;
        }
        StateHasChanged();
    });

    public void Dispose()
    {
        App.Changed -= OnAppChanged;
        Hotkeys.CombosChanged -= OnCombosChanged;
        _globalNewRegistration?.Dispose();
        Hotkeys.UnregisterScope("shell");
        _self?.Dispose();
    }

    private void OnCombosChanged() => InvokeAsync(StateHasChanged);

    private void ToggleLeft() => _leftOpen = !_leftOpen;
    private void ToggleView() => _viewOpen = !_viewOpen;

    private string RecentHref((string Kind, int Id, string Label) r) => r.Kind switch
    {
        "Record" => "records",
        "Container" => "containers",
        "Location" => "locations",
        "User" => "users",
        _ => ""
    };
}
