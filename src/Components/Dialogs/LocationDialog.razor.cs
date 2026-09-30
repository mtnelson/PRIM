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

public partial class LocationDialog : ComponentBase
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public Location Model { get; set; } = new();

    private MudForm _form = null!;
    private Location _model = new();
    private List<Location> _locs = new();
    private string? _error;
    private List<string> _labels = new();

    protected override async Task OnInitializedAsync()
    {
        if (Model.Id != 0) _labels = await Prim.GetObjectLabelNamesAsync("Location", Model.Id);
        _locs = await Prim.GetLocationsAsync();
        _model = Model.Id == 0 ? new Location() : new Location
        {
            Id = Model.Id, LocationName = Model.LocationName, LocationType = Model.LocationType,
            ParentId = Model.ParentId, Description = Model.Description, Barcode = Model.Barcode,
            RowVersion = Model.RowVersion
        };
    }

    private void Upper(string v) => _model.LocationName = v.ToUpperInvariant();

    private async Task Save()
    {
        _error = null;
        await _form.Validate();
        if (!_form.IsValid) return;
        var isNew = _model.Id == 0;
        var (ok, err) = await Prim.SaveLocationAsync(_model, App.CurrentUserId);
        if (!ok) { _error = err; return; }
        await Prim.SetObjectLabelsAsync("Location", _model.Id, _labels, App.CurrentUserId);
        Snackbar.Add(isNew ? "Location created." : "Location updated.", Severity.Success);
        App.Log(isNew ? "Created location" : "Updated location", _model.LocationName);
        MudDialog.Close(DialogResult.Ok(true));
    }
}
