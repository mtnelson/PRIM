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

public partial class RecordDialog : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public RecordItem Model { get; set; } = new();

    private MudForm _form = null!;
    private RecordItem _model = new();
    private List<RecordItem> _compressedParents = new();
    private string _parentSearch = "";
    private bool _parentsHasMore;
    private const int ParentPageCap = 500;
    private List<string> _labels = new();
    private string? _error;
    private string _originalType = "";
    private bool _typeLocked => _model.Id != 0 && !App.IsRecordsManager;

    protected override async Task OnInitializedAsync()
    {
        _model = Model.Id == 0
            ? new RecordItem { Home = App.CurrentDisplayName, HomeKind = "User", Assignee = App.CurrentDisplayName, AssigneeKind = "User" }
            : Clone(Model);
        _originalType = _model.RecordType;
        await LoadCompressedParents();
        if (_model.Id != 0) _labels = await Rim.GetObjectLabelNamesAsync("Record", _model.Id);
    }

    // H4: the compressed-parent dropdown used to load ALL records. It now
    // pages server-side (filter + take cap) instead of materializing the table.
    private async Task LoadCompressedParents()
    {
        var page = await Rim.GetRecordsPageAsync(new GridPageRequest { Take = ParentPageCap, Filter = _parentSearch });
        _compressedParents = page.Rows
            // v0.12.0: only Compressed Parents can accept filed children (no
            // nesting); a record can never be its own parent.
            .Where(r => r.RecordType == "Compressed" && r.CompressedRole == "Parent" && r.Id != Model.Id)
            .ToList();
        _parentsHasMore = page.HasMore;
        // Keep the currently selected parent visible even when it falls
        // outside the filtered page.
        if (_model.ParentRecordId is int pid && _compressedParents.All(p => p.Id != pid))
        {
            var cur = await Rim.GetRecordAsync(pid);
            if (cur != null) _compressedParents.Insert(0, cur);
        }
    }

    private async Task OnParentSearchChanged(string v)
    {
        _parentSearch = v;
        await LoadCompressedParents();
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
        try
        {
            var (ok2, err) = await Rim.SaveRecordAsync(_model, App.CurrentUserId, App.CurrentRole);
            if (!ok2) { _error = err; return; }
            await Rim.SetObjectLabelsAsync("Record", _model.Id, _labels, App.CurrentUserId);
        }
        catch (DbUpdateException ex)
        {
            // M8: surface persistence failures in the dialog instead of
            // tearing the circuit.
            Snackbar.Add($"Save failed: {ex.Message}", Severity.Error);
            return;
        }
        Snackbar.Add(isNew ? "Record created." : "Record updated.", Severity.Success);
        App.Log(isNew ? "Created record" : "Updated record", _model.RecordNumber);
        MudDialog.Close(DialogResult.Ok(true));
    }
}
