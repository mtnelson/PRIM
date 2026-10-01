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

public partial class TextInputDialog : ComponentBase
{
    [CascadingParameter] IMudDialogInstance MudDialog { get; set; } = null!;
    [Parameter] public string Title { get; set; } = "Enter value";
    [Parameter] public string Label { get; set; } = "Value";
    [Parameter] public string Value { get; set; } = "";

    private string _value = "";
    private string? _error;

    protected override void OnInitialized() => _value = Value;

    private void Cancel() => MudDialog.Cancel();

    private async Task Key(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await Ok();
    }

    private Task Ok()
    {
        if (string.IsNullOrWhiteSpace(_value)) { _error = "A value is required."; return Task.CompletedTask; }
        MudDialog.Close(DialogResult.Ok(_value.Trim()));
        return Task.CompletedTask;
    }
}
