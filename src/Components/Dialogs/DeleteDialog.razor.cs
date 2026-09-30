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

public partial class DeleteDialog : ComponentBase
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public List<int> Ids { get; set; } = new();

    private string _reason = "Duplicate entry";
    private string? _mergedInto, _otherText, _error;

    private async Task Submit()
    {
        _error = null;
        if (_reason == "Merged with case file" && string.IsNullOrWhiteSpace(_mergedInto))
        { _error = "Merged Into barcode is required."; return; }
        if (_reason == "Other" && string.IsNullOrWhiteSpace(_otherText))
        { _error = "A reason is required."; return; }

        var n = await Prim.DeleteRecordsAsync(Ids, _reason, _mergedInto, _otherText, App.CurrentUserId);
        Snackbar.Add($"Deleted {n} record(s).", Severity.Success);
        var labels = new List<string>();
        foreach (var id in Ids) labels.Add(await Prim.GetObjectLabelAsync("Record", id));
        App.LogItems($"Deleted records ({_reason})", labels);
        MudDialog.Close(DialogResult.Ok(true));
    }
}
