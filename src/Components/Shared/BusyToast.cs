using MudBlazor;

namespace Rim.Components.Shared;

/// <summary>
/// Persistent "working" toast for long-running operations. The toast only
/// appears if the operation outlasts a short delay (fast operations never
/// flash it), stays visible until the operation finishes, and is then replaced
/// by a completion toast. Usage:
/// <code>await using var busy = BusyToast.Show(Snackbar, "Working…");</code>
/// </summary>
public sealed class BusyToast : IAsyncDisposable
{
    private readonly ISnackbar _snackbar;
    private readonly int _delayMs;
    private string _message;
    private Snackbar? _toast;
    private bool _done;

    private BusyToast(ISnackbar snackbar, string message, int delayMs)
    {
        _snackbar = snackbar;
        _message = message;
        _delayMs = delayMs;
        _ = ShowAfterDelayAsync();
    }

    public static BusyToast Show(ISnackbar snackbar, string message, int delayMs = 600) =>
        new(snackbar, message, delayMs);

    private static void Configure(SnackbarOptions o)
    {
        o.VisibleStateDuration = int.MaxValue; // never auto-dismiss; we remove it
        o.ShowCloseIcon = false;
        o.HideTransitionDuration = 100;
    }

    private async Task ShowAfterDelayAsync()
    {
        await Task.Delay(_delayMs);
        if (_done) return;
        _toast = _snackbar.Add(_message, Severity.Info, Configure);
    }

    /// <summary>Refresh the working message, e.g. "3,000 of 50,000…".</summary>
    public void Update(string message)
    {
        _message = message;
        if (_toast is null) return;
        var next = _snackbar.Add(message, Severity.Info, Configure);
        _snackbar.Remove(_toast);
        _toast = next;
    }

    /// <summary>
    /// Yield to the Blazor renderer so the working toast (and progress updates)
    /// can actually paint. Call periodically inside bulk loops: with SQLite the
    /// EF Core awaits often complete synchronously — and CPU-only loops never
    /// await at all — so without an explicit yield the UI thread stays blocked
    /// until the operation finishes and the toast never appears.
    /// </summary>
    public static async Task YieldForPaintAsync() => await Task.Yield();

    /// <summary>Remove the working toast and show the completion toast.</summary>
    public void Complete(string message, Severity severity = Severity.Success)
    {
        _done = true;
        if (_toast is not null) _snackbar.Remove(_toast);
        _toast = null;
        _snackbar.Add(message, severity);
    }

    public async ValueTask DisposeAsync()
    {
        _done = true;
        if (_toast is not null) _snackbar.Remove(_toast);
        _toast = null;
        await Task.CompletedTask;
    }
}
