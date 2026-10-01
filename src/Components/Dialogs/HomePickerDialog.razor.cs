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

namespace Rim.Components.Dialogs;

public partial class HomePickerDialog : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public string Title { get; set; } = "Select Home";

    private string _q = "";
    private List<Location> _allLocs = new();
    private List<Container> _allCons = new();
    private List<AppUser> _allUsers = new();
    private Dictionary<int, string> _locNames = new();
    private List<TreeItemData<Location>> _locRoots = new();
    private List<TreeItemData<Container>> _conRoots = new();
    private List<(string Name, List<AppUser> Users)> _userGroups = new();

    protected override async Task OnInitializedAsync()
    {
        _allLocs = await Rim.GetLocationsAsync();
        _allCons = await Rim.GetContainersAsync();
        _allUsers = await Rim.GetUsersAsync();
        _locNames = _allLocs.ToDictionary(l => l.Id, l => l.LocationName);
        BuildTrees();
    }

    private void OnSearchChanged(string q) { _q = q; BuildTrees(); }

    private void BuildTrees()
    {
        var byParent = _allLocs.ToLookup(l => l.ParentId);
        TreeItemData<Location> BuildLoc(Location l) => new()
        {
            Value = l,
            Children = byParent[l.Id].Where(x => Match(x.LocationName)).Select(BuildLoc).ToList()
        };
        _locRoots = byParent[null].Where(x => Match(x.LocationName)).Select(BuildLoc).ToList();

        var byConParent = _allCons.ToLookup(c => c.ParentContainerId);
        TreeItemData<Container> BuildCon(Container c) => new()
        {
            Value = c,
            Children = byConParent[c.Id].Where(x => Match(x.ContainerName)).Select(BuildCon).ToList()
        };
        _conRoots = byConParent[null].Where(x => Match(x.ContainerName)).Select(BuildCon).ToList();

        _userGroups = _allUsers
            .GroupBy(u => u.LocationId is int id && _locNames.TryGetValue(id, out var n) ? n : "No location")
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.OrderBy(u => u.DisplayName).ToList()))
            .ToList();
    }

    private bool Match(string s) => string.IsNullOrWhiteSpace(_q) || s.Contains(_q, StringComparison.OrdinalIgnoreCase);

    // Only choose on click when the node itself matches the search; expanding still works.
    private void ChooseLocation(Location? l)
    {
        if (l != null && Match(l.LocationName)) Choose("Location", l.Id, l.LocationName);
    }

    private void ChooseContainer(Container? c)
    {
        if (c != null && Match(c.ContainerName)) Choose("Container", c.Id, c.ContainerName);
    }

    private void Choose(string kind, int id, string label) => MudDialog.Close(DialogResult.Ok((kind, id, label)));
}
