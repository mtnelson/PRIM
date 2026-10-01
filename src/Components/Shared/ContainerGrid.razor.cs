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

namespace Rim.Components.Shared;

public partial class ContainerGrid : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;

    private ObjectGrid<Container>? _grid;

    [Parameter] public IEnumerable<Container> Items { get; set; } = Enumerable.Empty<Container>();
    [Parameter] public Func<GridPageRequest, Task<GridPageResult<Container>>>? ItemsProvider { get; set; }
    [Parameter] public Func<Task<int>>? CountProvider { get; set; }
    [Parameter] public Func<Task<List<int>>>? AllIdsProvider { get; set; }
    [Parameter] public string GridId { get; set; } = "containers";
    [Parameter] public string Scope { get; set; } = "containers";
    [Parameter] public Func<Container, Task<bool>>? EditItem { get; set; }
    [Parameter] public Func<List<int>, Task<bool>>? DeleteItems { get; set; }
    [Parameter] public bool CanMove { get; set; } = true;
    [Parameter] public bool CanDelete { get; set; } = true;
    [Parameter] public bool ShowRemoveFromSlot { get; set; }
    [Parameter] public EventCallback<List<int>> RemoveFromSlot { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }
    // Search-tab state, passed through to the inner ObjectGrid.
    [Parameter] public SearchTabState? TabState { get; set; }
    [Parameter] public EventCallback TabStateChanged { get; set; }

    private Dictionary<int, List<PathSeg>> _paths = new();
    private HashSet<int> _withChildren = new();
    private Dictionary<int, List<(int Id, string Name)>> _labelPairs = new();

    protected override async Task OnParametersSetAsync()
    {
        if (ItemsProvider != null) return; // provider mode enriches per chunk
        var ids = Items.Select(c => c.Id).ToList();
        _labelPairs = await Rim.GetObjectLabelPairsAsync("Container", ids);
        _paths = await Rim.GetAncestorPathsAsync("Container", ids);
        _withChildren = await Rim.GetHasChildrenAsync("Container", ids);
    }

    private Dictionary<string, string> RowWithPath(Container c)
    {
        var d = GridColumns.ContainerRow(c);
        d["Labels"] = _labelPairs.TryGetValue(c.Id, out var lp) ? string.Join(", ", lp.Select(x => x.Name)) : "";
        d["Path"] = _paths.TryGetValue(c.Id, out var segs) ? string.Join(" › ", segs.Select(s => s.Label)) : "";
        return d;
    }

    private List<PathSeg> GetPath(Container c) =>
        _paths.TryGetValue(c.Id, out var segs) ? segs : new() { new("Container", c.Id, c.ContainerName) };

    private (string Kind, int Id)? GetHomeRef(Container c) =>
        c.HomeKind != null && c.HomeRefId != null ? (c.HomeKind, c.HomeRefId.Value) : null;

    private (string Kind, int Id)? GetAssigneeRef(Container c) =>
        c.AssigneeKind != null && c.AssigneeRefId != null ? (c.AssigneeKind, c.AssigneeRefId.Value) : null;

    private bool HasKids(Container c) => _withChildren.Contains(c.Id);

    private Task<List<ChildItem>> GetKids(Container c) => Rim.GetChildItemsAsync("Container", c.Id);

    private List<(int Id, string Name)> GetChips(Container c) =>
        _labelPairs.TryGetValue(c.Id, out var lp2) ? lp2 : new();

    private Func<GridPageRequest, Task<GridPageResult<Container>>>? _provider
        => ItemsProvider == null ? null : ProvideAsync;

    private async Task<GridPageResult<Container>> ProvideAsync(GridPageRequest req)
    {
        var page = await ItemsProvider!(req);
        await EnrichChunkAsync(page.Rows);
        return page;
    }

    public async Task EnrichChunkAsync(List<Container> rows)
    {
        var ids = rows.Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        foreach (var kv in await Rim.GetObjectLabelPairsAsync("Container", ids)) _labelPairs[kv.Key] = kv.Value;
        foreach (var id in await Rim.GetHasChildrenAsync("Container", ids)) _withChildren.Add(id);
        foreach (var kv in await Rim.GetAncestorPathsAsync("Container", ids)) _paths[kv.Key] = kv.Value;
    }

    private void ResetSupplemental()
    {
        _withChildren.Clear(); _labelPairs.Clear();
        _paths.Clear();
    }

    public void ClearSelection() => _grid?.ClearSelection();
    public Task ResetAsync() => _grid?.ResetAsync() ?? Task.CompletedTask;
}
