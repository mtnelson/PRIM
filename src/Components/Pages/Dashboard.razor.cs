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

public partial class Dashboard : ComponentBase, IDisposable
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;

    private RecordGrid? _recordGrid;
    private ContainerGrid? _containerGrid;
    private LocationGrid? _locationGrid;

    private string _objectType = "Record";
    private List<(string Key, string Label)> _fields = new();
    private HashSet<string> _selectedFields = new();
    private Dictionary<string, string> _criteria = new();
    private bool _searched = false;
    private bool _searching = false;
    private string _resultsTitle = "", _saveName = "";
    private List<SavedSearch> _saved = new();

    private List<RecordItem> _records = new();
    private List<Container> _containers = new();
    private List<Location> _locations = new();

    protected override void OnInitialized()
    {
        Hotkeys.PushScope("dashboard");
        Hotkeys.Register("dashboard", "F9", Refresh);
    }

    public void Dispose() => Hotkeys.UnregisterScope("dashboard");

    protected override async Task OnInitializedAsync()
    {
        SetFields();
        _saved = await Prim.GetSavedSearchesAsync(App.CurrentUserId);
    }

    private void SetFields()
    {
        _fields = _objectType switch
        {
            "Record" => new() { ("Barcode","Barcode"), ("CaseClassification","Case Classification"), ("FieldOffice","Field Office"),
                                ("CaseNumber","Case Number"), ("SubfileId","Subfile ID"), ("RecordNumber","Record Number"), ("Home","Home") },
            "Container" => new() { ("Barcode","Barcode"), ("ContainerName","Container Name"), ("FieldOffice","Field Office"),
                                   ("ContainerCode","Container Code"), ("Home","Home") },
            _ => new() { ("LocationName","Location Name") },
        };
        _selectedFields = _objectType switch
        {
            "Record" => new() { "Barcode","CaseClassification","FieldOffice","CaseNumber","SubfileId","RecordNumber","Home" },
            "Container" => new() { "Barcode","ContainerName","FieldOffice","ContainerCode","Home" },
            _ => new() { "LocationName" },
        };
        _criteria = _fields.ToDictionary(f => f.Key, _ => "");
    }

    private void OnObjectTypeChanged(string v)
    {
        _objectType = v; _searched = false;
        SetFields();
    }

    private void ToggleField(string key, bool v)
    {
        if (v) _selectedFields.Add(key); else { _selectedFields.Remove(key); _criteria[key] = ""; }
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await RunSearch();
    }

    private async Task Refresh()
    {
        await RunSearch();
        Snackbar.Add("Search refreshed.", Severity.Info);
    }

    private async Task RunSearch()
    {
        _searching = true;
        try
        {
            var crit = _selectedFields.Where(k => !string.IsNullOrWhiteSpace(_criteria[k]))
                                     .ToDictionary(k => k, k => _criteria[k]);
            if (_objectType == "Record")
            {
                _records = await Prim.SearchRecordsAsync(crit);
                _resultsTitle = $"{_records.Count} record(s) found";
            }
            else if (_objectType == "Container")
            {
                _containers = await Prim.SearchContainersAsync(crit);
                _resultsTitle = $"{_containers.Count} container(s) found";
            }
            else
            {
                _locations = await Prim.SearchLocationsAsync(crit.GetValueOrDefault("LocationName"));
                _resultsTitle = $"{_locations.Count} location(s) found";
            }
            _recordGrid?.ClearSelection(); _containerGrid?.ClearSelection(); _locationGrid?.ClearSelection();
            _searched = true;
            App.Log("Quick search", $"{_objectType}: {_resultsTitle}");
        }
        finally { _searching = false; }
    }

    private void ClearCriteria()
    {
        foreach (var k in _criteria.Keys.ToList()) _criteria[k] = "";
    }

    private async Task SaveSearch()
    {
        var crit = _selectedFields.Where(k => !string.IsNullOrWhiteSpace(_criteria[k]))
                                 .ToDictionary(k => k, k => _criteria[k]);
        await Prim.SaveSearchAsync(new SavedSearch
        {
            OwnerUserId = App.CurrentUserId, Name = _saveName, ObjectKind = _objectType,
            FieldsCsv = string.Join(",", crit.Keys),
            Criteria = System.Text.Json.JsonSerializer.Serialize(crit)
        });
        _saved = await Prim.GetSavedSearchesAsync(App.CurrentUserId);
        Snackbar.Add($"Saved search '{_saveName}'.", Severity.Success);
        _saveName = "";
    }

    private async Task LoadSaved(SavedSearch s)
    {
        _objectType = s.ObjectKind; SetFields();
        var crit = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(s.Criteria) ?? new();
        _selectedFields = crit.Keys.ToHashSet();
        foreach (var (k,v) in crit) if (_criteria.ContainsKey(k)) _criteria[k] = v;
        await RunSearch();
    }

    private async Task DeleteSaved(SavedSearch s)
    {
        await Prim.DeleteSavedSearchAsync(s.Id);
        _saved = await Prim.GetSavedSearchesAsync(App.CurrentUserId);
    }

    private async Task<bool> EditRecord(RecordItem r)
    {
        var d = await DialogService.ShowAsync<RecordDialog>("Edit Record",
            new DialogParameters { ["Model"] = r },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        var res = await d.Result;
        return res is { Canceled: false };
    }

    private async Task<bool> EditContainer(Container c)
    {
        var d = await DialogService.ShowAsync<ContainerDialog>("Edit Container",
            new DialogParameters { ["Model"] = c },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        var res = await d.Result;
        return res is { Canceled: false };
    }

    private async Task<bool> EditLocation(Location l)
    {
        var d = await DialogService.ShowAsync<LocationDialog>("Edit Location",
            new DialogParameters { ["Model"] = l },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        var res = await d.Result;
        return res is { Canceled: false };
    }

    private async Task<bool> DeleteRecords(List<int> ids)
    {
        var d = await DialogService.ShowAsync<DeleteDialog>("Delete Records",
            new DialogParameters { ["Ids"] = ids },
            new DialogOptions { MaxWidth = MaxWidth.Medium });
        var res = await d.Result;
        return res is { Canceled: false };
    }

    private async Task<bool> DeleteContainers(List<int> ids)
    {
        bool? ok = await DialogService.ShowMessageBox("Delete containers?",
            $"Delete {ids.Count} container(s)? This cannot be undone.", yesText: "Delete", cancelText: "Cancel");
        if (ok != true) return false;
        await using var busy = BusyToast.Show(Snackbar, $"Deleting {ids.Count:N0} container(s)…");
        var n = await Prim.DeleteContainersAsync(ids, App.CurrentUserId);
        busy.Complete($"Deleted {n:N0} container(s).");
        var names = new List<string>();
        foreach (var id in ids) names.Add(await Prim.GetObjectLabelAsync("Container", id));
        App.LogItems("Deleted containers", names);
        return true;
    }
}
