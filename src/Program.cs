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
// Ring buffer of recently executed advanced-search SQL, shown on the
// Advanced Search page's SQL tab. PrimService records into it via
// ToQueryString (provider-agnostic); untagged queries cost nothing.
builder.Services.AddSingleton<SearchQueryLog>();
builder.Services.AddDbContextFactory<PrimDbContext>(opt =>
    opt.UseSqlite($"Data Source={sqlitePath}"));
// builder.Services.AddDbContextFactory<PrimDbContext>(opt =>
//     opt.UseSqlServer(builder.Configuration.GetConnectionString("Prim")));

builder.Services.AddScoped<PrimService>();
builder.Services.AddScoped<AppState>();
builder.Services.AddScoped<HotkeyManager>();
// Authentication is always behind IAuthProvider: DevPasswordAuthProvider for the
// prototype (temporary username/password logins), OAuth/SSO later. Never read
// AppUser.PasswordHash/PasswordSalt directly from UI code.
builder.Services.AddScoped<IAuthProvider, DevPasswordAuthProvider>();

// Port is configurable via appsettings.json (Prim:HttpPort); defaults to 5000.
// An explicit --urls argument (or ASPNETCORE_URLS) still takes precedence.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    var httpPort = builder.Configuration.GetValue<int?>("Prim:HttpPort") ?? 5000;
    builder.WebHost.UseUrls($"http://localhost:{httpPort}");
}

var app = builder.Build();

// Seed on startup. EnsureCreated does NOT add new columns/tables to an
// existing database, so SchemaUpgrader backfills anything the old file lacks
// (new dev fields are added in this revision) before seeding.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PrimDbContext>>();
    using var db = factory.CreateDbContext();
    db.Database.EnsureCreated();
    SeedData.UpgradeSchema(db);
    SeedData.EnsureSeeded(db);
    SeedData.BackfillDevCredentials(db);
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<Prim.Components.App>().AddInteractiveServerRenderMode();

app.Run();
