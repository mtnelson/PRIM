using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.EntityFrameworkCore;
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
            var (delOk, delErr, n) = await Rim.DeleteRecordsAsync(Ids, _reason, _mergedInto, _otherText, App.CurrentUserId);
            if (!delOk) { Snackbar.Add($"Delete failed: {delErr}", Severity.Error); return; }
            Snackbar.Add($"Deleted {n} record(s).", Severity.Success);
            var labels = new List<string>();
            foreach (var id in Ids) labels.Add(await Rim.GetObjectLabelAsync("Record", id));
            App.LogItems($"Deleted records ({_reason})", labels);
            MudDialog.Close(DialogResult.Ok(true));
        }
        catch (DbUpdateException ex)
        {
            // M8: surface persistence failures in the dialog instead of
            // tearing the circuit.
            Snackbar.Add($"Delete failed: {ex.Message}", Severity.Error);
        }
        finally { _busy = false; }
    }
}
