using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Vev.Atlas.Api;
using Vev.Atlas.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Atlas") ?? "Data Source=atlas.db";
builder.Services.AddAtlasCommunity(connectionString);

// Register the authentication the identity mode needs (JWT bearer for fabric-oidc); the matching request
// pipeline is wired below by UseAtlasRequestIdentity (fabric#3, atlas#34).
builder.AddAtlasRequestIdentity();

// Speak the same wire shape the contract publishes: omit null properties.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AtlasExceptionHandler>();

// Throttle the whole-landscape export so it cannot be pulled in a tight loop (atlas#36). Fixed window,
// partitioned per tenant, and configurable so an operator can tune it.
var exportPermitLimit = builder.Configuration.GetValue(ExportRateLimit.PermitLimitKey, ExportRateLimit.DefaultPermitLimit);
var exportWindowSeconds = builder.Configuration.GetValue(ExportRateLimit.WindowSecondsKey, ExportRateLimit.DefaultWindowSeconds);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(ExportRateLimit.PolicyName, http =>
    {
        var tenant = http.Request.Headers["X-Tenant-Id"].ToString();
        if (string.IsNullOrWhiteSpace(tenant))
        {
            tenant = "global";
        }

        return RateLimitPartition.GetFixedWindowLimiter(tenant, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = exportPermitLimit,
            Window = TimeSpan.FromSeconds(exportWindowSeconds),
            QueueLimit = 0
        });
    });
});

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

app.UseRateLimiter();

app.MapOpenApi();
// Health carries no identity: it must answer the container/orchestrator probe without a token, so it is
// exempt from the OIDC identity gate (see OidcRequestContextMiddleware).
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Ops").AllowAnonymous();
app.MapAtlasCommunityEndpoints();

await app.RunAsync();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry point.</summary>
public partial class Program;
