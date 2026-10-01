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

public partial class AdvancedSearch : ComponentBase, IDisposable
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public SearchQueryLog QueryLog { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    private class AdvRow
    {
        public string Field { get; set; } = "CaseNumber";
        public string Op { get; set; } = "Contains";
        public string Value { get; set; } = "";
    }

    // Object type being searched: Record | Container | Location | User.
    // Persisted in the session's CriteriaJson ("kind") so tab restore
    // re-selects the radio and re-runs the right query.
    private string _kind = "Record";
    private string _kindLabel => _kind switch
    {
        "Container" => "Containers",
        "Location" => "Locations",
        "User" => "Users",
        _ => "Records",
    };

    private static readonly Dictionary<string, List<(string Key, string Label)>> AdvFieldsByKind = new()
    {
        ["Record"] = new()
        {
            ("RecordNumber","Record Number"), ("RecordType","Record Type"),
            ("CaseClassification","Case Classification"), ("FieldOffice","Field Office"),
            ("CaseNumber","Case Number"), ("SubfileId","Subfile ID"),
            ("Volume","Volume"), ("SerialStart","Serial Start"), ("SerialEnd","Serial End"),
            ("Barcode","Barcode"), ("Home","Home"), ("Assignee","Assignee"),
            ("Subject","Subject"), ("State","State"),
        },
        ["Container"] = new()
        {
            ("ContainerName","Container Name"), ("ContainerType","Container Type"),
            ("FieldOffice","Field Office"), ("ContainerCode","Container Code"),
            ("FormattedNumber","Formatted Number"), ("Description","Description"),
            ("Home","Home"), ("Assignee","Assignee"), ("Barcode","Barcode"),
        },
        ["Location"] = new()
        {
            ("LocationName","Location Name"), ("LocationType","Location Type"),
            ("Description","Description"), ("Barcode","Barcode"),
        },
        ["User"] = new()
        {
            ("UserId","User ID"), ("DisplayName","Display Name"),
            ("Role","Role"), ("Email","Email"),
        },
    };

    private List<(string Key, string Label)> _advFields => AdvFieldsByKind[_kind];
    private AdvRow NewRow() => new AdvRow { Field = AdvFieldsByKind[_kind][0].Key };

    protected override void OnInitialized()
    {
        Hotkeys.PushScope("advanced");
        Hotkeys.Register("advanced", "F9", RunSearch);
        QueryLog.Changed += OnQueryLogChanged;
    }

    // A page chunk loading in the background only needs a re-render when the
    // user is actually looking at the SQL tab.
    private void OnQueryLogChanged()
    {
        if (_resultTab == 1)
            _ = InvokeAsync(StateHasChanged);
    }

    private void OnResultTabChanged(int index) => _resultTab = index;

    protected override async Task OnInitializedAsync()
    {
        _tabs = await Prim.GetOpenSessionsAsync(App.CurrentUserId, "advanced");
        _activity = await Prim.GetSearchActivityAsync(App.CurrentUserId);
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Prim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "advanced",
                Title = "Advanced search", CriteriaJson = "",
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.FirstOrDefault();
        if (_activeTab != null)
        {
            _tabState = _activeTab.ToTabState();
            // Restore the most-recent search: re-run it server-side.
            if (TryParseCriteria(_activeTab.CriteriaJson))
                await ExecuteSearchAsync(restore: true);
        }
    }

    public void Dispose()
    {
        Hotkeys.UnregisterScope("advanced");
        QueryLog.Changed -= OnQueryLogChanged;
        // Best-effort: persist the active tab's state when leaving the page.
        _ = SaveActiveTabAsync().ContinueWith(t => { var _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // Search-session tabs: one persisted descriptor per open tab. Switching
    // tabs restores the criteria and re-runs the search server-side.
    private List<SearchSession> _tabs = new();
    private SearchSession? _activeTab;
    private SearchTabState? _tabState;

    private async Task ActivateTab(SearchSession tab)
    {
        if (_activeTab?.Id == tab.Id) return;
        await SaveActiveTabAsync();
        _activeTab = tab;
        _tabState = tab.ToTabState();
        if (TryParseCriteria(tab.CriteriaJson))
            await ExecuteSearchAsync(restore: true);
        else
        {
            // TryParseCriteria already restored the kind and a fresh row set
            // (or safe defaults when the stored JSON was malformed).
            _logic = "AND";
            _searched = false;
        }
    }

    private async Task NewTab()
    {
        await SaveActiveTabAsync();
        var (ok, err, s) = await Prim.CreateSessionAsync(new SearchSession
        {
            OwnerUserId = App.CurrentUserId, PageKind = "advanced",
            Title = $"Advanced {_tabs.Count + 1}", CriteriaJson = "",
        });
        if (!ok || s == null) { Snackbar.Add(err ?? "Could not open a tab.", Severity.Warning); return; }
        _tabs.Add(s);
        _activeTab = s;
        _tabState = s.ToTabState();
        _kind = "Record";
        _rows = new() { NewRow() };
        _logic = "AND";
        _criteria = new();
        _searched = false;
    }

    private async Task CloseTab(SearchSession tab)
    {
        await Prim.DeleteSessionAsync(tab.Id, App.CurrentUserId);
        _tabs.RemoveAll(t => t.Id == tab.Id);
        if (_activeTab?.Id != tab.Id) return;
        if (_tabs.Count == 0)
        {
            var (ok, _, s) = await Prim.CreateSessionAsync(new SearchSession
            {
                OwnerUserId = App.CurrentUserId, PageKind = "advanced",
                Title = "Advanced search", CriteriaJson = "",
            });
            if (ok && s != null) _tabs.Add(s);
        }
        _activeTab = _tabs.OrderByDescending(t => t.LastUsedUtc).FirstOrDefault();
        if (_activeTab == null) return;
        _tabState = _activeTab.ToTabState();
        if (TryParseCriteria(_activeTab.CriteriaJson))
            await ExecuteSearchAsync(restore: true);
        else
        {
            _logic = "AND";
            _searched = false;
        }
    }

    // Switching the object type resets the criteria (fields differ per type),
    // clears the run state, and starts the grid tab state fresh — the stored
    // sort/columns/selection belong to the previous type.
    private async Task OnKindChanged(string kind)
    {
        if (_kind == kind) return;
        _kind = kind;
        _rows = new() { NewRow() };
        _criteria = new();
        _logic = "AND";
        _searched = false;
        _resultsTitle = "";
        _runId = Guid.Empty;
        _tabState = new SearchTabState();
        await SaveActiveTabAsync();
    }

    private Task OnTabStateChanged() => SaveActiveTabAsync();

    private async Task SaveActiveTabAsync()
    {
        if (_activeTab == null || _tabState == null) return;
        _activeTab.CriteriaJson = SerializeCriteria();
        _activeTab.Title = DeriveTitle();
        _activeTab.ApplyTabState(_tabState);
        await Prim.SaveSessionAsync(_activeTab);
    }

    // Full search logic, including the object type — stored untruncated; the
    // tab strip truncates visually (CSS ellipsis) with a hover tooltip.
    private string DeriveTitle()
    {
        var parts = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r =>
            {
                var label = _advFields.FirstOrDefault(f => f.Key == r.Field).Label ?? r.Field;
                return $"{label} {r.Op.ToLower()} {r.Value}".Trim();
            }).ToList();
        if (parts.Count == 0) return $"{_kindLabel} search";
        return $"{_kindLabel}: {string.Join($" {_logic} ", parts)}";
    }

    private string SerializeCriteria()
    {
        var rows = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => new { field = r.Field, op = r.Op, value = r.Value });
        return System.Text.Json.JsonSerializer.Serialize(new { logic = _logic, kind = _kind, rows });
    }

    private bool TryParseCriteria(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            _logic = root.GetProperty("logic").GetString() ?? "AND";
            // Old sessions have no "kind" — they are record searches.
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
            _kind = AdvFieldsByKind.ContainsKey(kind ?? "") ? kind! : "Record";
            _rows = root.GetProperty("rows").EnumerateArray()
                .Select(e => new AdvRow
                {
                    Field = e.GetProperty("field").GetString() ?? AdvFieldsByKind[_kind][0].Key,
                    Op = e.GetProperty("op").GetString() ?? "Contains",
                    Value = e.GetProperty("value").GetString() ?? "",
                })
                .Where(r => r.Value != null)
                .ToList();
            if (_rows.Count == 0) _rows.Add(NewRow());
            return _rows.Any(r => !string.IsNullOrWhiteSpace(r.Value));
        }
        catch
        {
            // Malformed JSON: fall back to safe defaults rather than leaving
            // half-parsed state behind.
            _kind = "Record";
            _logic = "AND";
            _rows = new() { NewRow() };
            return false;
        }
    }

    private List<AdvRow> _rows = new() { new AdvRow() };
    private string _logic = "AND";
    private bool _searched = false;
    private bool _searching = false;
    private string _resultsTitle = "";
    private List<(string Field, string Op, string Value)> _criteria = new();
    private RecordGrid? _recordGrid;
    private ContainerGrid? _containerGrid;
    private LocationGrid? _locationGrid;
    private UserGrid? _userGrid;

    // Server-side providers per object type: filtering and paging both happen
    // in the database; the grid fetches 500-row chunks as the user scrolls.
    // Every query carries the current run's tag so the SQL tab can show the
    // exact SQL of the most recent search (page chunks included).
    private Guid _runId = Guid.Empty;
    private string TagFor(string role) => $"AdvancedSearch:{_kind}:{_runId:N}:{role}";

    private Task<GridPageResult<RecordItem>> ProvideRecordResults(GridPageRequest req) =>
        Prim.AdvancedSearchRecordsPageAsync(_criteria, _logic, req, TagFor("page"));
    private Task<GridPageResult<Container>> ProvideContainerResults(GridPageRequest req) =>
        Prim.AdvancedSearchContainersPageAsync(_criteria, _logic, req, TagFor("page"));
    private Task<GridPageResult<Location>> ProvideLocationResults(GridPageRequest req) =>
        Prim.AdvancedSearchLocationsPageAsync(_criteria, _logic, req, TagFor("page"));
    private Task<GridPageResult<AppUser>> ProvideUserResults(GridPageRequest req) =>
        Prim.AdvancedSearchUsersPageAsync(_criteria, _logic, req, TagFor("page"));

    private Task<int> CountResults() => _kind switch
    {
        "Container" => Prim.AdvancedSearchContainersCountAsync(_criteria, _logic, TagFor("count")),
        "Location" => Prim.AdvancedSearchLocationsCountAsync(_criteria, _logic, TagFor("count")),
        "User" => Prim.AdvancedSearchUsersCountAsync(_criteria, _logic, TagFor("count")),
        _ => Prim.AdvancedSearchRecordsCountAsync(_criteria, _logic, TagFor("count")),
    };

    private Task<List<int>> AllResultIds() => _kind switch
    {
        "Container" => Prim.AdvancedSearchContainerIdsAsync(_criteria, _logic, TagFor("ids")),
        "Location" => Prim.AdvancedSearchLocationIdsAsync(_criteria, _logic, TagFor("ids")),
        "User" => Prim.AdvancedSearchUserIdsAsync(_criteria, _logic, TagFor("ids")),
        _ => Prim.AdvancedSearchRecordIdsAsync(_criteria, _logic, TagFor("ids")),
    };

    private Task ResetActiveGridAsync() => _kind switch
    {
        "Container" => _containerGrid?.ResetAsync() ?? Task.CompletedTask,
        "Location" => _locationGrid?.ResetAsync() ?? Task.CompletedTask,
        "User" => _userGrid?.ResetAsync() ?? Task.CompletedTask,
        _ => _recordGrid?.ResetAsync() ?? Task.CompletedTask,
    };

    private async Task RunSearch()
    {
        var rows = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (rows.Count == 0) { Snackbar.Add("Enter at least one criterion.", Severity.Info); return; }
        await ExecuteSearchAsync(restore: false);
        App.Log("Advanced search", _resultsTitle);
    }

    // Commits the criteria editor and runs the search. On restore (tab
    // switch) the grid picks up the new TabState and re-fetches by itself;
    // on a manual run the existing grid is reset explicitly. Each execution
    // gets a fresh run id so the SQL tab shows exactly this run's queries,
    // and the run is recorded in the persistent search-activity log.
    private async Task ExecuteSearchAsync(bool restore)
    {
        _criteria = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => (r.Field, r.Op, r.Value)).ToList();
        if (_criteria.Count == 0) { _searched = false; return; }
        _runId = Guid.NewGuid();
        _searching = true;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var count = await CountResults();
            sw.Stop();
            var unit = _kind switch
            {
                "Container" => "container", "Location" => "location",
                "User" => "user", _ => "record",
            };
            _resultsTitle = $"{count} {unit}{(count == 1 ? "" : "s")} found";
            _searched = true;
            _resultTab = 0; // back to Results on every new search
            if (!restore) await ResetActiveGridAsync();
            await SaveActiveTabAsync();
            await Prim.LogSearchActivityAsync(new SearchActivity
            {
                UserId = App.CurrentUserId,
                TimestampUtc = DateTime.UtcNow,
                ObjectKind = _kind,
                Logic = _logic,
                CriteriaSummary = DeriveTitle(),
                ResultCount = count,
                DurationMs = sw.Elapsed.TotalMilliseconds,
            });
            _activity = await Prim.GetSearchActivityAsync(App.CurrentUserId);
        }
        finally { _searching = false; }
    }

    private List<SearchActivity> _activity = new();
    private int _resultTab = 0; // 0 = Results, 1 = SQL, 2 = Activity

    private async Task CopySql(string sql)
    {
        var ok = await PrimJs.TryInvokeAsync<bool>(JS, "prim.copyText", sql);
        Snackbar.Add(ok ? "SQL copied to clipboard." : "Could not copy SQL.",
            ok ? Severity.Success : Severity.Warning);
    }

    // Per-type edit/delete handlers, mirroring the corresponding object pages.
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
            var n = await Prim.DeleteContainersAsync(ids, App.CurrentUserId);
            busy.Complete($"Deleted {n:N0} container(s).");
            var names = new List<string>();
            foreach (var id in ids) names.Add(await Prim.GetObjectLabelAsync("Container", id));
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
