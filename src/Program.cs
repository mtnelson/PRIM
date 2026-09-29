using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Prim.Data;
using Prim.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server (interactive) + MudBlazor (TIS-2459 target stack)
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

// --- Database ---
// Default: SQLite file next to the executable (zero-install, home-hostable).
// MSSQL target: set ConnectionStrings:Prim in appsettings.json and swap the
// two lines below (TIS-2456/2457/2458 migration direction).
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
var sqlitePath = Path.Combine(dataDir, "prim.db");
builder.Services.AddDbContextFactory<PrimDbContext>(opt =>
    opt.UseSqlite($"Data Source={sqlitePath}"));
// builder.Services.AddDbContextFactory<PrimDbContext>(opt =>
//     opt.UseSqlServer(builder.Configuration.GetConnectionString("Prim")));

builder.Services.AddScoped<PrimService>();
builder.Services.AddScoped<AppState>();

// Port is configurable via appsettings.json (Prim:HttpPort); defaults to 5000.
// An explicit --urls argument (or ASPNETCORE_URLS) still takes precedence.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    var httpPort = builder.Configuration.GetValue<int?>("Prim:HttpPort") ?? 5000;
    builder.WebHost.UseUrls($"http://localhost:{httpPort}");
}

var app = builder.Build();

// Seed on startup
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PrimDbContext>>();
    using var db = factory.CreateDbContext();
    db.Database.EnsureCreated();
    SeedData.EnsureSeeded(db);
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<Prim.Components.App>().AddInteractiveServerRenderMode();

app.Run();
