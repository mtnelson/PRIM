namespace Prim.Services;

/// <summary>
/// Field Office / Auxiliary Office list. Codes are stored uppercase (existing rule).
/// Typo corrections applied 2026-09-29 per user list: Cincinnati (was "Cincinnatti"),
/// Las Vegas (was "Las"), Minneapolis (was "Minneapolios").
/// </summary>
public static class FieldOffices
{
    public static readonly IReadOnlyList<(string Code, string Name)> Offices = new List<(string, string)>
    {
        ("AL", "Albany"), ("AQ", "Albuquerque"), ("AX", "Alexandria"), ("AN", "Anchorage"),
        ("AT", "Atlanta"), ("BA", "Baltimore"), ("BH", "Birmingham"), ("BS", "Boston"),
        ("BQ", "Brooklyn/Queens"), ("BU", "Buffalo"), ("BT", "Butte"), ("CE", "Charlotte"),
        ("CG", "Chicago"), ("CI", "Cincinnati"), ("CV", "Cleveland"), ("CO", "Columbia"),
        ("DL", "Dallas"), ("DN", "Denver"), ("DE", "Detroit"), ("EP", "El Paso"),
        ("HN", "Honolulu"), ("HO", "Houston"), ("IP", "Indianapolis"), ("JN", "Jackson"),
        ("JK", "Jacksonville"), ("KC", "Kansas City"), ("KX", "Knoxville"), ("LV", "Las Vegas"),
        ("LR", "Little Rock"), ("LA", "Los Angeles"), ("LS", "Louisville"), ("ME", "Memphis"),
        ("MM", "Miami"), ("MI", "Milwaukee"), ("MP", "Minneapolis"), ("MO", "Mobile"),
        ("NK", "Newark"), ("NH", "New Haven"), ("NO", "New Orleans"), ("NR", "New Rochelle"),
        ("NY", "New York City"), ("NF", "Norfolk"), ("OC", "Oklahoma City"), ("OM", "Omaha"),
        ("PH", "Philadelphia"), ("PX", "Phoenix"), ("PG", "Pittsburgh"), ("PD", "Portland"),
        ("RH", "Richmond"), ("SC", "Sacramento"), ("SL", "Saint Louis"), ("SU", "Salt Lake City"),
        ("SA", "San Antonio"), ("SD", "San Diego"), ("SF", "San Francisco"), ("SJ", "San Juan"),
        ("SV", "Savannah"), ("SE", "Seattle"), ("SI", "Springfield"), ("TP", "Tampa"),
        ("WF", "Washington"),
    };

    public static string Display((string Code, string Name) o) => $"{o.Code} - {o.Name}";

    public static bool IsKnownCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Offices.Any(o => o.Code == code.ToUpperInvariant());
}
