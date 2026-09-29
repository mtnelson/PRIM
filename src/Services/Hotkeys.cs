using Microsoft.JSInterop;

namespace Prim.Services;

/// <summary>A single hotkey definition. HotkeyCatalog is the single source of
/// truth: the Help dialog is generated from it, so docs cannot drift from
/// the implementation. Combos look like "Ctrl+N", "Alt+3", "F1", "Escape".</summary>
public sealed record HotkeyDef(string Combo, string Title, string Description, string Category);

public static class HotkeyCatalog
{
    public static readonly IReadOnlyList<HotkeyDef> All = new List<HotkeyDef>
    {
        new("Ctrl+N", "New", "Create a new item on the current screen.", "Items"),
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
    private readonly Dictionary<(string Scope, string Combo), Func<Task>> _handlers = new();

    public string CurrentScope => _scopes.TryPeek(out var s) ? s : "app";

    public void PushScope(string scope) => _scopes.Push(scope);

    public void PopScope()
    {
        if (_scopes.Count > 0) _scopes.Pop();
    }

    public void Register(string scope, string combo, Func<Task> handler) =>
        _handlers[(scope, combo)] = handler;

    public void Register(string scope, string combo, Action handler) =>
        _handlers[(scope, combo)] = () => { handler(); return Task.CompletedTask; };

    public void UnregisterScope(string scope)
    {
        foreach (var k in _handlers.Keys.Where(k => k.Scope == scope).ToList())
            _handlers.Remove(k);
    }

    /// <summary>
    /// Dispatches a key combo captured by prim.js. Returns true when a handler
    /// ran (JS then calls preventDefault). Checks the top scope, then "app".
    /// </summary>
    public async Task<bool> Handle(string combo)
    {
        if (_handlers.TryGetValue((CurrentScope, combo), out var h) ||
            _handlers.TryGetValue(("app", combo), out h))
        {
            await h();
            return true;
        }
        return false;
    }
}
