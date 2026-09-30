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

public partial class UserDialog : ComponentBase
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public AppUser Model { get; set; } = new();

    private MudForm _form = null!;
    private AppUser _model = new();
    private List<Location> _locations = new();
    private string _password = "";
    private string? _error;
    private List<string> _labels = new();

    protected override async Task OnInitializedAsync()
    {
        if (Model.Id != 0) _labels = await Prim.GetObjectLabelNamesAsync("User", Model.Id);
        _locations = await Prim.GetLocationsAsync();
        _model = Model.Id == 0 ? new AppUser() : new AppUser
        {
            Id = Model.Id, UserId = Model.UserId, DisplayName = Model.DisplayName,
            Role = Model.Role, Email = Model.Email, LocationId = Model.LocationId,
            Active = Model.Active, RowVersion = Model.RowVersion
        };
    }

    private async Task Save()
    {
        _error = null;
        await _form.Validate();
        if (!_form.IsValid) return;
        var isNew = _model.Id == 0;
        var (ok, err) = await Prim.SaveUserAsync(_model, App.CurrentUserId);
        if (!ok) { _error = err; return; }
        await Prim.SetObjectLabelsAsync("User", _model.Id, _labels, App.CurrentUserId);
        if (!string.IsNullOrEmpty(_password))
        {
            var (pok, perr) = await Prim.SetUserPasswordAsync(_model.UserId, _password, App.CurrentUserId);
            if (!pok) { _error = perr; return; }
        }
        Snackbar.Add(isNew ? "User created." : "User updated.", Severity.Success);
        App.Log(isNew ? "Created user" : "Updated user", _model.UserId);
        MudDialog.Close(DialogResult.Ok(true));
    }
}
