namespace Rim.Services;

/// <summary>A single 500-row chunk request from a virtualized data grid.</summary>
public sealed record GridPageRequest
{
    /// <summary>Global row offset (used for explicit sorts and jump fetches).</summary>
    public int Skip { get; init; }
    /// <summary>Id of the last row of the previous chunk (keyset cursor for
    /// default Id ordering; preferred over Skip on large tables).</summary>
    public int? AfterId { get; init; }
    public int Take { get; init; } = 500;
    public string? Filter { get; init; }
    public string? SortColumn { get; init; }
    public bool SortDescending { get; init; }
}

/// <summary>One chunk of grid rows.</summary>
public sealed class GridPageResult<T>
{
    public List<T> Rows { get; init; } = new();
    public bool HasMore { get; init; }
}
