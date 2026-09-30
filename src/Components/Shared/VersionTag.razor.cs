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

public partial class VersionTag : ComponentBase
{
    [Inject] public IDialogService Dialogs { get; set; } = default!;

    private Task ShowChangelog() => Dialogs.ShowAsync<ChangelogDialog>("Changelog",
        new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
}
