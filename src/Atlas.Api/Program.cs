using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Api;
using Vev.Atlas.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Atlas") ?? "Data Source=atlas.db";
builder.Services.AddAtlasCommunity(connectionString);

// Speak the same wire shape the contract publishes: omit null properties.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AtlasExceptionHandler>();

var app = builder.Build();

// Create the schema on first run so a self-hoster can `docker compose up` and go.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseExceptionHandler();

// The read-only landscape browser (atlas#6) is a static single-page client of the API, served from
// wwwroot so a self-hoster gets a visualisation out of the box with no separate front-end to deploy.
app.UseDefaultFiles();
app.UseStaticFiles();

// Establish request identity for this environment, failing closed when no trustworthy source exists.
// Development gets the header shim; any other environment refuses to start until Fabric OIDC (fabric#3)
// is wired, rather than trusting caller-supplied identity headers (atlas#34).
app.UseAtlasRequestIdentity();

app.MapOpenApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Ops");
app.MapAtlasCommunityEndpoints();

await app.RunAsync();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry point.</summary>
public partial class Program;
