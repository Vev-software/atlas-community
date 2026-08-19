using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Portic;
using Vev.Fabric.Contracts.Entitlements;
using Vev.Atlas.Persistence;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Integration tests proving the Portic provider extension runs alongside built-in BYOK providers
/// (openai/anthropic) without interfering with them.
/// </summary>
public sealed class PorticProviderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Portic_provider_is_accepted_when_registered()
    {
        using var factory = new PorticTestFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-portic");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var save = await client.PutAsJsonAsync("/api/v1/ai/module", new
        {
            enabled = true,
            consentAccepted = true,
            provider = "portic",
            apiKey = "sk-portic-test"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var body = await save.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("portic", body.GetProperty("provider").GetString());
        Assert.True(body.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Openai_provider_still_works_when_Portic_is_registered()
    {
        using var factory = new PorticTestFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-openai-with-portic");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var save = await client.PutAsJsonAsync("/api/v1/ai/module", new
        {
            enabled = true,
            consentAccepted = true,
            provider = "openai",
            apiKey = "sk-test-123"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var body = await save.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("openai", body.GetProperty("provider").GetString());
        Assert.True(body.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Anthropic_provider_still_works_when_Portic_is_registered()
    {
        using var factory = new PorticTestFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-anthropic-with-portic");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var save = await client.PutAsJsonAsync("/api/v1/ai/module", new
        {
            enabled = true,
            consentAccepted = true,
            provider = "anthropic",
            apiKey = "sk-anth-test"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var body = await save.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("anthropic", body.GetProperty("provider").GetString());
        Assert.True(body.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Unknown_provider_is_rejected_even_when_Portic_is_registered()
    {
        using var factory = new PorticTestFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-unknown");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var save = await client.PutAsJsonAsync("/api/v1/ai/module", new
        {
            enabled = true,
            consentAccepted = true,
            provider = "nonexistent-provider",
            apiKey = "sk-test"
        }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
    }

    [Fact]
    public async Task Portic_assist_request_routes_to_extension()
    {
        using var factory = new PorticTestFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-portic-chat");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Vev.Atlas.Contracts.Asset("sys-payments", Vev.Atlas.Contracts.AssetKind.System, "Payments", Vev.Atlas.Contracts.Lifecycle.Active), Json);
        await client.PutAsJsonAsync("/api/v1/ai/module", new
        {
            enabled = true,
            consentAccepted = true,
            provider = "portic",
            apiKey = "sk-portic-test"
        }, Json);

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", new
        {
            question = "What is in this landscape?"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Contains("portic", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Portic_provider_works_without_api_key_when_registered()
    {
        using var factory = new PorticTestFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-portic-no-key");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var save = await client.PutAsJsonAsync("/api/v1/ai/module", new
        {
            enabled = true,
            consentAccepted = true,
            provider = "portic"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var body = await save.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("portic", body.GetProperty("provider").GetString());
        Assert.True(body.GetProperty("ready").GetBoolean());
        Assert.False(body.GetProperty("apiKeyConfigured").GetBoolean());
    }

    [Fact]
    public async Task Portic_chat_works_without_api_key_when_registered()
    {
        using var factory = new PorticTestFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-portic-no-key-chat");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Vev.Atlas.Contracts.Asset("sys-payments", Vev.Atlas.Contracts.AssetKind.System, "Payments", Vev.Atlas.Contracts.Lifecycle.Active), Json);
        await client.PutAsJsonAsync("/api/v1/ai/module", new
        {
            enabled = true,
            consentAccepted = true,
            provider = "portic"
        }, Json);

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", new
        {
            question = "What is in this landscape?"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Contains("portic", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Providers_endpoint_returns_all_registered_providers()
    {
        using var factory = new PorticTestFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-providers");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var response = await client.GetAsync("/api/v1/ai/providers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement[]>(Json);
        Assert.NotNull(body);

        var ids = body.Select(e => e.GetProperty("id").GetString()!).ToList();
        Assert.Contains("openai", ids);
        Assert.Contains("anthropic", ids);
        Assert.Contains("portic", ids);
    }

    [Fact]
    public async Task Providers_endpoint_marks_extension_providers_as_no_key_required()
    {
        using var factory = new PorticTestFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-providers-key");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        var response = await client.GetAsync("/api/v1/ai/providers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement[]>(Json);
        Assert.NotNull(body);

        var portic = body.First(e => e.GetProperty("id").GetString() == "portic");
        Assert.False(portic.GetProperty("requiresApiKey").GetBoolean());

        var openai = body.First(e => e.GetProperty("id").GetString() == "openai");
        Assert.True(openai.GetProperty("requiresApiKey").GetBoolean());
    }

    private sealed class PorticTestFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            builder.UseSetting("Atlas:Portic:BaseUrl", "https://portic.test.example/v1");
            builder.UseSetting("Atlas:Portic:Model", "gpt-4.1-mini");

            builder.ConfigureServices((hostingContext, services) =>
            {
                services.RemoveAll<DbContextOptions<AtlasDbContext>>();
                services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(_connection));
                services.RemoveAll<IAiAssistService>();
                services.AddScoped<IAiAssistService, TestPorticAssistService>();
                services.RemoveAll<IEntitlementService>();
                services.RemoveAll<IEntitlementAllowanceProvider>();
                services.AddSingleton<IEntitlementService, TestEntitlementService>();
                services.AddSingleton<IEntitlementAllowanceProvider, TestEntitlementService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }

    private sealed class TestPorticAssistService(
        IRequestContextAccessor context,
        IAiModuleConfigurationStore moduleStore,
        IEnumerable<IAiProviderExtension> providerExtensions) : IAiAssistService
    {
        private const string Source = "ai:unconfigured";
        private readonly Dictionary<string, IAiProviderExtension> _extensions =
            providerExtensions.ToDictionary(e => e.ProviderId, StringComparer.Ordinal);
        private readonly IReadOnlyList<string> _extensionProviderIds =
            providerExtensions.Select(e => e.ProviderId).ToList();

        public AiAssistResult Assist(AiAssistRequest request)
        {
            var configuration = moduleStore.GetAsync(context.Tenant).AsTask().GetAwaiter().GetResult();
            if (configuration?.IsUsableForProvider(_extensionProviderIds) != true)
            {
                return AiAssistResult.Unavailable(Source);
            }

            var provider = configuration.Provider!;
            if (_extensions.TryGetValue(provider, out var extension))
            {
                return AiAssistResult.Available("This is a portic grounded answer about the selected landscape.", "ai:portic");
            }

            return provider switch
            {
                "openai" => AiAssistResult.Available("This is a grounded answer about the selected landscape.", "ai:test"),
                "anthropic" => AiAssistResult.Available("This is a grounded answer about the selected landscape.", "ai:test"),
                _ => AiAssistResult.Unavailable(Source)
            };
        }
    }

    private sealed class TestEntitlementService : IEntitlementService, IEntitlementAllowanceProvider
    {
        public EntitlementDecision Evaluate(EntitlementRequest request) =>
            Vev.Fabric.Contracts.Entitlements.EntitlementDecision.Allow(request.Capability, "entitlement:test", TimeProvider.System.GetUtcNow());

        public Vev.Atlas.Fabric.EntitlementAllowanceSnapshot Describe(EntitlementAllowanceRequest request) =>
            Vev.Atlas.Fabric.EntitlementAllowanceSnapshot.UnlimitedAllowance("entitlement:test");
    }
}
