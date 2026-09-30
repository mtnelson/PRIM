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

namespace Prim.Components.Pages;

public partial class Reports : ComponentBase
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public IJSRuntime JS { get; set; } = default!;

    private Dictionary<string, int> _counts = new();
    private double[] _typeData = Array.Empty<double>();
    private string[] _typeLabels = Array.Empty<string>();
    private double[] _foData = Array.Empty<double>();
    private string[] _foLabels = Array.Empty<string>();

    protected override async Task OnInitializedAsync()
    {
        _counts = await Prim.GetCountsAsync();

        var byType = await Prim.GroupRecordsAsync(r => r.RecordType);
        _typeData = byType.Select(g => (double)g.Count).ToArray();
        _typeLabels = byType.Select(g => $"{g.Label} ({g.Count})").ToArray();

        var byOffice = await Prim.GroupRecordsAsync(r => r.FieldOffice);
        _foData = byOffice.Select(g => (double)g.Count).ToArray();
        _foLabels = byOffice.Select(g => $"{g.Label} ({g.Count})").ToArray();
    }

    private async Task ExportCounts()
    {
        var csv = "Metric,Count\r\n" + string.Join("\r\n",
            _counts.Select(kv => $"{Csv.Escape(kv.Key)},{kv.Value}"));
        await Csv.DownloadAsync(JS, "prim-totals.csv", csv);
    }
}
