using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using MudBlazor;
using Prim.Components.Dialogs;
using Prim.Components.Layout;
using Prim.Components.Shared;
using Prim.Data;
using Prim.Services;

namespace Prim.Components.Shared;

public partial class ViewPane : ComponentBase, IDisposable
{
    [Inject] public PrimService Prim { get; set; } = default!;
    [Inject] public AppState App { get; set; } = default!;
    [Inject] public NavigationManager Nav { get; set; } = default!;

    [Parameter] public string Kind { get; set; } = "";
    [Parameter] public int Id { get; set; }

    private List<(string K, string V)> _props = new();
    private Dictionary<string, (string Kind, int Id)> _links = new();
    private string? _notes;
    private List<AuditEvent> _audit = new();

    private void NavigateTo(string kind, int id, bool expand)
    {
        App.RequestFocus(kind, id, expand);
        Nav.NavigateTo(kind switch
        {
            "Record" => "/records",
            "Container" => "/containers",
            "Location" => "/locations",
            "User" => "/users",
            _ => "/"
        });
    }

    protected override async Task OnParametersSetAsync()
    {
        _props.Clear(); _notes = null; _audit = new(); _links.Clear();
        if (Kind == "Record")
        {
            var r = await Prim.GetRecordAsync(Id);
            if (r == null) return;
            _props = new()
            {
                ("Record Number", r.RecordNumber), ("Barcode", r.Barcode), ("Record Type", r.RecordType),
                ("Case Classification", r.CaseClassification), ("Field Office", r.FieldOffice),
                ("Case Number", r.CaseNumber), ("Subfile ID", r.SubfileId ?? "—"), ("Volume", r.Volume),
                ("Serial Start", r.SerialStart ?? "—"), ("Serial End", r.SerialEnd ?? "—"),
                ("Auxiliary Office", r.AuxiliaryOffice ?? "—"),
                ("Flags", string.Join(", ", new[] { r.IsBulky ? "Bulky" : null, r.IsAdmin ? "Admin" : null, r.IsControlFile ? "Control File" : null }.Where(x => x != null))),
                ("Labels", r.Labels ?? "—"), ("Security Classification", r.SecurityClassification ?? "—"),
                ("Subject", r.Subject ?? "—"), ("Home", $"{r.Home} ({r.HomeKind})"), ("Assignee", r.Assignee),
                ("State", r.State), ("Created", $"{r.CreatedBy} · {r.CreatedUtc.ToLocalTime():g}"),
                ("Last Updated", $"{r.LastUpdatedBy} · {r.LastUpdatedUtc.ToLocalTime():g}"),
            };
            if (r.HomeKind != null && r.HomeRefId != null) _links["Home"] = (r.HomeKind, r.HomeRefId.Value);
            if (r.AssigneeKind != null && r.AssigneeRefId != null) _links["Assignee"] = (r.AssigneeKind, r.AssigneeRefId.Value);
            _notes = r.Notes;
        }
        else if (Kind == "Container")
        {
            var c = await Prim.GetContainerAsync(Id);
            if (c == null) return;
            _props = new()
            {
                ("Container Name", c.ContainerName), ("Barcode", c.Barcode), ("Container Type", c.ContainerType),
                ("Field Office", c.FieldOffice), ("Container Code", c.ContainerCode), ("Formatted Number", c.FormattedNumber),
                ("Description", c.Description ?? "—"), ("Home", $"{c.Home} ({c.HomeKind})"), ("Assignee", c.Assignee),
                ("Created", $"{c.CreatedBy} · {c.CreatedUtc.ToLocalTime():g}"),
                ("Last Updated", $"{c.LastUpdatedBy} · {c.LastUpdatedUtc.ToLocalTime():g}"),
            };
            if (c.HomeKind != null && c.HomeRefId != null) _links["Home"] = (c.HomeKind, c.HomeRefId.Value);
            if (c.AssigneeKind != null && c.AssigneeRefId != null) _links["Assignee"] = (c.AssigneeKind, c.AssigneeRefId.Value);
        }
        else if (Kind == "Location")
        {
            var l = await Prim.GetLocationAsync(Id);
            if (l == null) return;
            string? parent = null;
            if (l.ParentId is int pid) parent = (await Prim.GetLocationAsync(pid))?.LocationName;
            _props = new()
            {
                ("Location Name", l.LocationName), ("Barcode", l.Barcode), ("Location Type", l.LocationType),
                ("Parent", parent ?? "—"), ("Description", l.Description ?? "—"),
                ("Created", $"{l.CreatedBy} · {l.CreatedUtc.ToLocalTime():g}"),
                ("Last Updated", $"{l.LastUpdatedBy} · {l.LastUpdatedUtc.ToLocalTime():g}"),
            };
            if (l.ParentId is int parentId) _links["Parent"] = ("Location", parentId);
        }
        else if (Kind == "User")
        {
            var u = await Prim.GetUserAsync(Id);
            if (u == null) return;
            _props = new()
            {
                ("User ID", u.UserId), ("Display Name", u.DisplayName), ("Role", u.Role),
                ("Email", u.Email ?? "—"), ("Active", u.Active ? "Yes" : "No"),
            };
        }
        _audit = await Prim.GetAuditAsync(Kind, Id);
    }

    public void Dispose() { }
}
