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

namespace Prim.Components.Layout;

public partial class MainLayout : IDisposable
{
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public NavigationManager Nav { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    private bool _leftOpen = true;
    private bool _viewOpen = true;
    private bool _logOpen = false;
    private DotNetObjectReference<MainLayout>? _self;

    protected override async Task OnInitializedAsync()
    {
        App.Changed += OnAppChanged;
        // Push the browser's synchronous preventDefault set whenever the
        // registered combos change, even if this layout doesn't re-render.
        Hotkeys.CombosChanged += OnCombosChanged;
        App.ActiveAnnouncement = await Prim.GetAnnouncementAsync();
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
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            await PrimJs.TryInvokeVoidAsync(JS, "prim.hotkeys.init", _self);
        }
        // Keep the browser's synchronous preventDefault set in sync. The
        // CombosChanged event (subscribed in OnInitializedAsync) re-renders
        // this layout whenever any component registers/unregisters, so the
        // push happens even for dialogs and tab switches that don't navigate.
        if (Hotkeys.CombosDirty)
            await PrimJs.TryInvokeVoidAsync(JS, "prim.hotkeys.setCombos", Hotkeys.TakeCombos());
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

    private void OnAppChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        App.Changed -= OnAppChanged;
        Hotkeys.CombosChanged -= OnCombosChanged;
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
