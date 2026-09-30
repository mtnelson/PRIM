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

namespace Prim.Components.Dialogs;

public partial class ArchiveFileDialog : ComponentBase
{
    [Inject] public PrimService Prim { get; set; } = default!;

    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public string FileName { get; set; } = "";

    private List<AuditEventArchive> _rows = new();

    protected override async Task OnInitializedAsync()
        => _rows = await Prim.ReadAuditExportAsync(FileName);
}
