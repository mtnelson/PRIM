using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Prim.Components.Dialogs;
using Prim.Data;
using Prim.Services;

namespace Prim.Components.Pages;

public partial class BarcodeScan : ComponentBase
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public IDialogService DialogService { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    private const string ActWorkspace = "Workspace";
    private const string ActHome = "Home";
    private const string ActAssignee = "Assignee";
    private const string ActHomeAssignee = "HomeAssignee";

    private static readonly string[] Slots =
        { "Workspace 1", "Workspace 2", "Workspace 3", "Workspace 4", "Workspace 5", "Favorites" };

    private string _action = ActWorkspace;
    private string _slot = "Workspace 1";
    private string _dest = "";
    private string _assigneeBarcode = "";
    private string _homeBarcode = "";
    private string _objects = "";
    private bool _running;

    private MudTextField<string>? _destField;
    private MudTextField<string>? _objectsField;
    private MudTextField<string>? _assigneeField;
    private MudTextField<string>? _homeField;

    private string ActionButtonLabel => _action switch
    {
        ActWorkspace => $"Add to {_slot}",
        ActHome => "Set Home",
        ActAssignee => "Set Assignee",
        _ => "Set Home & Assignee",
    };

    private string ActionTitle => _action switch
    {
        ActWorkspace => $"Add to {_slot}",
        ActHome => "Add to Home or Container",
        ActAssignee => "Add to Assignee",
        _ => "Add to Home & Assignee",
    };

    // Keyboard-wedge behavior: Enter in a single-line field advances focus
    // to the next field. (The multi-line scan field keeps Enter as newline.)
    private async Task AdvanceOnEnter(KeyboardEventArgs e, MudTextField<string>? next)
    {
        if (e.Key == "Enter" && next != null)
            await next.FocusAsync();
    }

    private static List<string> ParseBarcodes(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task Execute()
    {
        if (_running) return;
        var codes = ParseBarcodes(_objects);
        if (codes.Count == 0)
        {
            Snackbar.Add("Scan at least one object barcode first.", Severity.Warning);
            return;
        }
        if (_action == ActHome && string.IsNullOrWhiteSpace(_dest))
        {
            Snackbar.Add("Scan the destination barcode first.", Severity.Warning);
            return;
        }
        if ((_action == ActAssignee || _action == ActHomeAssignee) && string.IsNullOrWhiteSpace(_assigneeBarcode))
        {
            Snackbar.Add("Scan the assignee barcode first.", Severity.Warning);
            return;
        }
        if (_action == ActHomeAssignee && string.IsNullOrWhiteSpace(_homeBarcode))
        {
            Snackbar.Add("Scan the home barcode first.", Severity.Warning);
            return;
        }

        _running = true;
        try
        {
            PrimService.BarcodeActionResult result = _action switch
            {
                ActWorkspace => await Prim.BarcodeAddToSlotAsync(App.CurrentUserId, _slot, codes),
                ActHome => await Prim.BarcodeSetHomeAsync(codes, _dest, App.CurrentUserId),
                ActAssignee => await Prim.BarcodeSetAssigneeAsync(codes, _assigneeBarcode, App.CurrentUserId),
                _ => await Prim.BarcodeSetHomeAndAssigneeAsync(codes, _homeBarcode, _assigneeBarcode, App.CurrentUserId),
            };

            // Failed barcodes stay in the scan field for correction/retry;
            // successful ones are cleared.
            _objects = string.Join("\n",
                result.Outcomes.Where(o => !o.Ok).Select(o => o.Barcode));

            App.Log($"Barcode: {ActionTitle}",
                $"{result.SuccessCount} succeeded, {result.FailCount} failed");
            if (result.FailCount > 0)
                App.LogItems("Barcode failures", result.Outcomes.Where(o => !o.Ok)
                    .Select(o => $"{o.Barcode}: {o.Message}"));

            await DialogService.ShowAsync<BarcodeResultDialog>("Barcode results",
                new DialogParameters
                {
                    ["Title"] = ActionTitle,
                    ["Result"] = result,
                },
                new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Barcode action failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _running = false;
        }
    }
}
