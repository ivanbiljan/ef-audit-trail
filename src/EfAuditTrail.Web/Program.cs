using EfAuditTrail.ServiceDefaults;
using EfAuditTrail.Web.Components;
using EfAuditTrail.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// One persona per circuit: the back-office user "logged in" for this tab.
builder.Services.AddScoped<PersonaState>();

// "api" resolves through Aspire service discovery (WithReference(api) in the AppHost).
builder.Services.AddHttpClient<AuditTrailApi>(client =>
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https+http://api"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapDefaultEndpoints();

await app.RunAsync();
