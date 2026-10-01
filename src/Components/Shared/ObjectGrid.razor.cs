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

public partial class ObjectGrid<T> : ComponentBase, IDisposable
{
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [Inject] public NavigationManager Nav { get; set; } = default!;

    [Parameter] public IEnumerable<T> Items { get; set; } = Enumerable.Empty<T>();
    // Virtualized mode: when set, the grid renders only the visible window
    // (+overscan) and fetches 500-row chunks from this provider on demand
    // instead of rendering a pre-loaded Items list.
    [Parameter] public Func<GridPageRequest, Task<GridPageResult<T>>>? ItemsProvider { get; set; }
    [Parameter] public int PageSize { get; set; } = 500;
    // Virtualized mode: total matching rows for the current filter (single
    // indexed COUNT(*)). Invoked once per query; cached until the next reset.
    [Parameter] public Func<Task<int>>? CountProvider { get; set; }
    // Virtualized mode: every matching Id in Id order, for select-all across
    // rows that are not currently loaded.
    [Parameter] public Func<Task<List<int>>>? AllIdsProvider { get; set; }
    // Fixed scroll height of the virtualized grid body. Null selects the
    // default: provider-mode grids fill the viewport, Items-mode grids
    // (dashboard, workspaces) use a compact 400px so each grid scrolls
    // instead of growing the page without bound.
    [Parameter] public string? GridHeight { get; set; }
    private string EffectiveHeight =>
        GridHeight ?? (ItemsProvider != null ? "calc(100dvh - 300px)" : "400px");
    // Called when the chunk pipeline resets (filter/sort change) so wrappers
    // can drop per-row supplemental caches (paths, child flags, labels).
    [Parameter] public Action? OnReset { get; set; }
    // Search-tab state (one instance per open tab, owned by the page). The
    // grid applies it when a tab is activated and writes user-driven changes
    // (sort, selection, expansion, column layout) back into it, firing
    // TabStateChanged so the page can persist the session descriptor.
    // Null on pages without tabs: behavior is exactly as before.
    [Parameter] public SearchTabState? TabState { get; set; }
    [Parameter] public EventCallback TabStateChanged { get; set; }
    [Parameter] public string Kind { get; set; } = "";
    [Parameter] public string GridId { get; set; } = "";
    [Parameter] public string Scope { get; set; } = "";
    [Parameter] public Func<T, (string Kind, int Id, string Label)> GetInfo { get; set; } = _ => ("", 0, "");
    [Parameter] public Func<T, string> GetBarcode { get; set; } = _ => "";
    [Parameter] public Func<T, Task<bool>>? EditItem { get; set; }
    [Parameter] public Func<List<int>, Task<bool>>? DeleteItems { get; set; }
    [Parameter] public List<(string Key, string Label)> ExportCols { get; set; } = new();
    [Parameter] public Func<T, Dictionary<string, string>>? RowToDict { get; set; }
    [Parameter] public bool CanMove { get; set; } = true;
    [Parameter] public bool CanDelete { get; set; } = true;
    [Parameter] public bool ShowRemoveFromSlot { get; set; } = false;
    [Parameter] public EventCallback<List<int>> RemoveFromSlot { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }

    // Hierarchy + navigation (expandable child rows, clickable Home/Assignee,
    // breadcrumb Path column, cross-page focus).
    [Parameter] public bool Expandable { get; set; }
    [Parameter] public Func<T, bool>? HasChildren { get; set; }
    [Parameter] public Func<T, Task<List<ChildItem>>>? GetChildrenAsync { get; set; }
    [Parameter] public Func<T, (string Kind, int Id)?>? GetHomeRef { get; set; }
    [Parameter] public Func<T, (string Kind, int Id)?>? GetAssigneeRef { get; set; }
    [Parameter] public Func<T, List<PathSeg>>? GetPath { get; set; }
    // Label chips for the "Labels" column; click navigates to /labels/{id}.
    [Parameter] public Func<T, List<(int Id, string Name)>>? GetLabelChips { get; set; }

    private HashSet<T> _selected = new();
    private T? _active;
    private bool _hasActive;
    // Plain-click single-select: MudDataGrid fires its own selection toggle
    // around RowClick (order not guaranteed), so a pending plain click
    // overrides whatever set the grid reports.
    private bool _singleSelectPending;
    private T? _singleSelectItem;
    private string _menuHeader = "No selection";
    private List<(string Key, string Label)> _effective = new();
    private string _scope => string.IsNullOrEmpty(Scope) ? GridId : Scope;
    // Hotkey registrations owned by this grid; disposing removes only ours so
    // sibling grids sharing the scope (Dashboard, Workspaces) keep theirs.
    private readonly List<IDisposable> _hotkeyRegs = new();
    // Rows force-expanded by a focus request or restored from a search tab
    // (InitiallyExpandedFunc reads this).
    private HashSet<int> _forceExpanded = new();
    // Per-row child lists, loaded lazily on first expansion.
    private readonly Dictionary<int, List<ChildItem>> _childCache = new();

    // Virtualized provider-mode state. The grid renders only the visible
    // window (+overscan); 500-row chunks are fetched on demand through
    // ItemsProvider and cached here. Chunks outside a small neighborhood of
    // the window are evicted, so the DOM and the Blazor circuit stay small
    // no matter how many rows the query matches.
    private MudDataGrid<T>? _mudGrid;
    private readonly Dictionary<int, List<T>> _chunks = new();
    private string _chunkSortKey = "";
    private int _totalItems;
    private bool _totalValid;
    // Generation counter: reset/sort/tab bumps it so chunk fetches from a
    // stale query are discarded instead of polluting the new cache.
    private int _querySeq;
    // Inner grid recreation key: bumped on every reset so the Virtualize
    // component restarts at row 0 with the new query.
    private string _gridKey = "g0";
    private int _gridSeq;
    // Stable IDs selected by a tab descriptor or a cross-page focus request,
    // applied to rows as their chunks stream into view.
    private readonly HashSet<int> _restoreSelectIds = new();
    // Identity comparer: rows are the same object when their stable Ids
    // match, so selection/expansion survive chunk eviction and re-fetch.
    private IEqualityComparer<T>? _comparer;
    private IEqualityComparer<T> Comparer => _comparer ??= new IdComparer<T>(x => GetInfo(x).Id);
    private sealed class IdComparer<TItem>(Func<TItem, int> idOf) : IEqualityComparer<TItem>
    {
        public bool Equals(TItem? x, TItem? y) => x is not null && y is not null && idOf(x) == idOf(y);
        public int GetHashCode(TItem obj) => obj is null ? 0 : idOf(obj).GetHashCode();
    }
    private string? _sortCol;
    private bool _sortDesc;
    private bool _disposed;
    // The TabState instance already applied to this grid; a new reference
    // means the page switched tabs.
    private SearchTabState? _appliedTabState;
    // Stable per-column sort delegates, so a GridState sort definition can be
    // mapped back to its column key by reference equality.
    private readonly Dictionary<string, Func<T, object>> _sortFuncs = new();

    private IEnumerable<T> _gridItems => ItemsProvider != null ? _chunks.Values.SelectMany(c => c) : Items;
    // MudDataGrid forbids supplying both Items and ServerData: in provider
    // mode the Items parameter must be null (the grid reads via the
    // VirtualizeServerData delegate).
    private IEnumerable<T>? _mudItems => ItemsProvider != null ? null : Items;
    private Func<GridState<T>, Task<GridData<T>>>? _serveData => null;
    private Func<GridStateVirtualize<T>, CancellationToken, Task<GridData<T>>>? _virtualizeServerData
        => ItemsProvider != null ? ServeVirtualized : null;

    private bool NoTarget => _selected.Count == 0 && !_hasActive;

    // In provider mode only entity-mapped columns can sort server-side;
    // Path/Labels are computed per row and sort client-side in Items mode.
    private bool IsColSortable(string key)
        => ItemsProvider == null || (key != "Path" && key != "Labels");

    private Func<T, object> GetSortFunc(string key)
    {
        if (!_sortFuncs.TryGetValue(key, out var f))
        {
            f = x => CellVal(x, key);
            _sortFuncs[key] = f;
        }
        return f;
    }

    protected override void OnInitialized()
    {
        _effective = ExportCols.ToList();
        _selected = new HashSet<T>(Comparer);
        if (TabState != null)
        {
            _appliedTabState = TabState;
            _sortCol = TabState.SortColumn;
            _sortDesc = TabState.SortDescending;
            _forceExpanded = new HashSet<int>(TabState.ExpandedIds);
        }
        App.Changed += OnAppChanged;
        if (!string.IsNullOrEmpty(_scope))
        {
            _hotkeyRegs.Add(Hotkeys.Register(_scope, "Ctrl+A", SelectAll));
            _hotkeyRegs.Add(Hotkeys.Register(_scope, "Ctrl+C", CopyWithHeaders));
            _hotkeyRegs.Add(Hotkeys.Register(_scope, "F2", MenuEdit));
            _hotkeyRegs.Add(Hotkeys.Register(_scope, "Delete", MenuDelete));
            _hotkeyRegs.Add(Hotkeys.Register(_scope, "Ctrl+P", MenuPrint));
            _hotkeyRegs.Add(Hotkeys.Register(_scope, "Escape", ClearSelection));
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await ResolveColumnLayoutAsync();
    }

    // Tab switch: the page hands over a new SearchTabState instance. Apply
    // it (sort, expansion, columns) and re-run the query server-side from
    // the first chunk — inactive tabs keep no loaded rows.
    protected override async Task OnParametersSetAsync()
    {
        if (TabState != null && !ReferenceEquals(TabState, _appliedTabState))
        {
            _appliedTabState = TabState;
            await ApplyTabStateAsync();
        }
    }

    // Applies the new tab's descriptor. The inner MudDataGrid is keyed by
    // _gridKey, so this render recreates it with clean sort UI — its initial
    // VirtualizeServerData call then fetches the first chunk for the new
    // query. Inactive tabs keep no loaded rows.
    private async Task ApplyTabStateAsync()
    {
        var ts = TabState!;
        _sortCol = ts.SortColumn;
        _sortDesc = ts.SortDescending;
        _forceExpanded = new HashSet<int>(ts.ExpandedIds);
        await ResolveColumnLayoutAsync();
        _chunks.Clear();
        _chunkSortKey = "";
        _totalValid = false;
        _querySeq++;
        _gridKey = $"g{++_gridSeq}";
        _selected.Clear();
        _restoreSelectIds.Clear();
        if (ts.SelectedIds.Count > 0) _restoreSelectIds.UnionWith(ts.SelectedIds);
        _active = default; _hasActive = false;
        OnReset?.Invoke();
        UpdateMenuHeader();
        StateHasChanged();
    }

    // Column layout precedence: the active tab's saved layout, then the
    // user's profile layout, then the full default column set.
    private async Task ResolveColumnLayoutAsync()
    {
        List<string>? keys = TabState?.ColumnKeys is { Count: > 0 } tabKeys ? tabKeys : null;
        if (keys == null && !string.IsNullOrEmpty(GridId) && !string.IsNullOrEmpty(App.CurrentUserId))
            keys = await Rim.GetGridLayoutAsync(App.CurrentUserId, GridId);
        ApplyLayout(keys ?? new());
    }

    public void Dispose()
    {
        _disposed = true;
        App.Changed -= OnAppChanged;
        foreach (var r in _hotkeyRegs) r.Dispose();
        _hotkeyRegs.Clear();
    }

    private void OnAppChanged() => InvokeAsync(StateHasChanged);

    // Applies a cross-page focus request (from a Home/Assignee link, a Path
    // breadcrumb, or a child row): selects the row, opens the detail pane,
    // and expands its children when asked.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (App.TryTakeFocus(Kind, out var f) && f != null)
        {
            var item = _gridItems.FirstOrDefault(x => GetInfo(x).Id == f.Id);
            if (item is null)
            {
                // Not in this grid's loaded chunks (or filtered out): select it
                // when its chunk scrolls into view, and still show details.
                if (ItemsProvider != null) _restoreSelectIds.Add(f.Id);
                App.SetViewPane(f.Kind, f.Id, await Rim.GetObjectLabelAsync(f.Kind, f.Id));
            }
            else
            {
                _selected = new HashSet<T>(Comparer) { item };
                _active = item; _hasActive = true;
                var (kind, id, label) = GetInfo(item);
                if (TabState != null) TabState.SelectedIds = new HashSet<int> { id };
                App.SetViewPane(kind, id, label);
                if (f.Expand && (HasChildren?.Invoke(item) ?? false))
                    _forceExpanded.Add(id);
                UpdateMenuHeader();
                StateHasChanged();
            }
        }
    }

    // Navigates to an object's page and focuses it there. Works for
    // same-page targets too (the focus request is consumed on re-render).
    private void NavigateToObject(string kind, int id, bool expand)
    {
        App.RequestFocus(kind, id, expand);
        Nav.NavigateTo(kind switch
        {
            "Record" => "/records",
            "Container" => "/containers",
            "Location" => "/locations",
            "User" => "/users",
            _ => "/"
        });
    }

    private void OnChildNavigate(ChildItem c) => NavigateToObject(c.Kind, c.Id, c.HasChildren);

    private async Task<List<ChildItem>> LoadChildren(T item)
    {
        var id = GetInfo(item).Id;
        if (!_childCache.TryGetValue(id, out var kids))
        {
            kids = await GetChildrenAsync!(item);
            _childCache[id] = kids;
        }
        // Record the expansion in the tab state so it survives tab switches.
        // (Collapse isn't observable from the grid; an expanded row stays
        // expanded for the tab session once opened.)
        if (TabState != null && TabState.ExpandedIds.Add(id))
        {
            _forceExpanded.Add(id);
            await NotifyTabStateChanged();
        }
        return kids;
    }

    private void ApplyLayout(List<string> keys)
    {
        var labels = ExportCols.ToDictionary(c => c.Key, c => c.Label);
        _effective = keys.Where(labels.ContainsKey).Select(k => (k, labels[k])).ToList();
        if (_effective.Count == 0) _effective = ExportCols.ToList();
    }

    private async Task CustomizeColumns()
    {
        var p = new DialogParameters
        {
            ["Available"] = ExportCols,
            ["Current"] = _effective.Select(c => c.Key).ToList()
        };
        var d = await DialogService.ShowAsync<ColumnPickerDialog>("Customize Columns", p,
            new DialogOptions { MaxWidth = MaxWidth.Small });
        var res = await d.Result;
        if (res is { Canceled: false, Data: List<string> keys } && !string.IsNullOrEmpty(GridId))
        {
            await Rim.SaveGridLayoutAsync(App.CurrentUserId, GridId, keys);
            ApplyLayout(keys);
            if (TabState != null)
            {
                TabState.ColumnKeys = keys.ToList();
                await NotifyTabStateChanged();
            }
            App.Log("Customized columns", $"{Kind} grid");
            Snackbar.Add("Column layout saved to your profile.", Severity.Success);
        }
    }

    // ---------- Virtualized chunk pipeline ----------

    // Serves the grid's visible window (+overscan). The total comes from a
    // single indexed COUNT(*) per query; rows come from 500-row chunk fetches
    // (keyset cursor on default Id ordering, offset otherwise). Only chunks
    // near the window are kept — the DOM never holds the whole result set.
    private async Task<GridData<T>> ServeVirtualized(GridStateVirtualize<T> state, CancellationToken ct)
    {
        if (ItemsProvider == null || _disposed)
            return new GridData<T> { Items = new List<T>(), TotalItems = 0 };
        var (sortCol, sortDesc, sortChanged) = ResolveSort(state);
        var sortKey = $"{sortCol}|{sortDesc}";
        var seq = _querySeq;
        if (sortKey != _chunkSortKey)
        {
            _chunks.Clear();
            _chunkSortKey = sortKey;
            _querySeq++; seq = _querySeq;
        }
        try
        {
            if (CountProvider != null && !_totalValid)
            {
                _totalItems = await CountProvider();
                _totalValid = true;
            }
            var window = await GetWindowAsync(state.StartIndex, state.Count, sortCol, sortDesc, seq, ct);
            if (sortChanged) await NotifyTabStateChanged();
            if (!_disposed) StateHasChanged();
            return new GridData<T> { Items = window, TotalItems = _totalItems };
        }
        catch (OperationCanceledException)
        {
            return new GridData<T> { Items = new List<T>(), TotalItems = _totalItems };
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Could not load rows: {ex.Message}", Severity.Error);
            return new GridData<T> { Items = new List<T>(), TotalItems = _totalItems };
        }
    }

    // Maps the grid's sort UI (or the tab's stored sort on first load) to an
    // entity sort column. A real header interaction overrides the tab sort
    // and persists it back to the tab descriptor.
    private (string? SortColumn, bool SortDescending, bool Changed) ResolveSort(GridStateVirtualize<T> state)
    {
        var def = state.SortDefinitions.FirstOrDefault();
        string? key = null;
        var desc = false;
        if (def?.SortFunc != null)
        {
            key = _sortFuncs.FirstOrDefault(kv => ReferenceEquals(kv.Value, def.SortFunc)).Key;
            desc = def.Descending;
        }
        else
        {
            // No header sort: keep whatever the tab (or the last header
            // interaction) established; null means default Id ordering.
            key = _sortCol;
            desc = _sortDesc;
        }
        var changed = key != _sortCol || desc != _sortDesc;
        if (changed)
        {
            _sortCol = key; _sortDesc = desc;
            if (TabState != null)
            {
                TabState.SortColumn = key;
                TabState.SortDescending = desc;
                TabState.SelectedIds.Clear();
            }
        }
        return (key, desc, changed);
    }

    private Task NotifyTabStateChanged()
        => TabStateChanged.HasDelegate ? TabStateChanged.InvokeAsync() : Task.CompletedTask;

    // Returns the rows for [start, start+count), fetching any missing chunks.
    private async Task<List<T>> GetWindowAsync(int start, int count, string? sortCol, bool sortDesc, int seq, CancellationToken ct)
    {
        var result = new List<T>(Math.Min(count, PageSize * 2));
        if (count <= 0 || start < 0) return result;
        var first = start / PageSize;
        var last = (start + count - 1) / PageSize;
        // Evict chunks outside a small neighborhood of the window.
        foreach (var k in _chunks.Keys.Where(k => k < first - 4 || k > last + 4).ToList())
            _chunks.Remove(k);
        for (var i = first; i <= last; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (seq != _querySeq || _disposed) return new List<T>(); // stale query — abandon
            if (!_chunks.TryGetValue(i, out var chunk))
            {
                int? afterId = null;
                if (string.IsNullOrEmpty(sortCol) && i > 0
                    && _chunks.TryGetValue(i - 1, out var prev) && prev.Count > 0)
                    afterId = GetInfo(prev[^1]).Id;
                chunk = await FetchChunkCoreAsync(i, afterId, sortCol, sortDesc);
                if (seq != _querySeq || _disposed) return new List<T>();
                _chunks[i] = chunk;
                // Tab restore: re-select rows whose stable IDs were selected
                // when the tab was saved, as their chunks stream into view.
                if (_restoreSelectIds.Count > 0)
                    foreach (var r in chunk)
                        if (_restoreSelectIds.Contains(GetInfo(r).Id))
                            _selected.Add(r);
            }
            var off = i == first ? start - i * PageSize : 0;
            var take = Math.Min(chunk.Count - off, start + count - i * PageSize);
            if (take <= 0) break;
            result.AddRange(chunk.Skip(off).Take(take));
        }
        return result;
    }

    // Fetches one 500-row chunk through the page's provider (filter applied
    // there). The wrapper's provider also runs per-chunk enrichment (paths,
    // child flags, labels), so cached chunks are fully display-ready.
    private async Task<List<T>> FetchChunkCoreAsync(int chunkIndex, int? afterId, string? sortCol, bool sortDesc)
    {
        var req = new GridPageRequest
        {
            Skip = chunkIndex * PageSize,
            AfterId = afterId,
            Take = PageSize,
            SortColumn = sortCol,
            SortDescending = sortDesc,
        };
        var page = await ItemsProvider!(req);
        return page.Rows;
    }

    // Drops all cached chunks and re-queries from row 0 — used after filter
    // changes and create/edit/delete operations.
    public Task ResetAsync()
    {
        if (ItemsProvider != null)
        {
            _chunks.Clear();
            _chunkSortKey = "";
            _totalValid = false;
            _querySeq++;
            _gridKey = $"g{++_gridSeq}";
            _selected.Clear();
            _restoreSelectIds.Clear();
            _active = default; _hasActive = false;
            // A reset is a new query within the tab: drop the tab's selection.
            TabState?.SelectedIds.Clear();
        }
        else if (_mudGrid != null)
        {
            _ = _mudGrid.ReloadServerData();
        }
        OnReset?.Invoke();
        UpdateMenuHeader();
        StateHasChanged();
        return Task.CompletedTask;
    }

    public void ClearSelection()
    {
        _selected.Clear(); _active = default; _hasActive = false;
        TabState?.SelectedIds.Clear();
        UpdateMenuHeader(); StateHasChanged();
    }

    private async Task OnSelChanged(HashSet<T> v)
    {
        if (_singleSelectPending && _singleSelectItem is not null)
        {
            _singleSelectPending = false;
            _selected = new HashSet<T>(Comparer) { _singleSelectItem };
        }
        else
        {
            // Normalize onto the ID comparer: the grid hands us instances it
            // rendered, which may be older chunk copies than ours.
            _selected = new HashSet<T>(v, Comparer);
        }
        _restoreSelectIds.Clear();
        UpdateMenuHeader();
        await SyncTabSelectionAsync();
    }

    // Writes the live selection into the tab state (user gestures replace
    // the restore pool) and asks the page to persist the session.
    private async Task SyncTabSelectionAsync()
    {
        if (TabState == null) return;
        TabState.SelectedIds = _selected.Select(x => GetInfo(x).Id).ToHashSet();
        await NotifyTabStateChanged();
    }

    private string RowClass(T item, int index)
    {
        var cls = _selected.Contains(item) ? "rim-row-selected" : "";
        // Rows without children get no caret (the disabled hierarchy button is
        // hidden by CSS); keep the selected highlight independent of that.
        if (Expandable && !(HasChildren?.Invoke(item) ?? false))
            cls += (cls.Length > 0 ? " " : "") + "rim-row-nochildren";
        return cls;
    }

    // CellVal is called once per cell; RowToDict builds the whole row's
    // dictionary, so memoize the last row's dict — a row's cells render
    // consecutively, turning N dict builds per row into one. RowToDict is a
    // pure function of the item, so the cached value is identical.
    private T? _cellDictItem;
    private Dictionary<string, string>? _cellDict;

    private string CellVal(T x, string key)
    {
        if (RowToDict is null) return "";
        if (!ReferenceEquals(x, _cellDictItem))
        {
            _cellDictItem = x;
            _cellDict = RowToDict(x);
        }
        return _cellDict!.GetValueOrDefault(key, "");
    }

    private async Task OnRowClick(DataGridRowClickEventArgs<T> e)
    {
        var toggle = e.MouseEventArgs?.CtrlKey == true || e.MouseEventArgs?.MetaKey == true;
        _active = e.Item; _hasActive = true;
        if (toggle)
        {
            // Ctrl+click (Cmd+click on Mac): toggle without clearing the rest.
            _singleSelectPending = false;
            var next = new HashSet<T>(_selected);
            if (!next.Add(e.Item)) next.Remove(e.Item);
            _selected = next;
            UpdateMenuHeader();
            StateHasChanged();
        }
        else
        {
            // Plain click: single-select (overrides the grid's own toggle).
            _singleSelectPending = true;
            _singleSelectItem = e.Item;
            _selected = new HashSet<T> { e.Item };
            UpdateMenuHeader();
            StateHasChanged();
        }
        await SyncTabSelectionAsync();
        var (kind, id, label) = GetInfo(e.Item);
        App.SetViewPane(kind, id, label);
        UpdateMenuHeader();
    }

    private async Task OnGridDblClick()
    {
        // Two clicks precede the double-click, so _active is the double-clicked row.
        if (_active is null || !_hasActive) return;
        var t = new List<T> { _active };
        if (EditItem is null) return;
        if (await EditItem(t[0])) await OnChanged.InvokeAsync();
    }

    private List<T> Targets()
        => _selected.Count > 0 ? _selected.ToList()
           : (_hasActive && _active is not null ? new() { _active } : new());

    private void UpdateMenuHeader()
    {
        var t = Targets();
        _menuHeader = t.Count switch
        {
            0 => "No selection",
            1 => GetInfo(t[0]).Label,
            _ => $"{t.Count} {Kind.ToLower()}s selected"
        };
    }

    // Ctrl+A across the whole result set: walks the 500-row chunk pipeline
    // with the current sort/filter (enrichment included). Capped — selecting
    // millions of rows would exhaust the circuit's memory.
    private const int MaxSelectAllRows = 50000;

    private async Task SelectAll()
    {
        _selected.Clear();
        if (ItemsProvider == null)
        {
            foreach (var x in Items) _selected.Add(x);
        }
        else if (AllIdsProvider != null && CountProvider != null)
        {
            var total = _totalValid ? _totalItems : await CountProvider();
            var target = Math.Min(total, MaxSelectAllRows);
            if (total > MaxSelectAllRows)
                Snackbar.Add($"Select-all capped at {MaxSelectAllRows:N0} rows — refine the filter to narrow it.", Severity.Warning);
            var seq = _querySeq;
            int? afterId = null;
            await using var busy = BusyToast.Show(Snackbar, $"Selecting {target:N0} row(s)…");
            for (var i = 0; i * PageSize < target; i++)
            {
                if (seq != _querySeq || _disposed) break;
                var rows = await FetchChunkCoreAsync(i, afterId, _sortCol, _sortDesc);
                foreach (var r in rows) _selected.Add(r);
                // Keyset cursor only valid for default Id ordering.
                afterId = string.IsNullOrEmpty(_sortCol) && rows.Count > 0
                    ? GetInfo(rows[^1]).Id : null;
                if ((i + 1) % 10 == 0)
                {
                    busy.Update($"Selecting… {_selected.Count:N0} of {target:N0} row(s)");
                    await BusyToast.YieldForPaintAsync();
                }
                if (rows.Count < PageSize) break;
            }
            busy.Complete($"Selected {_selected.Count:N0} row(s).");
        }
        UpdateMenuHeader();
        StateHasChanged();
        await SyncTabSelectionAsync();
    }
    private async Task UnselectAll()
    {
        _selected.Clear();
        UpdateMenuHeader();
        StateHasChanged();
        await SyncTabSelectionAsync();
    }

    private void MenuView()
    {
        var t = Targets(); if (t.Count == 0) return;
        var (kind, id, label) = GetInfo(t[0]);
        App.SetViewPane(kind, id, label);
    }

    private async Task MenuEdit()
    {
        var t = Targets();
        if (t.Count != 1) { Snackbar.Add("Select exactly one row to edit.", Severity.Info); return; }
        if (EditItem is null) return;
        if (await EditItem(t[0])) await OnChanged.InvokeAsync();
    }

    private async Task MenuMove()
    {
        var t = Targets(); if (t.Count == 0) return;
        var (kind, _, _) = GetInfo(t[0]);
        var p = new DialogParameters { ["Kind"] = kind, ["Ids"] = t.Select(x => GetInfo(x).Id).ToList() };
        var d = await DialogService.ShowAsync<MoveDialog>("Move Items", p,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        var res = await d.Result;
        if (res is { Canceled: false }) await OnChanged.InvokeAsync();
    }

    private async Task MenuDelete()
    {
        var t = Targets(); if (t.Count == 0) return;
        if (DeleteItems is null) { Snackbar.Add("Delete is not available here.", Severity.Info); return; }
        if (await DeleteItems(t.Select(x => GetInfo(x).Id).ToList())) await OnChanged.InvokeAsync();
    }

    private async Task MenuAudit()
    {
        var t = Targets(); if (t.Count != 1) { Snackbar.Add("Select exactly one row.", Severity.Info); return; }
        var (kind, id, label) = GetInfo(t[0]);
        var p = new DialogParameters { ["Kind"] = kind, ["Id"] = id, ["Label"] = label };
        await DialogService.ShowAsync<AuditDialog>("Audit Log", p,
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
    }

    private async Task MenuAddToSlot(string slot)
    {
        var t = Targets(); if (t.Count == 0) return;
        await using var busy = BusyToast.Show(Snackbar, $"Adding {t.Count:N0} item(s) to {slot}…");
        int n = 0;
        var labels = new List<string>();
        int i = 0;
        foreach (var x in t)
        {
            var (kind, id, label) = GetInfo(x);
            if (await Rim.AddToWorkspaceAsync(App.CurrentUserId, slot, kind, id, label)) n++;
            labels.Add(label);
            if (++i % 500 == 0)
            {
                busy.Update($"Adding {i:N0} of {t.Count:N0} item(s) to {slot}…");
                await BusyToast.YieldForPaintAsync();
            }
        }
        busy.Complete($"Added {n:N0} item(s) to {slot}.");
        App.LogItems("Added to " + slot, labels);
    }

    private async Task MenuRemoveFromSlot()
    {
        var t = Targets(); if (t.Count == 0) return;
        if (RemoveFromSlot.HasDelegate)
            await RemoveFromSlot.InvokeAsync(t.Select(x => GetInfo(x).Id).ToList());
    }

    private async Task MenuPrint()
    {
        var t = Targets(); if (t.Count == 0) return;
        var labels = t.Select(x =>
        {
            var (kind, _, label) = GetInfo(x);
            return (Title: label, Line2: kind, Barcode: GetBarcode(x));
        }).ToList();
        var p = new DialogParameters { ["Labels"] = labels };
        await DialogService.ShowAsync<LabelDialog>("Inventory Labels", p, new DialogOptions { MaxWidth = MaxWidth.Small });
    }

    private async Task MenuExport()
    {
        var t = Targets(); if (t.Count == 0) return;
        if (RowToDict is null || _effective.Count == 0) return;
        var rows = t.Select(RowToDict).ToList();
        var cols = _effective;
        var p = new DialogParameters
        {
            ["Columns"] = cols,
            ["OnExport"] = (Func<List<string>, Task>)(async keys =>
            {
                var sel = cols.Where(c => keys.Contains(c.Key)).ToList();
                await using var busy = BusyToast.Show(Snackbar, $"Exporting {rows.Count:N0} row(s)…");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Join(",", sel.Select(c => Csv(c.Label))));
                int er = 0;
                foreach (var row in rows)
                {
                    sb.AppendLine(string.Join(",", sel.Select(c => Csv(row.GetValueOrDefault(c.Key, "")))));
                    if (++er % 500 == 0)
                    {
                        busy.Update($"Exporting {er:N0} of {rows.Count:N0} row(s)…");
                        await BusyToast.YieldForPaintAsync();
                    }
                }
                await RimJs.TryInvokeVoidAsync(JS, "rim.download", $"{Kind.ToLower()}s.csv", sb.ToString(), "text/csv");
                busy.Complete($"Exported {rows.Count:N0} row(s).");
                App.LogItems("Exported CSV", t.Select(x => GetInfo(x).Label));
            })
        };
        await DialogService.ShowAsync<ExportDialog>("Export to CSV", p, new DialogOptions { MaxWidth = MaxWidth.Small });
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    // Context-menu Copy: same full-grid TSV as Ctrl+C, so paste lands in
    // spreadsheet columns exactly as displayed.
    private Task MenuCopy() => CopyWithHeaders();

    // Ctrl+C on the grid: rows WITH column headers, tab-separated for spreadsheets.
    private async Task CopyWithHeaders()
    {
        var t = Targets();
        if (t.Count == 0)
        {
            Snackbar.Add("Select one or more rows to copy.", Severity.Info);
            return;
        }
        if (RowToDict is null || _effective.Count == 0) return;
        await using var busy = BusyToast.Show(Snackbar, $"Copying {t.Count:N0} row(s)…");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join("\t", _effective.Select(c => c.Label)));
        int cr = 0;
        foreach (var x in t)
        {
            var d = RowToDict(x);
            sb.AppendLine(string.Join("\t", _effective.Select(c => d.GetValueOrDefault(c.Key, ""))));
            if (++cr % 500 == 0)
            {
                busy.Update($"Copying {cr:N0} of {t.Count:N0} row(s)…");
                await BusyToast.YieldForPaintAsync();
            }
        }
        var ok = await RimJs.TryInvokeAsync<bool>(JS, "rim.copyText", sb.ToString());
        busy.Complete(ok ? $"Copied {t.Count:N0} row(s) with headers." : "Clipboard unavailable.",
            ok ? Severity.Success : Severity.Warning);
    }
}
