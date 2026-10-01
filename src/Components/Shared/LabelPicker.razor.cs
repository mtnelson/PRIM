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

public partial class LabelPicker : ComponentBase
{
    [Inject] public RimService Rim { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    [Parameter] public List<string> Selected { get; set; } = new();
    [Parameter] public EventCallback<List<string>> SelectedChanged { get; set; }
    [Parameter] public string Actor { get; set; } = "";

    private List<Label> _all = new();
    private IEnumerable<string> _selected = Enumerable.Empty<string>();
    private string _newName = "";

    protected override async Task OnInitializedAsync()
    {
        _all = await Rim.GetLabelsAsync();
        _selected = Selected.ToList();
    }

    private string MultiText(List<string> names) =>
        names.Count == 0 ? "" : names.Count <= 2 ? string.Join(", ", names) : $"{names.Count} labels";

    private async Task OnSelectedChanged(IEnumerable<string> values)
    {
        _selected = values.ToList();
        await SelectedChanged.InvokeAsync(_selected.ToList());
    }

    private async Task NewLabelKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await AddNew();
    }

    private async Task AddNew()
    {
        var name = _newName.Trim();
        if (name.Length == 0) return;
        try
        {
            var label = await Rim.GetOrCreateLabelAsync(name, string.IsNullOrEmpty(Actor) ? "unknown" : Actor);
            if (!_all.Any(l => l.Id == label.Id)) _all.Add(label);
            _all = _all.OrderBy(l => l.Name).ToList();
            var cur = _selected.ToList();
            if (!cur.Contains(label.Name, StringComparer.OrdinalIgnoreCase)) cur.Add(label.Name);
            _newName = "";
            await OnSelectedChanged(cur);
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
    }
}
