namespace Rim.Services;

/// <summary>Live per-tab grid state shared between a page and its ObjectGrid.
/// The page owns one instance per open search tab; the grid reads it when a
/// tab is applied and writes user-driven changes (sort, selection, expansion,
/// column layout) back into it, firing TabStateChanged so the page can
/// persist the backing SearchSession descriptor.</summary>
public class SearchTabState
{
    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }
    public HashSet<int> SelectedIds { get; set; } = new();
    public HashSet<int> ExpandedIds { get; set; } = new();
    /// <summary>Null = fall back to the user's saved profile layout.</summary>
    public List<string>? ColumnKeys { get; set; }
}
