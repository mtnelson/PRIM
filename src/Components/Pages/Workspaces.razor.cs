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

public partial class Workspaces : ComponentBase, IDisposable
{
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public HotkeyManager Hotkeys { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    private static readonly string[] Slots =
        { "Workspace 1", "Workspace 2", "Workspace 3", "Workspace 4", "Workspace 5", "Favorites" };

    private record SlotGroups(List<RecordItem> Records, List<Container> Containers,
        List<Location> Locations, List<AppUser> Users)
    {
        public bool IsEmpty => Records.Count == 0 && Containers.Count == 0
            && Locations.Count == 0 && Users.Count == 0;
    }

    private static readonly SlotGroups Empty = new(new(), new(), new(), new());
    private Dictionary<string, SlotGroups> _groups = new();
    private bool _loading;
    private int _tab;

    private static string GridIdFor(string slot, string kind) =>
        "ws-" + slot.ToLowerInvariant().Replace(" ", "-") + "-" + kind;

    protected override async Task OnInitializedAsync()
    {
        Hotkeys.PushScope("workspaces");
        Hotkeys.Register("workspaces", "F9", Refresh);
        await Load();
    }

    public void Dispose() => Hotkeys.UnregisterScope("workspaces");

    private async Task Refresh() { await Load(); Snackbar.Add("Workspaces refreshed.", Severity.Info); }

    private async Task Load()
    {
        _loading = true;
        try
        {
            var next = new Dictionary<string, SlotGroups>();
            foreach (var s in Slots)
            {
                var items = await Rim.GetSlotAsync(App.CurrentUserId, s);
                next[s] = new SlotGroups(
                    await Rim.GetRecordsByIdsAsync(items.Where(w => w.ObjectKind == "Record").Select(w => w.ObjectId)),
                    await Rim.GetContainersByIdsAsync(items.Where(w => w.ObjectKind == "Container").Select(w => w.ObjectId)),
                    await Rim.GetLocationsByIdsAsync(items.Where(w => w.ObjectKind == "Location").Select(w => w.ObjectId)),
                    await Rim.GetUsersByIdsAsync(items.Where(w => w.ObjectKind == "User").Select(w => w.ObjectId)));
            }
            _groups = next;
        }
        finally { _loading = false; }
        StateHasChanged();
    }

    // Delete on a workspace grid removes the selected item(s) from the slot,
    // never the underlying object.
    private async Task RemoveSelected(string slot, string kind, List<int> ids)
    {
        await using var busy = BusyToast.Show(Snackbar, $"Removing {ids.Count:N0} item(s) from {slot}…");
        var labels = new List<string>();
        int i = 0;
        foreach (var id in ids)
        {
            labels.Add(await Rim.GetObjectLabelAsync(kind, id));
            await Rim.RemoveFromSlotAsync(App.CurrentUserId, slot, kind, id);
            if (++i % 500 == 0)
            {
                busy.Update($"Removing {i:N0} of {ids.Count:N0} item(s) from {slot}…");
                await BusyToast.YieldForPaintAsync();
            }
        }
        busy.Complete($"Removed {ids.Count:N0} item(s) from {slot}.");
        App.LogItems("Removed from " + slot, labels);
        await Load();
    }
}
