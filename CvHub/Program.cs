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
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
// AddDbContextFactory also registers ApplicationDbContext itself as a scoped service
// (used by Identity and the feature APIs), keeping the singleton factory consistent.
builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// ---------- Identity ----------
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, AppClaimsPrincipalFactory>();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

// ---------- Markdown ----------
builder.Services.AddSingleton(sp => new MarkdownPipelineBuilder()
    .UseAdvancedExtensions()
    .DisableHtml()
    .Build());

// ---------- App services ----------
builder.Services.AddScoped<IdentitySeeder>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
// HttpClient with BaseAddress for interactive components (DiscussionPanel, editors, pickers).
// Works in Server circuits and would work in WASM (same-origin relative calls).
builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("interactive");
    client.BaseAddress = new Uri(nav.BaseUri);
    return client;
});

var app = builder.Build();

// Migrate and seed on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<IdentitySeeder>().SeedAsync();
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
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
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
