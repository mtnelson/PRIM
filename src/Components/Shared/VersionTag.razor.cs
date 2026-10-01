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

public partial class VersionTag : ComponentBase
{
    [Inject] public IDialogService Dialogs { get; set; } = default!;

    private Task ShowChangelog() => Dialogs.ShowAsync<ChangelogDialog>("Changelog",
        new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
}
