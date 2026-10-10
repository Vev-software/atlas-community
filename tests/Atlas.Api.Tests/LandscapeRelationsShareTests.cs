using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vev.Atlas.Api.Sharing;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Domain.Sharing;
using Vev.Atlas.Domain.Sharing.Push;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Vev.Fabric.Contracts.Entitlements;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Landscape digest v2, relations stage 1 (atlas#180): an opt-in, separately entitled addition to the share digest. With relations off the output is the
/// v1 digest of before; with them on it carries only <c>ref</c>, <c>layer</c> and the closed <c>runsOn</c> / <c>suppliedBy</c> edges between items that are
/// in the digest, validates against the published v2 contract, and is signed like v1. The switch is consent, never authorization: the separate relations
/// grant is required next to the sharing permission, for the file, the preview and every push.
/// </summary>
public sealed class LandscapeRelationsShareTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Destination = "https://consumer.example/api/atlas";

    // ---- The builder -------------------------------------------------------------------------

    private static readonly Asset[] Assets =
    [
        new("sys-payments", AssetKind.System, "Payments platform", Lifecycle.Active, Description: "SECRET-DESCRIPTION"),
        new("app-checkout", AssetKind.Application, "Checkout", Lifecycle.Active, Application: new ApplicationDetails(Vendor: "Example Corp", BusinessOwner: "SECRET-OWNER")),
        new("app-internal", AssetKind.Application, "Internal tool", Lifecycle.Draft, Application: new ApplicationDetails(Vendor: "example corp")),
        new("srv-1", AssetKind.Server, "App server", Lifecycle.Active, Server: new ServerDetails(Hostname: "secret-host.internal")),
        new("infra-1", AssetKind.Infrastructure, "EU compute", Lifecycle.Active),
        new("area-1", AssetKind.DataArea, "Customer data", Lifecycle.Active),
    ];

    private static readonly Relationship[] Relationships =
    [
        new("r1", "sys-payments", "app-checkout", RelationshipType.ConnectsTo, "SECRET-RELATIONSHIP-TEXT"),
        new("r2", "app-checkout", "srv-1", RelationshipType.RunsOn),
        // The same fact stated both ways must become one edge, as the contract forbids duplicates.
        new("r3", "app-internal", "infra-1", RelationshipType.RunsOn),
        new("r4", "infra-1", "app-internal", RelationshipType.Hosts),
        // Never exported: containment, dependency, data-layer links and edges to something that is not a platform.
        new("r5", "app-checkout", "sys-payments", RelationshipType.PartOf),
        new("r6", "app-checkout", "app-internal", RelationshipType.DependsOn),
        new("r7", "srv-1", "app-checkout", RelationshipType.RunsOn),
        new("r8", "app-checkout", "area-1", RelationshipType.RunsOn),
    ];

    private static LandscapeDigestV2 Build(LandscapeDigestScope? scope = null, Asset[]? assets = null, Relationship[]? relationships = null) =>
        LandscapeDigestBuilder.BuildV2(assets ?? Assets, relationships ?? Relationships, scope ?? new LandscapeDigestScope(LandscapeDigestBuilder.AllKinds, []),
            "d1", "src", 1, DateTimeOffset.UnixEpoch);

    private static string Edge(LandscapeDigestV2 digest, LandscapeDigestRelation edge) =>
        $"{digest.Items.Single(i => i.Ref == edge.From).Name} {edge.Kind} {digest.Items.Single(i => i.Ref == edge.To).Name}";

    [Fact]
    public void The_builder_emits_only_runsOn_and_suppliedBy_between_items_in_the_digest_and_each_fact_once()
    {
        var digest = Build();

        Assert.Equal(["Checkout RunsOn App server", "Checkout SuppliedBy Example Corp", "Internal tool RunsOn EU compute", "Internal tool SuppliedBy Example Corp"],
            digest.Relations!.Value.Select(e => Edge(digest, e)).Order());
        Assert.Empty(LandscapeDigestV2Validation.Validate(digest));
        Assert.True(digest.Scope.Relations);
        Assert.Equal("2", digest.ContractVersion);
    }

    [Fact]
    public void Refs_are_opaque_per_digest_and_layers_follow_the_kind()
    {
        var digest = Build();

        Assert.All(digest.Items, i => Assert.Matches("^c[0-9]+$", i.Ref));
        Assert.Equal(digest.Items.Length, digest.Items.Select(i => i.Ref).Distinct().Count());
        Assert.DoesNotContain(digest.Items, i => Assets.Any(a => a.Id == i.Ref));
        Assert.Equal(DigestLayer.Application, digest.Items.Single(i => i.Name == "Checkout").Layer);
        Assert.Equal(DigestLayer.Infrastructure, digest.Items.Single(i => i.Name == "App server").Layer);
        Assert.Null(digest.Items.Single(i => i.Kind == DigestKind.Vendor).Layer);
    }

    [Fact]
    public void An_edge_never_reveals_an_item_the_kind_or_tag_filter_removed()
    {
        var noPlatforms = Build(new LandscapeDigestScope([DigestKind.System, DigestKind.Application, DigestKind.Vendor], []));
        Assert.DoesNotContain(noPlatforms.Relations!.Value, e => e.Kind == DigestRelationKind.RunsOn);
        Assert.DoesNotContain(noPlatforms.Items, i => i.Kind == DigestKind.Platform);
        Assert.Empty(LandscapeDigestV2Validation.Validate(noPlatforms));

        var noVendors = Build(new LandscapeDigestScope([DigestKind.Application, DigestKind.Platform], []));
        Assert.DoesNotContain(noVendors.Relations!.Value, e => e.Kind == DigestRelationKind.SuppliedBy);

        var tagged = Build(new LandscapeDigestScope(LandscapeDigestBuilder.AllKinds, [new Tag("shared", "true")]),
            [Assets[1] with { Tags = [new Tag("shared", "true")] }, Assets[3]], Relationships);
        Assert.Empty(tagged.Relations!.Value.Where(e => e.Kind == DigestRelationKind.RunsOn));
        Assert.DoesNotContain(tagged.Items, i => i.Name == "App server");
    }

    [Fact]
    public void The_output_is_deterministic_so_the_signed_bytes_are_stable()
    {
        var a = JsonSerializer.Serialize(Build(), Json);
        var b = JsonSerializer.Serialize(Build(assets: [.. Assets.Reverse()], relationships: [.. Relationships.Reverse()]), Json);

        Assert.Equal(a, b);
    }

    // ---- The API: off by default, entitled and authorized when on ---------------------------------

    private static async Task SeedAsync(HttpClient client)
    {
        foreach (var asset in Assets) Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/assets", asset, Json)).StatusCode);
        foreach (var relationship in Relationships) Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/relationships", relationship, Json)).StatusCode);
    }

    private sealed class Host : IDisposable
    {
        private readonly AtlasApiFactory _root = new();
        public WebApplicationFactory<Program> Factory { get; }
        public ManualTime Time { get; } = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        public FakeConsumerClient Consumer { get; } = new();

        public Host(bool entitled)
        {
            Factory = _root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Atlas:Share:Push:PollSeconds", "3600");
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton<TimeProvider>(Time);
                    s.RemoveAll<IShareConsumerClient>();
                    s.AddSingleton<IShareConsumerClient>(Consumer);
                    // The base sharing permission is role-based; only the relations grant is an entitlement.
                    var granted = new HashSet<string>(StringComparer.Ordinal);
                    if (entitled) granted.Add(AtlasCapabilities.LandscapeShareRelations.Value);
                    var entitlements = new CommunityEntitlementService(granted);
                    s.RemoveAll<IEntitlementService>();
                    s.RemoveAll<IEntitlementAllowanceProvider>();
                    s.AddSingleton<IEntitlementService>(entitlements);
                    s.AddSingleton<IEntitlementAllowanceProvider>(entitlements);
                });
            });
        }

        public HttpClient Api(string tenant, string roles = "AtlasArchitect")
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
            client.DefaultRequestHeaders.Add("X-Principal-Id", "admin");
            client.DefaultRequestHeaders.Add("X-Principal-Roles", roles);
            return client;
        }

        public InMemoryAuditSink Audit => Factory.Services.GetRequiredService<InMemoryAuditSink>();

        public Task RunRoundAsync() => Factory.Services.GetServices<IHostedService>().OfType<LandscapePushBackgroundService>().Single().RunOnceAsync(CancellationToken.None);

        public void Dispose() { Factory.Dispose(); _root.Dispose(); }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeConsumerClient : IShareConsumerClient
    {
        public List<byte[]> Pushes { get; } = [];
        public int Activations { get; private set; }

        public Task<ConsumerActivationResult> ActivateAsync(string destinationUrl, string activationCode, string keyId, string publicKey, CancellationToken ct = default)
        {
            Activations++;
            return Task.FromResult(new ConsumerActivationResult(true, "enroll-1", "secret-credential-1", null, null, null));
        }

        public Task<PushResult> PushAsync(string destinationUrl, string credential, byte[] signedDigestJson, CancellationToken ct = default)
        {
            Pushes.Add(signedDigestJson);
            return Task.FromResult(new PushResult(PushOutcome.Accepted));
        }

        public Task RevokeAsync(string destinationUrl, string credential, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task<JsonNode> GetDigestAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/api/v1/share/digest" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static bool Verifies(JsonNode signed)
    {
        static byte[] FromBase64Url(string text) => Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=').Replace('-', '+').Replace('_', '/'));
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(FromBase64Url(signed["signature"]!["publicKey"]!.GetValue<string>()), out _);
        return rsa.VerifyData(Jcs.CanonicalizeUtf8(signed["digest"]!), FromBase64Url(signed["signature"]!["value"]!.GetValue<string>()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static JsonSchema DigestV2Schema()
    {
        var v1 = typeof(LandscapeRelationsShareTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AtlasContractsSchemas").Value!;
        var v2 = Path.Combine(Path.GetDirectoryName(v1.TrimEnd('/', '\\'))!, "v2");
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        JsonSchema? digest = null;
        foreach (var file in Directory.EnumerateFiles(v1, "*.json").Concat(Directory.EnumerateFiles(v2, "*.json")))
        {
            var schema = JsonSchema.FromText(File.ReadAllText(file), options);
            if (file.StartsWith(v2, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(file) == "landscape-digest.schema.json") digest = schema;
        }

        return digest!;
    }

    [Fact]
    public async Task Without_the_switch_the_digest_is_the_v1_digest_of_before()
    {
        using var host = new Host(entitled: true);
        var client = host.Api("t-rel-off");
        await SeedAsync(client);

        var signed = await GetDigestAsync(client);

        var digest = signed["digest"]!;
        Assert.Equal("1", digest["contractVersion"]!.GetValue<string>());
        Assert.Null(digest["relations"]);
        Assert.False(digest["scope"]!.AsObject().ContainsKey("relations"));
        Assert.All(digest["items"]!.AsArray(), i => { Assert.Null(i!["ref"]); Assert.Null(i["layer"]); });
        Assert.Contains(host.Audit.Events, e => e.Tenant.TenantId == "t-rel-off" && e.Action == "atlas.landscape.shared" && e.Resource.Value.Contains("&relations=false"));
    }

    [Fact]
    public async Task With_the_switch_and_the_grant_the_file_is_a_valid_signed_v2_digest_and_the_audit_holds_counts_only()
    {
        using var host = new Host(entitled: true);
        var client = host.Api("t-rel-on");
        await SeedAsync(client);

        var signed = await GetDigestAsync(client, "?relations=true");

        var digest = signed["digest"]!;
        var result = DigestV2Schema().Evaluate(JsonSerializer.SerializeToElement(digest), new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, string.Join("; ", (result.Details ?? []).Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
        Assert.Equal("2", digest["contractVersion"]!.GetValue<string>());
        Assert.True(digest["scope"]!["relations"]!.GetValue<bool>());
        Assert.Equal(4, digest["relations"]!.AsArray().Count);
        Assert.True(Verifies(signed));
        Assert.DoesNotContain("app-checkout", digest.ToJsonString());
        Assert.DoesNotContain("SECRET", digest.ToJsonString());
        Assert.Equal(["from", "kind", "to"], digest["relations"]![0]!.AsObject().Select(p => p.Key).Order());

        var audit = Assert.Single(host.Audit.Events, e => e.Tenant.TenantId == "t-rel-on" && e.Action == "atlas.landscape.shared");
        Assert.Contains("&relations=true&runsOn=2&suppliedBy=2", audit.Resource.Value);
        Assert.Equal("admin", audit.Actor.PrincipalId);
        Assert.DoesNotContain("Checkout", audit.Resource.Value);
    }

    [Fact]
    public async Task Relations_without_the_separate_grant_are_denied_with_a_reason_and_nothing_is_signed_or_audited()
    {
        using var host = new Host(entitled: false);
        var client = host.Api("t-rel-denied");
        await SeedAsync(client);

        var file = await client.GetAsync("/api/v1/share/digest?relations=true");
        var preview = await client.GetAsync("/api/v1/share/digest/preview?relations=true");

        Assert.Equal(HttpStatusCode.Forbidden, file.StatusCode);
        Assert.Contains(ReasonCodes.EntitlementDenied, await file.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.DoesNotContain(host.Audit.Events, e => e.Tenant.TenantId == "t-rel-denied" && e.Action == "atlas.landscape.shared");
        // The denied request did not use a sequence number, and the ordinary v1 sharing still works without the grant.
        Assert.Equal(1, (await GetDigestAsync(client))["digest"]!["sequence"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_read_only_customer_cannot_share_relations_even_with_the_grant()
    {
        using var host = new Host(entitled: true);
        var client = host.Api("t-rel-reader", "AtlasCustomer");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/share/digest?relations=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/share/digest/preview?relations=true")).StatusCode);
    }

    [Fact]
    public async Task The_preview_names_the_edges_the_consumer_would_see_without_a_sequence_number()
    {
        using var host = new Host(entitled: true);
        var client = host.Api("t-rel-preview");
        await SeedAsync(client);

        var preview = JsonNode.Parse(await client.GetStringAsync("/api/v1/share/digest/preview?relations=true"))!;

        Assert.True(preview["relationsIncluded"]!.GetValue<bool>());
        Assert.Equal((2, 2), (preview["runsOnCount"]!.GetValue<int>(), preview["suppliedByCount"]!.GetValue<int>()));
        Assert.Contains(preview["relations"]!.AsArray(), r => r!["from"]!.GetValue<string>() == "Checkout" && r["kind"]!.GetValue<string>() == "runsOn" && r["to"]!.GetValue<string>() == "App server");
        Assert.Contains(preview["shared"]!.AsArray().Select(s => s!.GetValue<string>()), s => s.Contains("runs on which platform"));
        Assert.Equal(1, (await GetDigestAsync(client))["digest"]!["sequence"]!.GetValue<int>());
    }

    // ---- Push -------------------------------------------------------------------------------

    [Fact]
    public async Task A_consumer_connected_with_relations_gets_v2_pushes_and_one_without_keeps_getting_v1()
    {
        using var host = new Host(entitled: true);
        var api = host.Api("t-rel-push");
        await SeedAsync(api);
        var response = await api.PostAsJsonAsync("/api/v1/share/consumers", new { name = "Partner", destinationUrl = Destination, activationCode = "AAAA-BBBB", shareRelations = true }, Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["shareRelations"]!.GetValue<bool>());

        await host.RunRoundAsync();

        var pushed = Assert.Single(host.Consumer.Pushes);
        var body = JsonNode.Parse(pushed)!;
        Assert.Equal("2", body["digest"]!["contractVersion"]!.GetValue<string>());
        Assert.Equal(4, body["digest"]!["relations"]!.AsArray().Count);
        Assert.True(Verifies(body));
        Assert.Contains(host.Audit.Events, e => e.Tenant.TenantId == "t-rel-push" && e.Resource.Value.StartsWith("atlas:landscape/share/push/") && e.Resource.Value.Contains("&relations=true&runsOn=2&suppliedBy=2"));

        // Switching it off is always allowed and the next push is v1 again.
        var off = await api.PutAsJsonAsync($"/api/v1/share/consumers/{JsonNode.Parse(await api.GetStringAsync("/api/v1/share/consumers"))![0]!["id"]!.GetValue<string>()}/scope", new { shareRelations = false }, Json);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        await host.RunRoundAsync();
        Assert.Equal("1", JsonNode.Parse(host.Consumer.Pushes.Last())!["digest"]!["contractVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Connecting_with_relations_needs_the_grant_and_activates_nothing_without_it()
    {
        using var host = new Host(entitled: false);
        var api = host.Api("t-rel-push-denied");

        var response = await api.PostAsJsonAsync("/api/v1/share/consumers", new { name = "Partner", destinationUrl = Destination, activationCode = "AAAA-BBBB", shareRelations = true }, Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Consumer.Activations);
        Assert.Equal("[]", (await api.GetStringAsync("/api/v1/share/consumers")).Replace(" ", ""));
    }
}
