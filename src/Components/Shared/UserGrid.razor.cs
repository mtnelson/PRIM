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

public partial class UserGrid : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;

    private ObjectGrid<AppUser>? _grid;

    [Parameter] public IEnumerable<AppUser> Items { get; set; } = Enumerable.Empty<AppUser>();
    [Parameter] public Func<GridPageRequest, Task<GridPageResult<AppUser>>>? ItemsProvider { get; set; }
    [Parameter] public Func<Task<int>>? CountProvider { get; set; }
    [Parameter] public Func<Task<List<int>>>? AllIdsProvider { get; set; }
    [Parameter] public string GridId { get; set; } = "users";
    [Parameter] public string Scope { get; set; } = "users";
    [Parameter] public Func<AppUser, Task<bool>>? EditItem { get; set; }
    [Parameter] public Func<List<int>, Task<bool>>? DeleteItems { get; set; }
    [Parameter] public bool CanMove { get; set; } = true;
    [Parameter] public bool CanDelete { get; set; } = true;
    [Parameter] public bool ShowRemoveFromSlot { get; set; }
    [Parameter] public EventCallback<List<int>> RemoveFromSlot { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }
    // Search-tab state, passed through to the inner ObjectGrid.
    [Parameter] public SearchTabState? TabState { get; set; }
    [Parameter] public EventCallback TabStateChanged { get; set; }

    private HashSet<int> _withChildren = new();
    private Dictionary<int, List<(int Id, string Name)>> _labelPairs = new();

    protected override async Task OnParametersSetAsync()
    {
        if (ItemsProvider != null) return; // provider mode enriches per chunk
        _withChildren = await Rim.GetHasChildrenAsync("User", Items.Select(u => u.Id));
        _labelPairs = await Rim.GetObjectLabelPairsAsync("User", Items.Select(u => u.Id));
    }

    private Dictionary<string, string> RowWithLabels(AppUser u)
    {
        var d = GridColumns.UserRow(u);
        d["Labels"] = _labelPairs.TryGetValue(u.Id, out var lp) ? string.Join(", ", lp.Select(x => x.Name)) : "";
        return d;
    }

    private List<(int Id, string Name)> GetChips(AppUser u) =>
        _labelPairs.TryGetValue(u.Id, out var lp2) ? lp2 : new();

    private bool HasKids(AppUser u) => _withChildren.Contains(u.Id);

    private Task<List<ChildItem>> GetKids(AppUser u) => Rim.GetChildItemsAsync("User", u.Id);

    private Func<GridPageRequest, Task<GridPageResult<AppUser>>>? _provider
        => ItemsProvider == null ? null : ProvideAsync;

    private async Task<GridPageResult<AppUser>> ProvideAsync(GridPageRequest req)
    {
        var page = await ItemsProvider!(req);
        await EnrichChunkAsync(page.Rows);
        return page;
    }

    public async Task EnrichChunkAsync(List<AppUser> rows)
    {
        var ids = rows.Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        foreach (var kv in await Rim.GetObjectLabelPairsAsync("User", ids)) _labelPairs[kv.Key] = kv.Value;
        foreach (var id in await Rim.GetHasChildrenAsync("User", ids)) _withChildren.Add(id);
    }

    private void ResetSupplemental()
    {
        _withChildren.Clear(); _labelPairs.Clear();
    }

    public void ClearSelection() => _grid?.ClearSelection();
    public Task ResetAsync() => _grid?.ResetAsync() ?? Task.CompletedTask;
}
