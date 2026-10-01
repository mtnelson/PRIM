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

public partial class GridContextMenu : ComponentBase
{
    [Parameter] public RenderFragment? Activator { get; set; }
    [Parameter] public string HeaderText { get; set; } = "";
    [Parameter] public bool NoTarget { get; set; }
    [Parameter] public bool CanMove { get; set; } = true;
    [Parameter] public bool CanDelete { get; set; } = true;
    [Parameter] public bool ShowRemove { get; set; }
    [Parameter] public EventCallback OnView { get; set; }
    [Parameter] public EventCallback OnEdit { get; set; }
    [Parameter] public EventCallback OnMove { get; set; }
    [Parameter] public EventCallback OnDelete { get; set; }
    [Parameter] public EventCallback OnAudit { get; set; }
    [Parameter] public EventCallback<string> OnAddToSlot { get; set; }
    [Parameter] public EventCallback OnRemoveFromSlot { get; set; }
    [Parameter] public EventCallback OnPrint { get; set; }
    [Parameter] public EventCallback OnExport { get; set; }
    [Parameter] public EventCallback OnCopy { get; set; }
    [Parameter] public EventCallback OnSelectAll { get; set; }
    [Parameter] public EventCallback OnUnselectAll { get; set; }
}
