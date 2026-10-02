using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain.Sharing;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The landscape share digest as a file (atlas#175): an elevated, audited, throttled action that produces a
/// minimized digest which validates against the published v1 schema and verifies against its detached signature,
/// and that never carries data the contract forbids, even when the landscape holds it.
/// </summary>
public sealed class LandscapeDigestShareTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client(string tenant, string roles = "AtlasArchitect")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Principal-Id", "p");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", roles);
        return client;
    }

    private static async Task SeedAsync(HttpClient client)
    {
        // Everything below the digest must never see: descriptions, hostnames, owners, data-layer assets, relationship text.
        Asset[] assets =
        [
            new("sys-payments", AssetKind.System, "Payments platform", Lifecycle.Active, Description: "SECRET-DESCRIPTION", Tags: [new Tag("shared", "true")]),
            new("app-checkout", AssetKind.Application, "Checkout", Lifecycle.Active, Tags: [new Tag("shared", "true")],
                Application: new ApplicationDetails(Version: "SECRET-VERSION", Vendor: "Example Corp", BusinessOwner: "SECRET-OWNER")),
            new("app-internal", AssetKind.Application, "Internal tool", Lifecycle.Draft, Application: new ApplicationDetails(Vendor: "Other Vendor")),
            new("srv-1", AssetKind.Server, "App server", Lifecycle.Active, Tags: [new Tag("shared", "true")],
                Server: new ServerDetails(Hostname: "secret-host.internal", Environment: "SECRET-ENV", OperatingSystem: "SECRET-OS")),
            new("infra-1", AssetKind.Infrastructure, "EU compute", Lifecycle.Active, Infrastructure: new InfrastructureDetails(Category: "cloud", Location: "SECRET-LOCATION")),
            new("ai-1", AssetKind.AiService, "Assistant", Lifecycle.Active, AiService: new AiServiceDetails(Provider: "Example Cloud", Endpoint: "https://secret-endpoint.example")),
            new("area-1", AssetKind.DataArea, "Customer data", Lifecycle.Active),
            new("ds-1", AssetKind.Dataset, "Orders", Lifecycle.Active, Dataset: new DatasetDetails(PhysicalName: "secret_orders_table", Owner: "SECRET-DATA-OWNER")),
            new("col-1", AssetKind.Column, "email", Lifecycle.Active, Column: new ColumnDetails(DataType: "SECRET-TYPE")),
        ];
        foreach (var asset in assets)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/assets", asset, Json)).StatusCode);
        }

        Relationship[] relationships =
        [
            new("r1", "sys-payments", "app-checkout", RelationshipType.ConnectsTo, "SECRET-RELATIONSHIP-TEXT"),
            new("r2", "app-checkout", "app-internal", RelationshipType.ConnectsTo),
            new("r3", "app-checkout", "srv-1", RelationshipType.RunsOn),
        ];
        foreach (var relationship in relationships)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/relationships", relationship, Json)).StatusCode);
        }
    }

    private static async Task<JsonNode> DigestAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/api/v1/share/digest" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    // ---- Authorization, audit and throttling ------------------------------------------------

    [Fact]
    public async Task A_read_only_customer_cannot_create_or_preview_a_digest()
    {
        var client = Client("t-share-denied", "AtlasCustomer");

        var create = await client.GetAsync("/api/v1/share/digest");
        var preview = await client.GetAsync("/api/v1/share/digest/preview");

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("role_missing", await create.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.DoesNotContain(factory.Services.GetRequiredService<InMemoryAuditSink>().Events, e => e.Tenant.TenantId == "t-share-denied" && e.Action == "atlas.landscape.shared");
    }

    [Fact]
    public async Task Each_digest_writes_exactly_one_audit_record_with_counts_and_no_names_and_a_preview_writes_none()
    {
        var client = Client("t-share-audit");
        await SeedAsync(client);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/share/digest/preview")).StatusCode);
        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.DoesNotContain(audit.Events, e => e.Tenant.TenantId == "t-share-audit" && e.Action == "atlas.landscape.shared");

        await DigestAsync(client);
        await DigestAsync(client, "?kinds=system");

        var events = audit.Events.Where(e => e.Tenant.TenantId == "t-share-audit" && e.Action == "atlas.landscape.shared").OrderBy(e => e.OccurredAt).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal("p", events[0].Actor.PrincipalId);
        Assert.Contains("sequence=1", events[0].Resource.Value);
        Assert.Contains("sequence=2", events[1].Resource.Value);
        Assert.Contains("kinds=system", events[1].Resource.Value);
        Assert.DoesNotContain("Payments", events[0].Resource.Value);   // metadata only, never item content
    }

    [Fact]
    public async Task Creating_digests_is_throttled_per_tenant()
    {
        using var throttled = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(ShareRateLimit.PermitLimitKey, "2");
            b.UseSetting(ShareRateLimit.WindowSecondsKey, "60");
        });
        var client = throttled.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "t-share-throttle");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "p");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/share/digest")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/share/digest")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/v1/share/digest")).StatusCode);
    }

    // ---- The contract ----------------------------------------------------------------------

    private static JsonSchema DigestSchema()
    {
        var directory = typeof(LandscapeDigestShareTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "AtlasContractsSchemas").Value!;
        var registry = new SchemaRegistry();
        var options = new BuildOptions { SchemaRegistry = registry };
        JsonSchema? digest = null;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var schema = JsonSchema.FromText(File.ReadAllText(file), options);
            if (Path.GetFileName(file) == "landscape-digest.schema.json") digest = schema;
        }

        return digest!;
    }

    [Fact]
    public async Task The_digest_validates_against_the_published_v1_schema_and_its_signature_verifies()
    {
        var client = Client("t-share-valid");
        await SeedAsync(client);

        var signed = await DigestAsync(client);

        var digest = signed["digest"]!;
        var result = DigestSchema().Evaluate(JsonSerializer.SerializeToElement(digest), new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, string.Join("; ", (result.Details ?? []).Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
        Assert.Equal("1", digest["contractVersion"]!.GetValue<string>());
        Assert.Equal(1, digest["sequence"]!.GetValue<int>());

        var signature = signed["signature"]!;
        Assert.Equal("rsa-pkcs1-v1_5-sha256", signature["algorithm"]!.GetValue<string>());
        Assert.True(Verify(signature, digest), "The detached signature must verify over the canonical digest.");
        // A changed digest no longer verifies: the signature binds exactly the delivered items.
        digest["items"]![0]!["name"] = "Tampered";
        Assert.False(Verify(signature, digest));
    }

    [Fact]
    public void The_canonicalization_reproduces_the_signature_of_the_published_test_vector()
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "landscape-digest.signed.valid.json")))!;

        Assert.True(Verify(sample["signature"]!, sample["digest"]!));
    }

    private static bool Verify(JsonNode signature, JsonNode digest)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(FromBase64Url(signature["publicKey"]!.GetValue<string>()), out _);
        return rsa.VerifyData(Jcs.CanonicalizeUtf8(digest), FromBase64Url(signature["value"]!.GetValue<string>()),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static byte[] FromBase64Url(string text) =>
        Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=').Replace('-', '+').Replace('_', '/'));

    // ---- Minimization ----------------------------------------------------------------------

    [Fact]
    public async Task Forbidden_data_never_appears_in_the_digest_even_when_the_landscape_holds_it()
    {
        var client = Client("t-share-minimized");
        await SeedAsync(client);

        var text = (await DigestAsync(client)).ToJsonString();

        foreach (var forbidden in new[]
                 {
                     "SECRET-DESCRIPTION", "SECRET-VERSION", "SECRET-OWNER", "secret-host", "SECRET-ENV", "SECRET-OS", "SECRET-LOCATION", "secret-endpoint",
                     "secret_orders_table", "SECRET-DATA-OWNER", "SECRET-TYPE", "SECRET-RELATIONSHIP-TEXT", "Customer data", "Orders", "email", "sys-payments", "app-checkout",
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Items_carry_kind_name_lifecycle_vendor_and_integration_counts_and_data_layer_assets_are_left_out()
    {
        var client = Client("t-share-items");
        await SeedAsync(client);

        var items = (await DigestAsync(client))["digest"]!["items"]!.AsArray().Select(i => i!.AsObject()).ToList();

        var checkout = items.Single(i => i["name"]!.GetValue<string>() == "Checkout");
        Assert.Equal(("application", "active", "Example Corp", 2), (checkout["kind"]!.GetValue<string>(), checkout["lifecycle"]!.GetValue<string>(),
            checkout["vendor"]!.GetValue<string>(), checkout["integrationCount"]!.GetValue<int>()));
        Assert.Equal(1, items.Single(i => i["name"]!.GetValue<string>() == "Payments platform")["integrationCount"]!.GetValue<int>());
        // A run-on relationship is not an integration; servers, infrastructure and AI services fold into platform.
        Assert.Equal("platform", items.Single(i => i["name"]!.GetValue<string>() == "App server")["kind"]!.GetValue<string>());
        Assert.Equal("Example Cloud", items.Single(i => i["name"]!.GetValue<string>() == "Assistant")["vendor"]!.GetValue<string>());
        Assert.Equal(["Example Cloud", "Example Corp", "Other Vendor"], items.Where(i => i["kind"]!.GetValue<string>() == "vendor").Select(i => i["name"]!.GetValue<string>()).Order());
        Assert.All(items, i => Assert.Contains(i["kind"]!.GetValue<string>(), new[] { "system", "application", "platform", "vendor" }));
        Assert.DoesNotContain(items, i => i.ContainsKey("description") || i.ContainsKey("id") || i.ContainsKey("tags"));
    }

    // ---- Scope -----------------------------------------------------------------------------

    [Fact]
    public async Task The_scope_filters_by_kind_and_tag_and_every_item_kind_is_in_the_scope()
    {
        var client = Client("t-share-scope");
        await SeedAsync(client);

        var tagged = await DigestAsync(client, "?kinds=application&kinds=vendor&tag=shared:true");

        var digest = tagged["digest"]!;
        Assert.Equal(["application", "vendor"], digest["scope"]!["kinds"]!.AsArray().Select(k => k!.GetValue<string>()).Order());
        Assert.Equal("shared", digest["scope"]!["tags"]![0]!["key"]!.GetValue<string>());
        var items = digest["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        // Only the tagged application qualifies, and the vendor entry follows from it alone.
        Assert.Equal(["Checkout", "Example Corp"], items.Select(i => i["name"]!.GetValue<string>()).Order());
        var kinds = digest["scope"]!["kinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToHashSet();
        Assert.All(items, i => Assert.Contains(i["kind"]!.GetValue<string>(), kinds));
    }

    [Fact]
    public async Task An_unknown_kind_or_a_malformed_tag_is_rejected_not_ignored()
    {
        var client = Client("t-share-bad-scope");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/share/digest?kinds=dataset")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/share/digest?tag=nocolon")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/share/digest/preview?kinds=nonsense")).StatusCode);
    }

    // ---- Sequence, source and key ----------------------------------------------------------

    [Fact]
    public async Task The_sequence_increases_per_tenant_the_source_is_stable_and_opaque_and_the_installation_key_is_shared()
    {
        var a = Client("t-share-seq-a");
        var b = Client("t-share-seq-b");

        var a1 = await DigestAsync(a);
        var a2 = await DigestAsync(a);
        var b1 = await DigestAsync(b);

        Assert.Equal((1, 2, 1), (a1["digest"]!["sequence"]!.GetValue<int>(), a2["digest"]!["sequence"]!.GetValue<int>(), b1["digest"]!["sequence"]!.GetValue<int>()));
        Assert.Equal(a1["digest"]!["sourceInstanceId"]!.GetValue<string>(), a2["digest"]!["sourceInstanceId"]!.GetValue<string>());
        Assert.NotEqual(a1["digest"]!["sourceInstanceId"]!.GetValue<string>(), b1["digest"]!["sourceInstanceId"]!.GetValue<string>());
        Assert.DoesNotContain("t-share-seq", a1["digest"]!["sourceInstanceId"]!.GetValue<string>());
        Assert.NotEqual(a1["digest"]!["digestId"]!.GetValue<string>(), a2["digest"]!["digestId"]!.GetValue<string>());
        Assert.Equal(a1["signature"]!["keyId"]!.GetValue<string>(), b1["signature"]!["keyId"]!.GetValue<string>());
        Assert.Equal(a1["signature"]!["publicKey"]!.GetValue<string>(), a2["signature"]!["publicKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_preview_shows_the_items_and_the_field_rule_without_using_a_sequence_number()
    {
        var client = Client("t-share-preview");
        await SeedAsync(client);

        var preview = JsonNode.Parse(await client.GetStringAsync("/api/v1/share/digest/preview?kinds=application"))!;

        Assert.Equal(["Checkout", "Internal tool"], preview["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()).Order());
        Assert.Contains(preview["neverShared"]!.AsArray().Select(n => n!.GetValue<string>()), n => n.Contains("hostnames"));
        Assert.Equal(1, (await DigestAsync(client))["digest"]!["sequence"]!.GetValue<int>());
    }
}
