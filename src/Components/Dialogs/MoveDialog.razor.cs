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

public partial class MoveDialog : ComponentBase
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public string Kind { get; set; } = "Record";
    [Parameter] public List<int> Ids { get; set; } = new();
    private int Count => Ids.Count;

    private bool _changeHome, _changeAssignee, _assigneeFollowsHome = true;
    private string? _newHome, _newHomeKind, _newAssignee, _newAssigneeKind;
    private int? _newHomeRefId, _newAssigneeRefId;
    private bool _busy;

    private async Task PickHome()
    {
        var d = await DialogService.ShowAsync<HomePickerDialog>("Select Home",
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        var res = await d.Result;
        if (res is { Canceled: false, Data: ValueTuple<string, int, string> pick })
        {
            _newHomeKind = pick.Item1; _newHomeRefId = pick.Item2; _newHome = pick.Item3;
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
            _newAssigneeKind = pick.Item1; _newAssigneeRefId = pick.Item2; _newAssignee = pick.Item3;
        }
    }

    private async Task Save()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var n = await Prim.MoveItemsAsync(Kind, Ids,
                _changeHome ? _newHome : null, _newHomeKind, _newHomeRefId,
                _changeAssignee ? _newAssignee : null, _newAssigneeKind, _newAssigneeRefId, _assigneeFollowsHome, App.CurrentUserId);
            Snackbar.Add($"Moved {n} item(s).", Severity.Success);
            var labels = new List<string>();
            foreach (var id in Ids) labels.Add(await Prim.GetObjectLabelAsync(Kind, id));
            App.LogItems("Moved items", labels);
            MudDialog.Close(DialogResult.Ok(true));
        }
        finally { _busy = false; }
    }
}
