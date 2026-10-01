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

namespace Rim.Components.Pages;

public partial class Labels : ComponentBase, IDisposable
{
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public NavigationManager Nav { get; set; } = default!;

    [Parameter] public int? LabelId { get; set; }

    private List<Label> _labels = new();
    private Dictionary<int, int> _counts = new();
    private string _filter = "";
    private string _newName = "";
    private Label? _label;
    // Label members as full entities, one list per kind (backing the grids).
    private List<RecordItem> _records = new();
    private List<Container> _containers = new();
    private List<Location> _locations = new();
    private List<AppUser> _users = new();
    private int _totalMembers => _records.Count + _containers.Count + _locations.Count + _users.Count;

    private IEnumerable<Label> Filtered => string.IsNullOrWhiteSpace(_filter) ? _labels
        : _labels.Where(l => l.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    protected override void OnInitialized() => Hotkeys.PushScope("labels");

    protected override async Task OnParametersSetAsync() => await Load();

    public void Dispose() => Hotkeys.UnregisterScope("labels");

    private async Task Load()
    {
        if (LabelId == null)
        {
            _labels = await Rim.GetLabelsAsync();
            _counts = await Rim.GetLabelCountsAsync();
        }
        else
        {
            _label = await Rim.GetLabelAsync(LabelId.Value);
            if (_label == null)
            {
                _records = new(); _containers = new(); _locations = new(); _users = new();
            }
            else
            {
                _records = await Rim.GetLabelRecordsAsync(LabelId.Value);
                _containers = await Rim.GetLabelContainersAsync(LabelId.Value);
                _locations = await Rim.GetLabelLocationsAsync(LabelId.Value);
                _users = await Rim.GetLabelUsersAsync(LabelId.Value);
            }
        }
    }

    private Task ReloadMembers() => Load();

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
            var label = await Rim.GetOrCreateLabelAsync(name, App.CurrentUserId);
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
                await Rim.RenameLabelAsync(label.Id, newName, App.CurrentUserId);
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
        await Rim.DeleteLabelAsync(label.Id, App.CurrentUserId);
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
            "Record" => "/advanced", "Container" => "/containers",
            "Location" => "/locations", "User" => "/users", _ => "/"
        });
    }

    // Per-kind edit/delete handlers, mirroring the corresponding object pages.
    private async Task<bool> EditRecord(RecordItem r)
    {
        var d = await DialogService.ShowAsync<RecordDialog>("Edit Record",
            new DialogParameters { ["Model"] = r },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        return (await d.Result) is { Canceled: false };
    }

    private async Task<bool> DeleteRecords(List<int> ids)
    {
        var d = await DialogService.ShowAsync<DeleteDialog>("Delete Records",
            new DialogParameters { ["Ids"] = ids }, new DialogOptions { MaxWidth = MaxWidth.Medium });
        return (await d.Result) is { Canceled: false };
    }

    private async Task<bool> EditContainer(Container c)
    {
        var d = await DialogService.ShowAsync<ContainerDialog>("Edit Container",
            new DialogParameters { ["Model"] = c },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        return (await d.Result) is { Canceled: false };
    }

    private async Task<bool> DeleteContainers(List<int> ids)
    {
        bool? ok = await DialogService.ShowMessageBox("Delete containers?",
            $"Delete {ids.Count} container(s)? This cannot be undone.", yesText: "Delete", cancelText: "Cancel");
        if (ok != true) return false;
        await using var busy = BusyToast.Show(Snackbar, $"Deleting {ids.Count:N0} container(s)…");
        try
        {
            var n = await Rim.DeleteContainersAsync(ids, App.CurrentUserId);
            busy.Complete($"Deleted {n:N0} container(s).");
            var names = new List<string>();
            foreach (var id in ids) names.Add(await Rim.GetObjectLabelAsync("Container", id));
            App.LogItems("Deleted containers", names);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            Snackbar.Add(ex.Message, Severity.Warning);
            return false;
        }
    }

    private async Task<bool> EditLocation(Location l)
    {
        var d = await DialogService.ShowAsync<LocationDialog>("Edit Location",
            new DialogParameters { ["Model"] = l },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        return (await d.Result) is { Canceled: false };
    }

    private async Task<bool> EditUser(AppUser u)
    {
        var d = await DialogService.ShowAsync<UserDialog>("Edit User",
            new DialogParameters { ["Model"] = u },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        return (await d.Result) is { Canceled: false };
    }
}
