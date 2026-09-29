namespace Prim.Services;

/// <summary>Per-circuit UI state: current user, recent items, activity log (TIS-349).</summary>
public class AppState
{
    public string CurrentUserId { get; private set; } = "admin";
    public string CurrentDisplayName { get; private set; } = "PRIM Administrator";
    public string CurrentRole { get; private set; } = "Admin";

    public event Action? Changed;
    private void Notify() => Changed?.Invoke();

    public void SetUser(string userId, string displayName, string role)
    {
        CurrentUserId = userId; CurrentDisplayName = displayName; CurrentRole = role;
        Log("Signed in", $"{displayName} ({role})");
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
