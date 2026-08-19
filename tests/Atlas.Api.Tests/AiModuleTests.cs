using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vev.Atlas.Contracts;
using Vev.Fabric.Contracts.Entitlements;
using Vev.Atlas.Fabric;
using Vev.Atlas.Persistence;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// AI module setup tests: consent + BYOK are persisted server-side, the key is never echoed back, and the
/// chat endpoint degrades or answers cleanly based on configuration.
/// </summary>
public sealed class AiModuleTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client(string tenant = "acme", string principal = "arch", string roles = "AtlasArchitect")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", principal);
        client.DefaultRequestHeaders.Add("X-Principal-Roles", roles);
        return client;
    }

    [Fact]
    public async Task Architect_can_enable_ai_module_and_the_key_is_never_returned()
    {
        var client = Client(tenant: "t-ai-setup");

        var save = await client.PutAsJsonAsync("/api/v1/ai/module", new
        {
            enabled = true,
            consentAccepted = true,
            provider = "openai",
            apiKey = "sk-test-123"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(saved.GetProperty("enabled").GetBoolean());
        Assert.True(saved.GetProperty("consentAccepted").GetBoolean());
        Assert.True(saved.GetProperty("apiKeyConfigured").GetBoolean());
        Assert.True(saved.GetProperty("ready").GetBoolean());
        Assert.Equal("openai", saved.GetProperty("provider").GetString());
        var saveBody = await save.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-test-123", saveBody, StringComparison.Ordinal);

        var status = await client.GetAsync("/api/v1/ai/module");
        var body = await status.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.DoesNotContain("sk-test-123", body, StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var stored = await db.Database
            .SqlQueryRaw<string>("SELECT EncryptedApiKey AS Value FROM ai_module_settings WHERE TenantId = 't-ai-setup'")
            .SingleAsync();
        Assert.NotEqual("sk-test-123", stored);
        Assert.False(string.IsNullOrWhiteSpace(stored));
    }

    [Fact]
    public async Task Read_only_customer_cannot_save_ai_module_settings()
    {
        var response = await Client(tenant: "t-ai-denied", principal: "viewer", roles: "AtlasCustomer")
            .PutAsJsonAsync("/api/v1/ai/module", new
            {
                enabled = true,
                consentAccepted = true,
                provider = "openai",
                apiKey = "sk-test-123"
            }, Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Chat_endpoint_prompts_for_setup_when_the_module_is_not_enabled()
    {
        var response = await Client(tenant: "t-ai-chat-setup").PostAsJsonAsync("/api/v1/ai/chat", new
        {
            question = "What depends on payments?"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("setup-required", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Chat_endpoint_returns_a_grounded_answer_when_ai_is_configured()
    {
        using var configuredFactory = new ConfiguredChatFactory();
        var client = configuredFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-ai-chat");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "arch");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("sys-payments", AssetKind.System, "Payments", Lifecycle.Active), Json);
        await client.PutAsJsonAsync("/api/v1/ai/module", new
        {
            enabled = true,
            consentAccepted = true,
            provider = "openai",
            apiKey = "sk-configured"
        }, Json);

        var response = await client.PostAsJsonAsync("/api/v1/ai/chat", new
        {
            question = "What is in this landscape?"
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Contains("grounded answer", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.True(body.GetProperty("docs").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Landscape_page_contains_the_ai_chat_and_setup_hooks()
    {
        var response = await factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("aiChatButton", body);
        Assert.Contains('"' + "/v1/ai/module" + '"', body);
        Assert.Contains('"' + "/v1/ai/chat" + '"', body);
        Assert.Contains("Atlas AI setup", body);
    }

    private sealed class ConfiguredChatFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AtlasDbContext>>();
                services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(_connection));
                services.RemoveAll<IAiAssistService>();
                services.AddScoped<IAiAssistService, TestAiAssistService>();
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

    private sealed class TestAiAssistService : IAiAssistService
    {
        public AiAssistResult Assist(AiAssistRequest request) =>
            AiAssistResult.Available("This is a grounded answer about the selected landscape.", "ai:test");
    }

    private sealed class TestEntitlementService : IEntitlementService, IEntitlementAllowanceProvider
    {
        public EntitlementDecision Evaluate(EntitlementRequest request) =>
            Vev.Fabric.Contracts.Entitlements.EntitlementDecision.Allow(request.Capability, "entitlement:test", TimeProvider.System.GetUtcNow());

        public Vev.Atlas.Fabric.EntitlementAllowanceSnapshot Describe(EntitlementAllowanceRequest request) =>
            Vev.Atlas.Fabric.EntitlementAllowanceSnapshot.UnlimitedAllowance("entitlement:test");
    }
}
