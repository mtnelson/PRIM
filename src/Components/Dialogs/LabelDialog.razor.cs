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

public partial class LabelDialog : ComponentBase
{
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    // Preferred: pass Labels. Single-label callers may still pass Title/Line2/Barcode.
    [Parameter] public List<(string Title, string Line2, string Barcode)> Labels { get; set; } = new();
    [Parameter] public string Title { get; set; } = "";
    [Parameter] public string Line2 { get; set; } = "";
    [Parameter] public string Barcode { get; set; } = "";

    private List<(string Title, string Line2, string Barcode)> Items =>
        Labels.Count > 0 ? Labels : new() { (Title, Line2, Barcode) };

    private async Task Print()
    {
        App.LogItems("Printed label(s)", Items.Select(i => i.Title));
        await PrimJs.TryInvokeVoidAsync(JS, "prim.print");
    }
}
