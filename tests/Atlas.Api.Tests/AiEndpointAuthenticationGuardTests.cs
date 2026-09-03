using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vev.Atlas.Domain;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// AI endpoints call out to a provider and consume a metered allowance, so they must never run for an
/// unauthenticated caller (atlas#148). This guards two things: no AI endpoint may ship as
/// <c>AllowAnonymous</c> (a structural check that breaks the build if a future one does), and in an
/// identity mode that can be unauthenticated (Fabric OIDC), every AI endpoint refuses a request that
/// carries no token. (That the Development-only header default identity never leaks to other environments
/// is covered by <c>IdentityProfileTests</c>.)
/// </summary>
public sealed class AiEndpointAuthenticationGuardTests
{
    private static bool IsAiRoute(string? raw) =>
        raw is not null &&
        (raw.Contains("/ai/", StringComparison.Ordinal)
         || raw.EndsWith("/structure/draft", StringComparison.Ordinal)
         || raw.EndsWith("/deliverables/draft", StringComparison.Ordinal)
         || raw.EndsWith("/context-pack", StringComparison.Ordinal)
         || raw.EndsWith("/setup-copilot", StringComparison.Ordinal));

    [Fact]
    public void No_AI_endpoint_is_marked_AllowAnonymous()
    {
        using var factory = new AtlasApiFactory();

        var aiEndpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => IsAiRoute(endpoint.RoutePattern.RawText))
            .ToArray();

        // Guard the guard: if the route set ever stops matching, this would silently pass.
        Assert.NotEmpty(aiEndpoints);

        var anonymous = aiEndpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        Assert.True(
            anonymous.Length == 0,
            $"AI endpoints must require an authenticated principal; found AllowAnonymous on: {string.Join(", ", anonymous)}");
    }

    [Theory]
    [InlineData("GET", "/api/v1/ai/allowances")]
    [InlineData("GET", "/api/v1/ai/module")]
    [InlineData("GET", "/api/v1/ai/providers")]
    [InlineData("POST", "/api/v1/ai/chat")]
    [InlineData("POST", "/api/v1/structure/draft")]
    [InlineData("POST", "/api/v1/deliverables/draft")]
    [InlineData("GET", "/api/v1/setup-copilot")]
    public async Task AI_endpoints_refuse_a_request_with_no_token(string method, string path)
    {
        using var host = new OidcTestHost();
        using var client = host.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_caller_is_admitted_to_an_AI_endpoint()
    {
        // Positive control: the guard rejects only the unauthenticated — a verified, tenant-bound token
        // is admitted (not 401).
        using var host = new OidcTestHost();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", host.CreateToken(tenant: "t-ai", roles: AtlasRoles.Architect));

        using var response = await client.GetAsync("/api/v1/ai/allowances");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
