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

namespace Rim.Components.Shared;

public partial class DialogHotkeys : ComponentBase, IDisposable
{
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;

    [Parameter] public Func<Task>? OnSave { get; set; }
    [Parameter] public Action? OnCancel { get; set; }

    private readonly List<IDisposable> _regs = new();

    protected override void OnInitialized()
    {
        Hotkeys.PushScope("dialog");
        var save = OnSave;
        if (save != null)
            _regs.Add(Hotkeys.Register("dialog", "Ctrl+S", save));
        var cancel = OnCancel;
        if (cancel != null)
            _regs.Add(Hotkeys.Register("dialog", "Escape", cancel));
    }

    public void Dispose()
    {
        foreach (var r in _regs) r.Dispose();
        _regs.Clear();
        Hotkeys.PopScope();
    }
}
