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

public partial class UserDialog : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;
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
        if (Model.Id != 0) _labels = await Rim.GetObjectLabelNamesAsync("User", Model.Id);
        _locations = await Rim.GetLocationsAsync();
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
        // H6: minimum password length 8 (UI-side; the service-side check in
        // RimService.SetUserPasswordAsync still enforces its own minimum).
        if (!string.IsNullOrEmpty(_password) && _password.Length < 8)
        { _error = "Password must be at least 8 characters."; return; }
        var isNew = _model.Id == 0;
        try
        {
            var (ok, err) = await Rim.SaveUserAsync(_model, App.CurrentUserId, App.CurrentRole);
            if (!ok) { _error = err; return; }
            await Rim.SetObjectLabelsAsync("User", _model.Id, _labels, App.CurrentUserId);
            if (!string.IsNullOrEmpty(_password))
            {
                var (pok, perr) = await Rim.SetUserPasswordAsync(_model.UserId, _password, App.CurrentUserId, App.CurrentRole);
                if (!pok) { _error = perr; return; }
            }
        }
        catch (DbUpdateException ex)
        {
            // M8: surface persistence failures in the dialog instead of
            // tearing the circuit.
            Snackbar.Add($"Save failed: {ex.Message}", Severity.Error);
            return;
        }
        Snackbar.Add(isNew ? "User created." : "User updated.", Severity.Success);
        App.Log(isNew ? "Created user" : "Updated user", _model.UserId);
        MudDialog.Close(DialogResult.Ok(true));
    }
}
