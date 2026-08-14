using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Vev.Atlas.Api;
using Vev.Atlas.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Atlas") ?? "Data Source=atlas.db";
builder.Services.AddAtlasCommunity(connectionString);
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<AtlasMcpTools>();

// Canonical URL/path configuration (atlas#19): hostnames and paths are deployment config, never a baked-in
// VEV identity. Defaults are a flat single-host shape, so a self-hoster sets nothing.
builder.Services.Configure<AtlasUrlOptions>(builder.Configuration.GetSection(AtlasUrlOptions.SectionName));
builder.Services.AddSingleton<AtlasUrls>();

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
var urls = app.Services.GetRequiredService<AtlasUrls>();

// Reverse-proxy sub-path support (atlas#19): when the deployment is hosted under a base path (e.g.
// "/atlas"), everything — the UI, static files and the API — hangs off it. Must run before routing and
// the static-file middleware. Empty base (the default) leaves the app at the host root.
if (urls.PathBase.Length > 0)
{
    app.UsePathBase(urls.PathBase);
}

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
app.MapMcp("/mcp");
// Health carries no identity: it must answer the container/orchestrator probe without a token, so it is
// exempt from the OIDC identity gate (see OidcRequestContextMiddleware).
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Ops").AllowAnonymous();

// Runtime config for the static SPA (atlas#19): hands the browser the from-origin API base (path base
// included), so the UI is never hard-coded to "/api" and works under a reverse-proxy sub-path. Anonymous —
// the page loads it before any sign-in — and served from a path-relative <script> so it resolves under the
// path base.
app.MapGet("/app-config.js", (HttpRequest request, AtlasUrls u) =>
{
    var config = new { apiBase = u.ClientApiBase(request), loginPath = u.ClientLoginPath(request) };
    var js = $"window.__ATLAS__=Object.freeze({JsonSerializer.Serialize(config)});";
    return Results.Text(js, "application/javascript");
}).WithTags("Ops").AllowAnonymous();

app.MapAtlasCommunityEndpoints(urls.ApiBasePath);

await app.RunAsync();

/// <summary>Exposed so the integration test host (WebApplicationFactory) can reference the entry point.</summary>
public partial class Program;
