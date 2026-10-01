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

public partial class RecordDialog : ComponentBase
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public RecordItem Model { get; set; } = new();

    private MudForm _form = null!;
    private RecordItem _model = new();
    private List<RecordItem> _compressedParents = new();
    private List<string> _labels = new();
    private string? _error;
    private string _originalType = "";
    private bool _typeLocked => _model.Id != 0 && !App.IsRecordsManager;

    protected override async Task OnInitializedAsync()
    {
        // v0.12.0: only Compressed Parents can accept filed children (no
        // nesting); a record can never be its own parent.
        _compressedParents = (await Prim.GetRecordsAsync())
            .Where(r => r.RecordType == "Compressed" && r.CompressedRole == "Parent" && r.Id != Model.Id)
            .ToList();
        _model = Model.Id == 0
            ? new RecordItem { Home = App.CurrentDisplayName, HomeKind = "User", Assignee = App.CurrentDisplayName, AssigneeKind = "User" }
            : Clone(Model);
        _originalType = _model.RecordType;
        if (_model.Id != 0) _labels = await Prim.GetObjectLabelNamesAsync("Record", _model.Id);
    }

    private static RecordItem Clone(RecordItem r) => new()
    {
        Id = r.Id, RecordNumber = r.RecordNumber, Barcode = r.Barcode, RecordType = r.RecordType,
        CaseClassification = r.CaseClassification, FieldOffice = r.FieldOffice, CaseNumber = r.CaseNumber,
        SubfileId = r.SubfileId, Volume = r.Volume, SerialStart = r.SerialStart, SerialEnd = r.SerialEnd,
        AuxiliaryOffice = r.AuxiliaryOffice, IsBulky = r.IsBulky, IsAdmin = r.IsAdmin, IsControlFile = r.IsControlFile,
        Labels = r.Labels, SecurityClassification = r.SecurityClassification, Subject = r.Subject, Notes = r.Notes,
        Home = r.Home, HomeKind = r.HomeKind, HomeRefId = r.HomeRefId,
        Assignee = r.Assignee, AssigneeKind = r.AssigneeKind, AssigneeRefId = r.AssigneeRefId,
        ParentRecordId = r.ParentRecordId, CompressedRole = r.CompressedRole,
        State = r.State, RowVersion = r.RowVersion
    };

    private void UpperClass(string v) => _model.CaseClassification = v.ToUpperInvariant();
    private void UpperCase(string v) => _model.CaseNumber = v.ToUpperInvariant();
    private void UpperSubfile(string v) => _model.SubfileId = v.ToUpperInvariant();
    private void UpperSerialStart(string v) => _model.SerialStart = v.ToUpperInvariant();
    private void UpperSerialEnd(string v) => _model.SerialEnd = v.ToUpperInvariant();
    private void MatchHome()
    {
        _model.Assignee = _model.Home;
        _model.AssigneeKind = _model.HomeKind;
        _model.AssigneeRefId = _model.HomeRefId;
    }

    private async Task PickHome()
    {
        var opts = new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true };
        var d = await DialogService.ShowAsync<HomePickerDialog>("Select Home", opts);
        var res = await d.Result;
        if (res is { Canceled: false, Data: ValueTuple<string, int, string> pick })
        {
            _model.Home = pick.Item3; _model.HomeKind = pick.Item1; _model.HomeRefId = pick.Item2;
        }
    }

    private async Task PickAssignee()
    {
        var p = new DialogParameters { ["Title"] = "Select Assignee" };
        var opts = new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true };
        var d = await DialogService.ShowAsync<HomePickerDialog>("Select Assignee", p, opts);
        var res = await d.Result;
        if (res is { Canceled: false, Data: ValueTuple<string, int, string> pick })
        {
            _model.Assignee = pick.Item3; _model.AssigneeKind = pick.Item1; _model.AssigneeRefId = pick.Item2;
        }
    }
    [Inject] IDialogService DialogService { get; set; } = null!;

    private void Cancel() => MudDialog.Cancel();

    private async Task Save()
    {
        _error = null;
        await _form.Validate();
        if (!_form.IsValid) return;

        // TIS-2218: type change needs Records Manager + confirmation.
        if (_model.Id != 0 && _model.RecordType != _originalType)
        {
            if (!App.IsRecordsManager) { _error = "Changing the record type requires the Records Manager role."; return; }
            bool? ok = await DialogService.ShowMessageBox("Change record type?",
                $"Change type from '{_originalType}' to '{_model.RecordType}'? Fields not associated with the new type will be removed.",
                yesText: "Change", cancelText: "Cancel");
            if (ok != true) { _model.RecordType = _originalType; return; }
            if (_model.RecordType == "Compressed") { _model.SerialStart = _model.SerialEnd = null; } // TIS-1808
        }

        var isNew = _model.Id == 0;
        var (ok2, err) = await Prim.SaveRecordAsync(_model, App.CurrentUserId);
        if (!ok2) { _error = err; return; }
        await Prim.SetObjectLabelsAsync("Record", _model.Id, _labels, App.CurrentUserId);
        Snackbar.Add(isNew ? "Record created." : "Record updated.", Severity.Success);
        App.Log(isNew ? "Created record" : "Updated record", _model.RecordNumber);
        MudDialog.Close(DialogResult.Ok(true));
    }
}
