using Microsoft.JSInterop;

namespace Rim.Services;

/// <summary>A single hotkey definition. HotkeyCatalog is the single source of
/// truth: the Help dialog is generated from it, so docs cannot drift from
/// the implementation. Combos look like "Ctrl+N", "Alt+3", "F1", "Escape".</summary>
public sealed record HotkeyDef(string Combo, string Title, string Description, string Category);

public static class HotkeyCatalog
{
    public static readonly IReadOnlyList<HotkeyDef> All = new List<HotkeyDef>
    {
        new("Ctrl+N", "New", "Open the New menu.", "Items"),
        new("F2", "Edit", "Edit the selected row.", "Items"),
        new("Delete", "Delete", "Delete the selected row(s).", "Items"),
        new("Ctrl+P", "Print labels", "Print inventory labels for all selected rows.", "Items"),
        new("Ctrl+A", "Select all", "Select every row in the focused grid.", "Grid"),
        new("Ctrl+C", "Copy", "Copy selected rows with column headers (spreadsheet-friendly).", "Grid"),
        new("Ctrl+Click", "Toggle select", "Ctrl+click (or Cmd+click) a row to add/remove it without clearing the selection.", "Grid"),
        new("Double-click", "Quick edit", "Double-click a row to open its edit screen immediately.", "Grid"),
        new("Ctrl+S", "Save", "Save the open dialog.", "Dialogs"),
        new("Escape", "Close / clear", "Close the open dialog, or clear the grid selection.", "Dialogs"),
        new("Ctrl+F", "Find", "Focus the filter box on the current screen.", "Navigation"),
        new("F9", "Refresh", "Reload the current screen.", "Navigation"),
        new("F1", "Help", "Open this help.", "Navigation"),
        new("Alt+1", "Dashboard", "Go to Dashboard.", "Navigation"),
        new("Alt+2", "Advanced Search", "Go to Advanced Search.", "Navigation"),
        new("Alt+3", "Records", "Go to Records.", "Navigation"),
        new("Alt+4", "Containers", "Go to Containers.", "Navigation"),
        new("Alt+5", "Locations", "Go to Locations.", "Navigation"),
        new("Alt+6", "Users", "Go to Users.", "Navigation"),
        new("Alt+7", "Workspaces", "Go to Workspaces.", "Navigation"),
        new("Alt+8", "Reports", "Go to Reports.", "Navigation"),
    };

    public static IEnumerable<IGrouping<string, HotkeyDef>> ByCategory() =>
        All.GroupBy(h => h.Category);
}

/// <summary>
/// Per-circuit hotkey dispatcher. Components register handlers under a scope
/// name (page scope, grid scope, or "dialog"); pages/dialogs push their scope
/// while active. The browser calls OnHotkey(combo) via JS; the handler for the
/// current scope (falling back to the "app" scope) runs and its return value
/// tells JS whether to preventDefault.
/// </summary>
public class HotkeyManager
{
    private readonly Stack<string> _scopes = new();
    // Several grids can share one scope (Dashboard, Workspaces). Handlers stack
    // per (scope, combo); the most recently registered one fires, and each
    // registration returns a token so a disposing component removes only its own.
    private readonly Dictionary<(string Scope, string Combo), List<(Guid Token, Func<Task> Handler)>> _handlers = new();

    public string CurrentScope => _scopes.TryPeek(out var s) ? s : "app";

    /// <summary>
    /// Fired whenever the registered combo set changes. MainLayout subscribes
    /// so the browser's synchronous preventDefault set is pushed via JS even
    /// when MainLayout itself doesn't re-render (dialogs, tab switches, …) —
    /// otherwise Ctrl+A can reach .NET (rows get tagged) without the browser
    /// suppressing its native select-all.
    /// </summary>
    public event Action? CombosChanged;

    public void PushScope(string scope) => _scopes.Push(scope);

    public void PopScope()
    {
        if (_scopes.Count > 0) _scopes.Pop();
    }

    public IDisposable Register(string scope, string combo, Func<Task> handler)
    {
        var key = (scope, combo);
        var token = Guid.NewGuid();
        if (!_handlers.TryGetValue(key, out var list))
            _handlers[key] = list = new();
        list.Add((token, handler));
        CombosDirty = true;
        CombosChanged?.Invoke();
        return new Registration(this, key, token);
    }

    public IDisposable Register(string scope, string combo, Action handler) =>
        Register(scope, combo, () => { handler(); return Task.CompletedTask; });

    private void Unregister((string Scope, string Combo) key, Guid token)
    {
        if (_handlers.TryGetValue(key, out var list))
        {
            list.RemoveAll(e => e.Token == token);
            if (list.Count == 0) _handlers.Remove(key);
        }
        CombosDirty = true;
        CombosChanged?.Invoke();
    }

    public void UnregisterScope(string scope)
    {
        foreach (var k in _handlers.Keys.Where(k => k.Scope == scope).ToList())
            _handlers.Remove(k);
        CombosDirty = true;
        CombosChanged?.Invoke();
    }

    /// <summary>
    /// Distinct combos currently registered. The browser needs the list up
    /// front so it can preventDefault() synchronously in the keydown handler;
    /// doing it after the .NET round-trip is too late and the native action
    /// (copy cell text, find bar, print dialog, ...) fires anyway.
    /// TakeCombos clears the dirty flag.
    /// </summary>
    public bool CombosDirty { get; private set; } = true;

    public string[] TakeCombos()
    {
        CombosDirty = false;
        return _handlers.Keys.Select(k => k.Combo).Distinct().ToArray();
    }

    /// <summary>
    /// Dispatches a key combo captured by rim.js. Returns true when a handler
    /// ran (JS then calls preventDefault). Checks the top scope, then "app".
    /// When several components registered the same combo, the most recent wins.
    /// </summary>
    public async Task<bool> Handle(string combo)
    {
        if ((_handlers.TryGetValue((CurrentScope, combo), out var list) && list.Count > 0) ||
            (_handlers.TryGetValue(("app", combo), out list) && list.Count > 0))
        {
            await list[^1].Handler();
            return true;
        }
        return false;
    }

    private sealed class Registration(HotkeyManager mgr, (string Scope, string Combo) key, Guid token) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            mgr.Unregister(key, token);
        }
    }
}
