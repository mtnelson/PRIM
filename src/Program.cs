using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Rim.Data;
using Rim.Services;

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
// PRESERVED NAMES (do not rename): the SQLite file stays "prim.db", the
// connection-string key stays "Prim", and the config section stays "Prim:" —
// existing user databases and appsettings.json files must keep working.
var sqlitePath = Path.Combine(dataDir, "prim.db");
// Ring buffer of recently executed advanced-search SQL, shown on the
// Advanced Search page's SQL tab. RimService records into it via
// ToQueryString (provider-agnostic); untagged queries cost nothing.
builder.Services.AddSingleton<SearchQueryLog>();
builder.Services.AddDbContextFactory<RimDbContext>(opt =>
    opt.UseSqlite($"Data Source={sqlitePath}"));
// builder.Services.AddDbContextFactory<RimDbContext>(opt =>
//     opt.UseSqlServer(builder.Configuration.GetConnectionString("Prim")));

builder.Services.AddScoped<RimService>();
builder.Services.AddScoped<AppState>();
builder.Services.AddScoped<HotkeyManager>();
// Content Manager 24.3 third-party integrations:
// LabelPdfService (PDFsharp — the same PDF library CM bundles) renders
// inventory labels as PDF; BarcodeImageService (ZXing + SkiaSharp) decodes
// barcodes from uploaded images on the scanning page.
builder.Services.AddSingleton<LabelPdfService>();
builder.Services.AddSingleton<BarcodeImageService>();

// NLog (also from CM's third-party set): persistent server log at
// <exe-dir>/logs/rim-YYYY-MM-DD.log plus console. The in-app activity feed
// stays in AppState; this is the on-disk trail for support and diagnosis.
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);
var logLayout = "${longdate}|${level:uppercase=true}|${logger}|${message}${onexception:${newline}${exception:format=tostring}}";
var nlogConfig = new NLog.Config.LoggingConfiguration();
nlogConfig.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal,
    new NLog.Targets.FileTarget("rimfile")
    {
        FileName = Path.Combine(logDir, "rim-${shortdate}.log"),
        Layout = logLayout,
        ArchiveAboveSize = 10 * 1024 * 1024,
        MaxArchiveFiles = 14,
    });
nlogConfig.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal,
    new NLog.Targets.ConsoleTarget("rimconsole") { Layout = logLayout });
NLog.LogManager.Configuration = nlogConfig;
var bootLog = NLog.LogManager.GetCurrentClassLogger();
bootLog.Info("RIM starting.");
// Authentication is always behind IAuthProvider. Development password logins
// are opt-in and fail closed: DevPasswordAuthProvider registers ONLY when
// Auth:AllowDevPasswords is explicitly true; otherwise a disabled provider is
// registered and password login is impossible. Production registers an
// OAuth/SSO IAuthProvider. Never read AppUser.PasswordHash/PasswordSalt
// directly from UI code.
var allowDevPasswords = builder.Configuration.GetValue<bool>("Auth:AllowDevPasswords");
if (allowDevPasswords)
    builder.Services.AddScoped<IAuthProvider, DevPasswordAuthProvider>();
else
    builder.Services.AddScoped<IAuthProvider, DisabledAuthProvider>();

// Port is configurable via appsettings.json (Prim:HttpPort); defaults to 5000.
// An explicit --urls argument (or ASPNETCORE_URLS) still takes precedence.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    var httpPort = builder.Configuration.GetValue<int?>("Prim:HttpPort") ?? 5000;
    builder.WebHost.UseUrls($"http://localhost:{httpPort}");
}

var app = builder.Build();

// H6: loud startup warning when development password auth is active —
// this must never run in production.
if (allowDevPasswords)
{
    app.Logger.LogWarning("SECURITY WARNING: development password authentication is ENABLED " +
        "(Auth:AllowDevPasswords=true). Do not use in production — register an OAuth/SSO " +
        "IAuthProvider and remove the flag.");
    bootLog.Warn("SECURITY WARNING: development password authentication is ENABLED.");
}

// Seed on startup. EnsureCreated does NOT add new columns/tables to an
// existing database, so SchemaUpgrader backfills anything the old file lacks
// (new dev fields are added in this revision) before seeding.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RimDbContext>>();
    using var db = factory.CreateDbContext();
    db.Database.EnsureCreated();
    SeedData.UpgradeSchema(db);
    SeedData.EnsureSeeded(db);
    SeedData.BackfillDevCredentials(db);
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<Rim.Components.App>().AddInteractiveServerRenderMode();

bootLog.Info("RIM started.");
try
{
    app.Run();
}
catch (Exception ex)
{
    bootLog.Fatal(ex, "RIM terminated unexpectedly.");
    throw;
}
finally
{
    NLog.LogManager.Shutdown();
}
