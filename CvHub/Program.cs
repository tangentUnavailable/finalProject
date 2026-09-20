using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using CvHub.Components;
using CvHub.Components.Account;
using CvHub.Data;
using CvHub.Features.Admin;
using CvHub.Features.Attributes;
using CvHub.Features.Cvs;
using CvHub.Features.Discussions;
using CvHub.Features.Positions;
using CvHub.Features.Profile;
using CvHub.Features.Search;
using CvHub.Endpoints;
using CvHub.Services;
using CvHub.Shared;
using Markdig;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------- Circuit configuration (keep alive longer) ----------
// NOTE: AddServerSideBlazor() (the legacy Blazor Server model) must NOT be combined with
// MapRazorComponents() (Blazor Web). Mixing the two breaks <AntiforgeryToken /> — the
// EndpointAntiforgeryStateProvider never initializes, form posts get 400s
// (see dotnet/aspnetcore#65070). CircuitOptions is configured via plain options instead.
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options =>
{
    options.DetailedErrors = true;
    options.DisconnectedCircuitMaxRetained = 100;
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);
});

// ---------- SignalR keep-alive ----------
builder.Services.AddSignalR(hubOptions =>
{
    hubOptions.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
    hubOptions.HandshakeTimeout = TimeSpan.FromSeconds(30);
    hubOptions.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// ---------- Razor components (Auto render mode) ----------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

// ---------- Authentication (cookies + external social providers) ----------
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// Social login providers are registered only when configured (two supported: Google + Facebook).
var googleId = builder.Configuration["Authentication:Google:ClientId"];
var googleSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleId) && !string.IsNullOrWhiteSpace(googleSecret))
{
    builder.Services.AddAuthentication().AddGoogle(o =>
    {
        o.ClientId = googleId;
        o.ClientSecret = googleSecret;
        o.CallbackPath = "/signin-google";
    });
}

var fbId = builder.Configuration["Authentication:Facebook:AppId"];
var fbSecret = builder.Configuration["Authentication:Facebook:AppSecret"];
if (!string.IsNullOrWhiteSpace(fbId) && !string.IsNullOrWhiteSpace(fbSecret))
{
    builder.Services.AddAuthentication().AddFacebook(o =>
    {
        o.AppId = fbId;
        o.AppSecret = fbSecret;
        o.CallbackPath = "/signin-facebook";
    });
}

// ---------- Database (PostgreSQL) ----------
// Connection string order of precedence:
// 1. CONNECTION_STRINGS__DEFAULTCONNECTION env var (standard .NET)
// 2. SUPABASE_CONNECTION_STRING env var (platform override)
// 3. appsettings.json DefaultConnection
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("SUPABASE_CONNECTION_STRING")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
// AddDbContextFactory also registers ApplicationDbContext itself as a scoped service
// (used by Identity and the feature APIs), keeping the singleton factory consistent.
builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ---------- Identity ----------
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = builder.Configuration.GetValue<bool>("Identity:RequireConfirmedEmail", false);
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, AppClaimsPrincipalFactory>();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, EmailSender>();

// ---------- Markdown ----------
builder.Services.AddSingleton(sp => new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()
    .DisableHtml()
    .Build());

// ---------- App services ----------
builder.Services.AddScoped<IdentitySeeder>();
builder.Services.AddScoped<DbSeeder>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
// HttpClient with BaseAddress for interactive components (DiscussionPanel, editors, pickers).
// Works in Server circuits and would work in WASM (same-origin relative calls).
// Note: in Server circuits this client carries no auth cookie (IHttpContextAccessor is
// null inside a circuit) — auth-protected APIs must be consumed via per-host services
// (see IAttributeLibrary) or kept unauthenticated.
builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("interactive");
    client.BaseAddress = new Uri(nav.BaseUri);
    return client;
});

// Per-host services for InteractiveAuto components: DB-backed on the server (has the
// real user context), HTTP-backed in WASM (browser sends the auth cookie itself).
builder.Services.AddScoped<CvHub.Services.AttributeLibraryServer>();
builder.Services.AddScoped<CvHub.Shared.IAttributeLibrary>(sp => sp.GetRequiredService<CvHub.Services.AttributeLibraryServer>());
builder.Services.AddScoped<CvHub.Services.DiscussionServiceServer>();
builder.Services.AddScoped<CvHub.Shared.IDiscussionService>(sp => sp.GetRequiredService<CvHub.Services.DiscussionServiceServer>());

var app = builder.Build();

// Migrate and seed on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<IdentitySeeder>().SeedAsync();
    await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
// app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true); // Disabled - interferes with Blazor circuit
app.UseHttpsRedirection();

// Per-request UI language from cookie (EN/ES; only UI strings are translated).
app.Use((ctx, next) =>
{
    I18n.Current = ctx.Request.Cookies["cv_lang"] ?? "en";
    return next();
});

app.UseAntiforgery();

app.MapStaticAssets();

PrefsEndpoints.MapPrefsEndpoints(app);
ProfileApi.MapProfileApi(app);
AttributesApi.MapAttributesApi(app);
PositionsApi.MapPositionsApi(app);
CvsApi.MapCvsApi(app);
CommentsApi.MapCommentsApi(app);
SearchEndpoints.MapSearchEndpoints(app);
AdminApi.MapAdminApi(app);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(CvHub.Client._Imports).Assembly);

// Identity /Account endpoints.
app.MapAdditionalIdentityEndpoints();

app.Run();
