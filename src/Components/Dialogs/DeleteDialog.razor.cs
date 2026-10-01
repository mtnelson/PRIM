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

public partial class DeleteDialog : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public List<int> Ids { get; set; } = new();

    private string _reason = "Duplicate entry";
    private string? _mergedInto, _otherText, _error;
    private bool _busy;

    private async Task Submit()
    {
        if (_busy) return;
        _error = null;
        if (_reason == "Merged with case file" && string.IsNullOrWhiteSpace(_mergedInto))
        { _error = "Merged Into barcode is required."; return; }
        if (_reason == "Other" && string.IsNullOrWhiteSpace(_otherText))
        { _error = "A reason is required."; return; }

        _busy = true;
        try
        {
            var n = await Rim.DeleteRecordsAsync(Ids, _reason, _mergedInto, _otherText, App.CurrentUserId);
            Snackbar.Add($"Deleted {n} record(s).", Severity.Success);
            var labels = new List<string>();
            foreach (var id in Ids) labels.Add(await Rim.GetObjectLabelAsync("Record", id));
            App.LogItems($"Deleted records ({_reason})", labels);
            MudDialog.Close(DialogResult.Ok(true));
        }
        finally { _busy = false; }
    }
}
