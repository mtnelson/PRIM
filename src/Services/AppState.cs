namespace Prim.Services;

/// <summary>Per-circuit UI state: current user, recent items, activity log (TIS-349).</summary>
public class AppState
{
    public string CurrentUserId { get; private set; } = "";
    public string CurrentDisplayName { get; private set; } = "";
    public string CurrentRole { get; private set; } = "";

    /// <summary>True after a successful login via IAuthProvider. Production uses OAuth/SSO.</summary>
    public bool IsAuthenticated { get; private set; }

    public event Action? Changed;
    private void Notify() => Changed?.Invoke();

    public void SignIn(string userId, string displayName, string role)
    {
        CurrentUserId = userId; CurrentDisplayName = displayName; CurrentRole = role;
        IsAuthenticated = true;
        Log("Signed in", $"{displayName} ({role})");
        Notify();
    }

    public void SignOut()
    {
        Log("Signed out", CurrentDisplayName);
        CurrentUserId = ""; CurrentDisplayName = ""; CurrentRole = "";
        IsAuthenticated = false;
        Recent.Clear(); ActivityLog.Clear(); ViewPaneSelection = null;
        Notify();
    }



    public bool IsAdmin => CurrentRole == "Admin";
    public bool IsRecordsManager => CurrentRole is "Admin" or "Records Manager";

    // Activity log panel (TIS-349): real-time actions with timestamps, clearable,
    // not persisted across sessions.
    public List<(DateTime At, string Action, string Detail)> ActivityLog { get; } = new();
    public void Log(string action, string detail = "")
    {
        ActivityLog.Add((DateTime.Now, action, detail));
        Notify();
    }
    // Item-specific log entries: names the affected items instead of a bare
    // count. Long lists are truncated with a "+N more" tail.
    public void LogItems(string action, IEnumerable<string> labels, int max = 8)
    {
        var list = labels.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var detail = string.Join(", ", list.Take(max));
        if (list.Count > max) detail += $" (+{list.Count - max} more)";
        Log(action, detail);
    }
    public void ClearLog() { ActivityLog.Clear(); Notify(); }

    // Recently viewed items for the Shortcuts pane.
    public List<(string Kind, int Id, string Label)> Recent { get; } = new();
    public void TouchRecent(string kind, int id, string label)
    {
        Recent.RemoveAll(r => r.Kind == kind && r.Id == id);
        Recent.Insert(0, (kind, id, label));
        while (Recent.Count > 10) Recent.RemoveAt(Recent.Count - 1);
        Notify();
    }

    public string? ActiveAnnouncement { get; set; }

    // Cross-page object navigation ("focus"): a grid link, breadcrumb segment,
    // or child row requests focus on an object; the matching page's grid takes
    // the request, selects the row, opens the detail pane, and optionally
    // expands its children. Survives the NavigationManager hop because only
    // the matching kind consumes it.
    public record FocusRequest(string Kind, int Id, bool Expand);
    private FocusRequest? _pendingFocus;
    public void RequestFocus(string kind, int id, bool expand = false)
    {
        _pendingFocus = new FocusRequest(kind, id, expand);
        Notify();
    }
    public bool TryTakeFocus(string kind, out FocusRequest? request)
    {
        request = null;
        if (_pendingFocus is { } p && p.Kind == kind)
        {
            request = p;
            _pendingFocus = null;
            return true;
        }
        return false;
    }

    // View pane selection (CM-style right pane, selection-driven).
    public (string Kind, int Id, string Label)? ViewPaneSelection { get; private set; }
    public bool ViewPaneOpen { get; set; } = true;
    public void SetViewPane(string kind, int id, string label)
    {
        ViewPaneSelection = (kind, id, label);
        TouchRecent(kind, id, label);
        Notify();
    }
    public void ClearViewPane() { ViewPaneSelection = null; Notify(); }
}
