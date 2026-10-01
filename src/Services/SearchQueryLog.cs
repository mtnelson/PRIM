using System.Collections.Concurrent;

namespace Rim.Services;

// One executed SQL statement captured from a tagged advanced-search query.
public sealed record SearchQueryEntry(
    DateTime TimestampUtc,
    Guid RunId,
    string Kind,   // Record | Container | Location | User
    string Role,   // count | ids | page
    string Sql,
    double DurationMs);

// App-wide ring buffer of recently executed advanced-search SQL.
// Queries opt in via .TagWith("AdvancedSearch:<Kind>:<runId>:<role>");
// RimService captures the SQL with ToQueryString() and records the measured
// duration. Provider-agnostic: ToQueryString renders SQL for whatever
// provider is configured (SQLite now, SQL Server later). Untagged queries
// cost nothing — recording only happens when a tag is supplied.
public sealed class SearchQueryLog
{
    public const string TagPrefix = "AdvancedSearch:";
    private const int MaxEntries = 200;

    private readonly ConcurrentQueue<SearchQueryEntry> _entries = new();

    // Raised after each recorded entry (on the capturing thread). Subscribers
    // (Blazor components) must marshal to their sync context.
    public event Action? Changed;

    public IReadOnlyList<SearchQueryEntry> GetForRun(Guid runId)
    {
        // ConcurrentQueue preserves insertion order: oldest first, newest last.
        return _entries.Where(e => e.RunId == runId).ToList();
    }

    public int Count => _entries.Count;

    // Parses a tag of the form "AdvancedSearch:<Kind>:<runId>:<role>".
    public static bool TryParseTag(string? tag, out Guid runId, out string kind, out string role)
    {
        runId = Guid.Empty;
        kind = "";
        role = "";
        if (string.IsNullOrEmpty(tag) || !tag.StartsWith(TagPrefix, StringComparison.Ordinal))
            return false;
        var parts = tag.Split(':');
        if (parts.Length != 4 || parts[0] != "AdvancedSearch")
            return false;
        if (!Guid.TryParse(parts[2], out runId))
            return false;
        kind = parts[1];
        role = parts[3];
        return kind.Length > 0 && role.Length > 0;
    }

    public void Record(Guid runId, string kind, string role, string sql, double durationMs)
    {
        _entries.Enqueue(new SearchQueryEntry(
            DateTime.UtcNow, runId, kind, role, sql, durationMs));
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }
        Changed?.Invoke();
    }
}
