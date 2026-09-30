namespace Prim.Services;

/// <summary>A single infinite-scroll chunk request from a data grid.</summary>
public sealed record GridPageRequest
{
    /// <summary>Rows already loaded (used for offset fallback on explicit sorts).</summary>
    public int Skip { get; init; }
    /// <summary>Id of the last loaded row (keyset cursor for default ordering).</summary>
    public int? AfterId { get; init; }
    public int Take { get; init; } = 500;
    public string? Filter { get; init; }
    public string? SortColumn { get; init; }
    public bool SortDescending { get; init; }
}

/// <summary>One chunk of grid rows. HasMore drives the scroll sentinel:
/// while true the grid keeps fetching as the user scrolls.</summary>
public sealed class GridPageResult<T>
{
    public List<T> Rows { get; init; } = new();
    public bool HasMore { get; init; }
}
