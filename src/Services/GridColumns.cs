using Microsoft.JSInterop;
using Prim.Data;

namespace Prim.Services;

public static class GridColumns
{
    public static List<(string Key, string Label)> RecordColumns() => new()
    {
        ("RecordNumber","Record Number"), ("RecordType","Record Type"), ("CaseClassification","Case Classification"),
        ("FieldOffice","Field Office"), ("CaseNumber","Case Number"), ("SubfileId","Subfile ID"),
        ("Volume","Volume"), ("SerialStart","Serial Start"), ("SerialEnd","Serial End"),
        ("AuxiliaryOffice","Auxiliary Office"), ("Home","Home"), ("Assignee","Assignee"),
        ("Path","Path"), ("Barcode","Barcode"), ("State","State"), ("Subject","Subject"), ("Notes","Notes"),
        ("Labels","Labels"),
    };

    public static Dictionary<string,string> RecordRow(RecordItem r) => new()
    {
        ["RecordNumber"] = r.RecordNumber, ["RecordType"] = r.RecordType,
        ["CaseClassification"] = r.CaseClassification, ["FieldOffice"] = r.FieldOffice,
        ["CaseNumber"] = r.CaseNumber, ["SubfileId"] = r.SubfileId ?? "",
        ["Volume"] = r.Volume, ["SerialStart"] = r.SerialStart ?? "", ["SerialEnd"] = r.SerialEnd ?? "",
        ["AuxiliaryOffice"] = r.AuxiliaryOffice ?? "", ["Home"] = r.Home, ["Assignee"] = r.Assignee,
        ["Path"] = "", ["Barcode"] = r.Barcode, ["State"] = r.State, ["Subject"] = r.Subject ?? "", ["Notes"] = r.Notes ?? "", ["Labels"] = "",
    };

    public static List<(string Key, string Label)> ContainerColumns() => new()
    {
        ("ContainerName","Container Name"), ("ContainerType","Container Type"), ("FieldOffice","Field Office"),
        ("ContainerCode","Container Code"), ("FormattedNumber","Formatted Number"),
        ("Description","Description"), ("Home","Home"), ("Assignee","Assignee"), ("Path","Path"), ("Barcode","Barcode"),
        ("Labels","Labels"),
    };

    public static Dictionary<string,string> ContainerRow(Container c) => new()
    {
        ["ContainerName"] = c.ContainerName, ["ContainerType"] = c.ContainerType,
        ["FieldOffice"] = c.FieldOffice, ["ContainerCode"] = c.ContainerCode,
        ["FormattedNumber"] = c.FormattedNumber, ["Description"] = c.Description ?? "",
        ["Home"] = c.Home, ["Assignee"] = c.Assignee, ["Path"] = "", ["Barcode"] = c.Barcode, ["Labels"] = "",
    };

    public static List<(string Key, string Label)> LocationColumns() => new()
    {
        ("LocationName","Location Name"), ("LocationType","Location Type"),
        ("Description","Description"), ("Path","Path"), ("Barcode","Barcode"),
        ("Labels","Labels"),
    };

    public static Dictionary<string,string> LocationRow(Location l) => new()
    {
        ["LocationName"] = l.LocationName, ["LocationType"] = l.LocationType,
        ["Description"] = l.Description ?? "", ["Path"] = "", ["Barcode"] = l.Barcode, ["Labels"] = "",
    };

    public static List<(string Key, string Label)> UserColumns() => new()
    {
        ("UserId","User ID"), ("DisplayName","Display Name"), ("Role","Role"),
        ("Email","Email"), ("LocationId","Location ID"), ("Active","Active"),
        ("Labels","Labels"),
    };

    public static Dictionary<string,string> UserRow(AppUser u) => new()
    {
        ["UserId"] = u.UserId, ["DisplayName"] = u.DisplayName, ["Role"] = u.Role,
        ["Email"] = u.Email ?? "", ["LocationId"] = u.LocationId?.ToString() ?? "",
        ["Active"] = u.Active ? "Yes" : "No", ["Labels"] = "",
    };

    public static List<(string Key, string Label)> WorkspaceColumns() => new()
    {
        ("Label","Item"), ("ObjectKind","Kind"), ("AddedUtc","Added"),
    };

    public static Dictionary<string,string> WorkspaceRow(WorkspaceItem w) => new()
    {
        ["Label"] = w.Label, ["ObjectKind"] = w.ObjectKind,
        ["AddedUtc"] = w.AddedUtc.ToLocalTime().ToString("g"),
    };

    // Explicit column widths for data grids. MudDataGrid only engages its
    // horizontal scrollbar when the columns' combined width overflows the
    // viewport; without widths the table squeezes columns to fit instead.
    public static string ColWidth(string key) => key switch
    {
        "FieldOffice" or "Volume" or "State" or "Active" => "90px",
        "RecordNumber" or "Barcode" or "CaseNumber" or "ContainerCode"
            or "CaseClassification" or "ContainerName" or "UserId" => "130px",
        "Subject" or "Notes" or "Description" or "Path" => "260px",
        "RecordType" or "ContainerType" or "LocationType" or "Home" or "Assignee"
            or "DisplayName" or "LocationName" or "FormattedNumber" => "170px",
        _ => "140px",
    };
}

public static class Csv
{
    public static string Escape(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    public static async Task DownloadAsync(Microsoft.JSInterop.IJSRuntime js, string fileName, string content)
        => await PrimJs.TryInvokeVoidAsync(js, "prim.download", fileName, content, "text/csv");
}
