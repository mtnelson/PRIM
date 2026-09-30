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

namespace Prim.Components.Dialogs;

public partial class ExportDialog : ComponentBase
{
    [Inject] public IJSRuntime JS { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public List<(string Key, string Label)> Columns { get; set; } = new();
    [Parameter] public Func<List<string>, Task> OnExport { get; set; } = _ => Task.CompletedTask;

    private HashSet<string> _sel = new();

    protected override void OnInitialized() => _sel = Columns.Select(c => c.Key).ToHashSet();

    private void Toggle(string key, bool v)
    {
        if (v) _sel.Add(key); else _sel.Remove(key);
    }

    private async Task DoExport()
    {
        await OnExport(_sel.ToList());
        MudDialog.Close(DialogResult.Ok(true));
    }
}
