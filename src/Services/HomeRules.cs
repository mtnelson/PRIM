namespace Prim.Services;

/// <summary>
/// Child-item / homing rules (user requirements 2026-09-29):
/// - Locations, users, and containers CANNOT be homed/assigned to compressed records.
/// - Records can be homed/assigned to: Containers, Locations, Users.
/// - Containers can be homed/assigned to: Containers, Locations, Users.
/// - Locations can have child locations and parent locations (location tree).
/// - Users can have membership in a location.
/// Enforced in the pickers (UI) and in PrimService validation (service level).
/// </summary>
public static class HomeRules
{
    /// <summary>Object kinds that may serve as a Home or Assignee.</summary>
    public static readonly string[] EligibleHomeKinds = { "Container", "Location", "User" };

    /// <summary>
    /// Returns an error message when the home assignment is invalid, else null.
    /// itemKind is "Record" or "Container".
    /// </summary>
    public static string? ValidateHome(string itemKind, string? homeKind, string fieldName = "home")
    {
        if (string.IsNullOrWhiteSpace(homeKind))
            return $"A {fieldName} is required.";
        if (!EligibleHomeKinds.Contains(homeKind))
            return $"{itemKind} can only be {fieldName}d to a container, location, or user — not to '{homeKind}'. Compressed records can never be a {fieldName}.";
        return null;
    }
}
