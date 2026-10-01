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

namespace Rim.Components.Dialogs;

public partial class ArchiveFileDialog : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public string FileName { get; set; } = "";

    private List<AuditEventArchive> _rows = new();

    protected override async Task OnInitializedAsync()
        => _rows = await Rim.ReadAuditExportAsync(FileName);
}
