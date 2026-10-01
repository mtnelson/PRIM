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

public partial class ColumnPickerDialog : ComponentBase
{
    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public List<(string Key, string Label)> Available { get; set; } = new();
    [Parameter] public List<string> Current { get; set; } = new();

    private List<(string Key, string Label)> _ordered = new();
    private HashSet<string> _visible = new();

    protected override void OnInitialized()
    {
        var byKey = Available.ToDictionary(c => c.Key, c => c.Label);
        // Current order first, then any remaining available columns.
        _ordered = Current.Where(k => byKey.ContainsKey(k)).Select(k => (k, byKey[k])).ToList();
        _ordered.AddRange(Available.Where(c => !_ordered.Any(o => o.Key == c.Key)));
        _visible = Current.Where(k => byKey.ContainsKey(k)).ToHashSet();
        if (_visible.Count == 0) _visible = Available.Select(c => c.Key).ToHashSet();
    }

    private void Toggle(string key, bool v)
    {
        if (v) _visible.Add(key); else _visible.Remove(key);
    }

    private void Move((string Key, string Label) col, int dir)
    {
        var i = _ordered.IndexOf(col);
        var j = i + dir;
        if (j < 0 || j >= _ordered.Count) return;
        (_ordered[i], _ordered[j]) = (_ordered[j], _ordered[i]);
    }

    private void Save()
    {
        var keys = _ordered.Where(c => _visible.Contains(c.Key)).Select(c => c.Key).ToList();
        MudDialog.Close(DialogResult.Ok(keys));
    }
}
