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

public partial class ContainerDialog : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public Container Model { get; set; } = new();

    private MudForm _form = null!;
    private Container _model = new();
    private string? _error;
    private List<string> _labels = new();

    protected override async Task OnInitializedAsync()
    {
        if (Model.Id != 0) _labels = await Rim.GetObjectLabelNamesAsync("Container", Model.Id);
        _model = Model.Id == 0
            ? new Container { Home = App.CurrentDisplayName, HomeKind = "User", Assignee = App.CurrentDisplayName, AssigneeKind = "User" }
            : new Container
            {
                Id = Model.Id, ContainerName = Model.ContainerName, ContainerType = Model.ContainerType,
                FieldOffice = Model.FieldOffice, ContainerCode = Model.ContainerCode, FormattedNumber = Model.FormattedNumber,
                Description = Model.Description, Barcode = Model.Barcode, Home = Model.Home, HomeKind = Model.HomeKind,
                HomeRefId = Model.HomeRefId, Assignee = Model.Assignee, AssigneeKind = Model.AssigneeKind,
                AssigneeRefId = Model.AssigneeRefId, ParentContainerId = Model.ParentContainerId,
                LocationId = Model.LocationId, RowVersion = Model.RowVersion
            };
        if (_model.Id == 0) await GenerateName();
    }

    private async Task GenerateName()
    {
        if (string.IsNullOrWhiteSpace(_model.FormattedNumber))
            _model.FormattedNumber = "00001";
        _model.ContainerName = await Rim.PreviewContainerNameAsync(_model.ContainerType, _model.FieldOffice, _model.ContainerCode);
        // For Box/Tub/Crate/Crate/NARA/Virtual the formatted number is embedded directly.
        if (_model.ContainerType is "Box" or "Tub" or "Crate" or "NARA Box" or "Virtual Container")
            _model.ContainerName = $"{_model.FieldOffice}-{_model.ContainerCode}-{_model.FormattedNumber}".ToUpperInvariant();
    }

    private async Task PickHome()
    {
        var d = await DialogService.ShowAsync<HomePickerDialog>("Select Home",
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        var res = await d.Result;
        if (res is { Canceled: false, Data: ValueTuple<string, int, string> pick })
        {
            _model.Home = pick.Item3; _model.HomeKind = pick.Item1; _model.HomeRefId = pick.Item2;
            if (pick.Item1 == "Location") _model.LocationId = pick.Item2;
        }
    }

    private async Task PickAssignee()
    {
        var p = new DialogParameters { ["Title"] = "Select Assignee" };
        var d = await DialogService.ShowAsync<HomePickerDialog>("Select Assignee", p,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        var res = await d.Result;
        if (res is { Canceled: false, Data: ValueTuple<string, int, string> pick })
        {
            _model.Assignee = pick.Item3; _model.AssigneeKind = pick.Item1; _model.AssigneeRefId = pick.Item2;
        }
    }

    private async Task Save()
    {
        _error = null;
        await _form.Validate();
        if (!_form.IsValid) return;
        var isNew = _model.Id == 0;
        try
        {
            var (ok, err) = await Rim.SaveContainerAsync(_model, App.CurrentUserId);
            if (!ok) { _error = err; return; }
            await Rim.SetObjectLabelsAsync("Container", _model.Id, _labels, App.CurrentUserId);
        }
        catch (DbUpdateException ex)
        {
            // M8: surface persistence failures in the dialog instead of
            // tearing the circuit.
            Snackbar.Add($"Save failed: {ex.Message}", Severity.Error);
            return;
        }
        Snackbar.Add(isNew ? "Container created." : "Container updated.", Severity.Success);
        App.Log(isNew ? "Created container" : "Updated container", _model.ContainerName);
        MudDialog.Close(DialogResult.Ok(true));
    }
}
