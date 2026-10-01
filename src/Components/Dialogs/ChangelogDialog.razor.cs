using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using MudBlazor;
using Rim.Components.Dialogs;
using Rim.Components.Layout;
using Rim.Components.Shared;
using Rim.Data;
using Rim.Services;

namespace Rim.Components.Dialogs;

public partial class ChangelogDialog : ComponentBase
{
    [Inject] public IWebHostEnvironment Env { get; set; } = default!;

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = null!;

    private readonly List<Block> _blocks = new();
    private record Block(string Kind, string Text = "", List<string>? Items = null);

    protected override void OnInitialized()
    {
        try
        {
            var path = Path.Combine(Env.WebRootPath, "changelog.md");
            if (!File.Exists(path))
            {
                _blocks.Add(new Block("p", "No changelog found."));
                return;
            }
            List<string>? pending = null;
            void Flush()
            {
                if (pending is { Count: > 0 }) _blocks.Add(new Block("ul", Items: pending));
                pending = null;
            }
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("# ")) { Flush(); _blocks.Add(new Block("h1", line[2..].Trim())); }
                else if (line.StartsWith("## ")) { Flush(); _blocks.Add(new Block("h2", line[3..].Trim())); }
                else if (line.StartsWith("- ")) { pending ??= new List<string>(); pending.Add(line[2..].Trim()); }
                else if (string.IsNullOrWhiteSpace(line)) { Flush(); }
                else { Flush(); _blocks.Add(new Block("p", line.Trim())); }
            }
            Flush();
            if (_blocks.Count == 0) _blocks.Add(new Block("p", "Changelog is empty."));
        }
        catch
        {
            _blocks.Clear();
            _blocks.Add(new Block("p", "Could not read the changelog."));
        }
    }
}
