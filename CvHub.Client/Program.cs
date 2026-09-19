using CvHub.Client.Services;
using CvHub.Shared;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

// Base-addressed HttpClient for WASM services (same-origin API calls carry the auth cookie).
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddScoped<IAttributeLibrary, AttributeLibraryApi>();
builder.Services.AddScoped<IDiscussionService, DiscussionServiceApi>();

await builder.Build().RunAsync();
