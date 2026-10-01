using Microsoft.AspNetCore.Components;
using MudBlazor;
using Prim.Services;

namespace Prim.Components.Dialogs;

public partial class BarcodeResultDialog : ComponentBase
{
    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public string Title { get; set; } = "Barcode results";
    [Parameter] public PrimService.BarcodeActionResult Result { get; set; } = new(new());

    private List<PrimService.BarcodeOutcome> _failures = new();
    private List<PrimService.BarcodeOutcome> _successes = new();

    protected override void OnParametersSet()
    {
        _failures = Result.Outcomes.Where(o => !o.Ok).ToList();
        _successes = Result.Outcomes.Where(o => o.Ok).ToList();
    }
}
