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

namespace Prim.Components.Shared;

public partial class LoginScreen : ComponentBase
{
    [Inject] public IAuthProvider Auth { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public ISnackbar Snackbar { get; set; } = default!;

    private string _userId = "";
    private string _password = "";
    private string? _error;
    private bool _busy;

    private async Task Login()
    {
        _error = null; _busy = true;
        try
        {
            var res = await Auth.AuthenticateAsync(_userId, _password);
            if (!res.Ok) { _error = res.Error; return; }
            App.SignIn(res.UserId, res.DisplayName, res.Role);
        }
        finally { _busy = false; }
    }
}
