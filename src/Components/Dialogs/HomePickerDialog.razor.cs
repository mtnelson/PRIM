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
    private HashSet<TreeItemData<Location>> _locRoots = new();
    private HashSet<TreeItemData<Container>> _conRoots = new();
    private List<(string Name, List<AppUser> Users)> _userGroups = new();
    private bool _locHasMore, _conHasMore, _usersHasMore;
    private const int PageCap = 500;

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    private async Task OnSearchChanged(string q) { _q = q; await ReloadAsync(); }

    // H4: this dialog used to load ALL locations/containers/users to build
    // its trees. Roots now come from server-side filtered, take-capped page
    // queries; children load lazily on node expand (ServerData) only.
    private async Task ReloadAsync()
    {
        var locPage = await Rim.GetLocationsPageAsync(new GridPageRequest { Take = PageCap, Filter = _q });
        var locIds = locPage.Rows.Select(l => l.Id).ToHashSet();
        var locRoots = locPage.Rows
            .Where(l => l.ParentId == null || !locIds.Contains(l.ParentId.Value)).ToList();
        var locExpandable = await Rim.GetHasChildrenAsync("Location", locRoots.Select(l => l.Id));
        _locRoots = locRoots.Select(l => new TreeItemData<Location>
            { Text = l.LocationName, Value = l, Expandable = locExpandable.Contains(l.Id) }).ToHashSet();
        _locHasMore = locPage.HasMore;

        var conPage = await Rim.GetContainersPageAsync(new GridPageRequest { Take = PageCap, Filter = _q });
        var conIds = conPage.Rows.Select(c => c.Id).ToHashSet();
        var conRoots = conPage.Rows
            .Where(c => c.ParentContainerId == null || !conIds.Contains(c.ParentContainerId.Value)).ToList();
        var conExpandable = await Rim.GetHasChildrenAsync("Container", conRoots.Select(c => c.Id));
        _conRoots = conRoots.Select(c => new TreeItemData<Container>
            { Text = c.ContainerName, Value = c, Expandable = conExpandable.Contains(c.Id) }).ToHashSet();
        _conHasMore = conPage.HasMore;

        var rows = new List<(string Field, string Op, string Value)>();
        if (!string.IsNullOrWhiteSpace(_q))
        {
            rows.Add(("DisplayName", "Contains", _q.Trim()));
            rows.Add(("UserId", "Contains", _q.Trim()));
        }
        var userPage = await Rim.AdvancedSearchUsersPageAsync(rows, "OR",
            new GridPageRequest { Take = PageCap });
        var locNames = (await Rim.GetLocationsByIdsAsync(userPage.Rows
                .Where(u => u.LocationId != null).Select(u => u.LocationId!.Value)))
            .ToDictionary(l => l.Id, l => l.LocationName);
        _userGroups = userPage.Rows
            .GroupBy(u => u.LocationId is int id && locNames.TryGetValue(id, out var n) ? n : "No location")
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.OrderBy(u => u.DisplayName).ToList()))
            .ToList();
        _usersHasMore = userPage.HasMore;
        StateHasChanged();
    }

    // Lazy child loading: one server round-trip per expanded node.
    // For containers this includes nested AND homed containers (the service
    // does not expose a nested-only query); both are valid home choices.
    private async Task<IReadOnlyCollection<TreeItemData<Location>>> LoadLocationChildren(Location parent)
    {
        var kids = (await Rim.GetChildItemsAsync("Location", parent.Id))
            .Where(i => i.Kind == "Location").ToList();
        var expandable = await Rim.GetHasChildrenAsync("Location", kids.Select(k => k.Id));
        return kids.Select(k => new TreeItemData<Location>
        {
            Text = k.Label,
            Value = new Location { Id = k.Id, LocationName = k.Label, LocationType = ChildType(k.Detail) },
            Expandable = expandable.Contains(k.Id)
        }).ToList();
    }

    private async Task<IReadOnlyCollection<TreeItemData<Container>>> LoadContainerChildren(Container parent)
    {
        var kids = (await Rim.GetChildItemsAsync("Container", parent.Id))
            .Where(i => i.Kind == "Container").ToList();
        var expandable = await Rim.GetHasChildrenAsync("Container", kids.Select(k => k.Id));
        return kids.Select(k => new TreeItemData<Container>
        {
            Text = k.Label,
            Value = new Container { Id = k.Id, ContainerName = k.Label, ContainerType = ChildType(k.Detail) },
            Expandable = expandable.Contains(k.Id)
        }).ToList();
    }

    // ChildItem.Detail is "Location · {type}" or "Container · {type} · {barcode}".
    private static string ChildType(string detail)
    {
        var parts = detail.Split('·');
        return parts.Length > 1 ? parts[1].Trim() : "";
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
