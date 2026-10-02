namespace Rim.Components.Shared;

// Pure range-selection helper for the data grids. Shift+click selects the
// contiguous run of loaded rows between the anchor row and the clicked row.
// Kept free of component state so the console test harness can cover it;
// ObjectGrid wires it to row clicks in both Items and virtualized modes.
public static class GridSelection
{
    // Rows between anchor and target (inclusive) in list order, or null when
    // either endpoint is absent (anchor scrolled out of the loaded window,
    // new query) — the caller then falls back to a plain single-select.
    public static HashSet<T>? Range<T>(IList<T> rows, T? anchor, T target, IEqualityComparer<T> comparer)
    {
        if (anchor is null) return null;
        int ai = IndexOf(rows, anchor, comparer);
        int ci = IndexOf(rows, target, comparer);
        if (ai < 0 || ci < 0) return null;
        var set = new HashSet<T>(comparer);
        for (int i = Math.Min(ai, ci); i <= Math.Max(ai, ci); i++) set.Add(rows[i]);
        return set;
    }

    private static int IndexOf<T>(IList<T> rows, T item, IEqualityComparer<T> comparer)
    {
        for (int i = 0; i < rows.Count; i++)
            if (comparer.Equals(rows[i], item)) return i;
        return -1;
    }
}
