namespace Rim.Services;

/// <summary>
/// Child-item / homing rules (user requirements 2026-09-29, extended 2026-10-01):
/// - Locations, users, and containers CANNOT be homed/assigned to compressed records.
/// - Records can be homed/assigned to: Containers, Locations, Users.
/// - Containers can be homed/assigned to: Containers, Locations, Users.
/// - Locations can have child locations and parent locations (location tree).
/// - Users can have membership in a location.
/// - v0.12.0: a record filed under a compressed parent (ParentRecordId set)
///   has the parent record itself as its Home and Assignee — kind "Record".
///   RimService sets this directly and skips ValidateHome for such rows.
/// Enforced in the pickers (UI) and in RimService validation (service level).
/// </summary>
public static class HomeRules
{
    /// <summary>Object kinds that may serve as a Home or Assignee.</summary>
    public static readonly string[] EligibleHomeKinds = { "Container", "Location", "User" };

    /// <summary>
    /// Returns an error message when the home assignment is invalid, else null.
    /// itemKind is "Record" or "Container". Kind "Record" (a compressed
    /// parent) is handled by RimService, not here — see the class comment.
    /// </summary>
    public static string? ValidateHome(string itemKind, string? homeKind, string fieldName = "home")
    {
        if (string.IsNullOrWhiteSpace(homeKind))
            return $"A {fieldName} is required.";
        if (!EligibleHomeKinds.Contains(homeKind))
            return $"{itemKind} can only be {fieldName}d to a container, location, or user — not to '{homeKind}'.";
        return null;
    }
}
