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

public partial class SearchTabs : ComponentBase
{
    [Parameter] public List<SearchSession> Tabs { get; set; } = new();
    [Parameter] public int ActiveId { get; set; }
    [Parameter] public EventCallback<SearchSession> OnActivate { get; set; }
    [Parameter] public EventCallback<SearchSession> OnClose { get; set; }
    [Parameter] public EventCallback OnAdd { get; set; }
}
