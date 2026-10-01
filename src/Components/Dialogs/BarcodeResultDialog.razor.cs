using Microsoft.AspNetCore.Components;
using MudBlazor;
using Rim.Services;

namespace Rim.Components.Dialogs;

public partial class BarcodeResultDialog : ComponentBase
{
    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public string Title { get; set; } = "Barcode results";
    [Parameter] public RimService.BarcodeActionResult Result { get; set; } = new(new());

    private List<RimService.BarcodeOutcome> _failures = new();
    private List<RimService.BarcodeOutcome> _successes = new();

    protected override void OnParametersSet()
    {
        _failures = Result.Outcomes.Where(o => !o.Ok).ToList();
        _successes = Result.Outcomes.Where(o => o.Ok).ToList();
    }
}
