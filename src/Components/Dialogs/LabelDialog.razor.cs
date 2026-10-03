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

namespace Rim.Components.Dialogs;

public partial class LabelDialog : ComponentBase
{
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public FastReportLabelService LabelReport { get; set; } = default!;

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
        await RimJs.TryInvokeVoidAsync(JS, "rim.print");
    }

    // PDF label sheet, now template-driven: FastReport lays out the 4"x2"
    // label from Reports/Label4x2.frx and the service redraws it as vector
    // PDF (barcodes stay sharp). One page per label for label printers.
    private async Task DownloadPdf()
    {
        var labels = Items.Select(i => new LabelItem(i.Title, i.Line2, i.Barcode)).ToList();
        var pdf = LabelReport.RenderLabels(labels, App.CurrentDisplayName);
        App.Log("Downloaded label PDF", $"{labels.Count} label(s)");
        await RimJs.TryInvokeVoidAsync(JS, "rim.download",
            $"RIM-labels-{DateTime.Now:yyyyMMdd-HHmm}.pdf", pdf, "application/pdf", true);
    }
}
