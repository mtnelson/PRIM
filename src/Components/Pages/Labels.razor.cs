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

namespace Prim.Components.Pages;

public partial class Labels : ComponentBase
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public NavigationManager Nav { get; set; } = default!;

    [Parameter] public int? LabelId { get; set; }

    private List<Label> _labels = new();
    private Dictionary<int, int> _counts = new();
    private string _filter = "";
    private string _newName = "";
    private Label? _label;
    private List<(string Kind, int Id, string Label)> _members = new();

    private IEnumerable<Label> Filtered => string.IsNullOrWhiteSpace(_filter) ? _labels
        : _labels.Where(l => l.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    protected override async Task OnParametersSetAsync() => await Load();

    private async Task Load()
    {
        if (LabelId == null)
        {
            _labels = await Prim.GetLabelsAsync();
            _counts = await Prim.GetLabelCountsAsync();
        }
        else
        {
            _label = await Prim.GetLabelAsync(LabelId.Value);
            _members = _label == null ? new() : await Prim.GetLabelMembersAsync(LabelId.Value);
        }
    }

    private async Task NewLabelKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await CreateLabel();
    }

    private async Task CreateLabel()
    {
        var name = _newName.Trim();
        if (name.Length == 0) return;
        try
        {
            var label = await Prim.GetOrCreateLabelAsync(name, App.CurrentUserId);
            App.Log("Created label", label.Name);
            _newName = "";
            await Load();
            Snackbar.Add($"Label '{label.Name}' created.", Severity.Success);
        }
        catch (Exception ex) { Snackbar.Add(ex.Message, Severity.Error); }
    }

    private async Task Rename(Label label)
    {
        var p = new DialogParameters { ["Title"] = "Rename Label", ["Label"] = "New name", ["Value"] = label.Name };
        var d = await DialogService.ShowAsync<TextInputDialog>("Rename Label", p,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        var res = await d.Result;
        if (res is { Canceled: false, Data: string newName })
        {
            try
            {
                await Prim.RenameLabelAsync(label.Id, newName, App.CurrentUserId);
                App.Log("Renamed label", $"{label.Name} → {newName.Trim()}");
                await Load();
                Snackbar.Add("Label renamed.", Severity.Success);
            }
            catch (Exception ex) { Snackbar.Add(ex.Message, Severity.Error); }
        }
    }

    private async Task Delete(Label label)
    {
        bool? ok = await DialogService.ShowMessageBox("Delete label?",
            $"Delete the label '{label.Name}'? It will be removed from all items. This cannot be undone.",
            yesText: "Delete", cancelText: "Cancel");
        if (ok != true) return;
        await Prim.DeleteLabelAsync(label.Id, App.CurrentUserId);
        App.Log("Deleted label", label.Name);
        Snackbar.Add("Label deleted.", Severity.Success);
        if (LabelId != null) Nav.NavigateTo("/labels");
        else await Load();
    }

    private void GoTo(string kind, int id)
    {
        App.RequestFocus(kind, id, false);
        Nav.NavigateTo(kind switch
        {
            "Record" => "/records", "Container" => "/containers",
            "Location" => "/locations", "User" => "/users", _ => "/"
        });
    }
}
