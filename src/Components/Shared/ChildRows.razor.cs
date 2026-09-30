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

namespace Prim.Components.Shared;

public partial class ChildRows : ComponentBase
{
    [Parameter] public Func<Task<List<ChildItem>>> LoadAsync { get; set; } = () => Task.FromResult(new List<ChildItem>());
    [Parameter] public EventCallback<ChildItem> OnNavigate { get; set; }

    private List<ChildItem> _kids = new();
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        _kids = await LoadAsync();
        _loading = false;
    }

    private static string IconFor(string kind) => kind switch
    {
        "Record" => Icons.Material.Filled.Description,
        "Container" => Icons.Material.Filled.Inventory2,
        "Location" => Icons.Material.Filled.Place,
        "User" => Icons.Material.Filled.Person,
        _ => Icons.Material.Filled.Circle,
    };
}
