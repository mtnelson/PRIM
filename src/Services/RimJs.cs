using Microsoft.JSInterop;

namespace Rim.Services;

/// <summary>
/// Safe wrapper for rim.js interop calls. If rim.js failed to load
/// (stale deployment, cached page, blocked script), the call degrades
/// silently instead of throwing a JSException that tears down the
/// Blazor circuit. A missing helper script must never block login
/// or kill the app.
/// </summary>
public static class RimJs
{
    public static async Task TryInvokeVoidAsync(IJSRuntime js, string identifier, params object?[] args)
    {
        try { await js.InvokeVoidAsync(identifier, args); }
        catch (JSException) { /* rim.js unavailable — feature degrades silently */ }
    }

    public static async Task<T?> TryInvokeAsync<T>(IJSRuntime js, string identifier, params object?[] args)
    {
        try { return await js.InvokeAsync<T>(identifier, args); }
        catch (JSException) { return default; }
    }
}
