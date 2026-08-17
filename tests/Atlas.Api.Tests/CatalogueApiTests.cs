using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>End-to-end tests for the Community catalogue: CRUD, relationships, tenant isolation, authz and the paid seam.</summary>
public sealed class CatalogueApiTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
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

    private static Asset SampleApp(string id = "app-checkout") =>
        new(id, AssetKind.Application, "Checkout", Lifecycle.Active,
            Tags: [new Tag("tier", "critical")],
            Application: new ApplicationDetails(Version: "1.0.0", Vendor: "in-house"));

    [Fact]
    public async Task Create_then_get_asset_round_trips_through_the_stack()
    {
        var client = Client(tenant: "t-roundtrip");

        var create = await client.PostAsJsonAsync("/api/v1/assets", SampleApp(), Json);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var fetched = await client.GetFromJsonAsync<Asset>("/api/v1/assets/app-checkout", Json);
        Assert.NotNull(fetched);
        Assert.Equal("Checkout", fetched!.Name);
        Assert.Equal(AssetKind.Application, fetched.Kind);
        Assert.Equal("1.0.0", fetched.Application?.Version);
        Assert.Contains(fetched.Tags, t => t is { Key: "tier", Value: "critical" });
    }

    [Fact]
    public async Task Asset_api_and_landscape_surface_the_stable_numeric_id()
    {
        var client = Client(tenant: "t-numeric-id");

        var created = await (await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app-numeric"), Json))
            .Content.ReadFromJsonAsync<JsonElement>();
        var numericId = created.GetProperty("numericId").GetInt64();
        Assert.True(numericId > 0);

        var fetched = await client.GetFromJsonAsync<JsonElement>("/api/v1/assets/app-numeric", Json);
        Assert.Equal(numericId, fetched.GetProperty("numericId").GetInt64());

        var landscape = await client.GetFromJsonAsync<JsonElement>("/api/v1/landscape", Json);
        var asset = landscape.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "app-numeric");
        Assert.Equal(numericId, asset.GetProperty("numericId").GetInt64());
    }

    [Fact]
    public async Task Listing_can_filter_by_kind()
    {
        var client = Client(tenant: "t-filter");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("a1"), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("s1", AssetKind.Server, "srv-01", Lifecycle.Active), Json);

        var servers = await client.GetFromJsonAsync<List<Asset>>("/api/v1/assets?kind=server", Json);

        Assert.NotNull(servers);
        Assert.Single(servers!);
        Assert.Equal(AssetKind.Server, servers![0].Kind);
    }

    [Fact]
    public async Task Data_layer_assets_and_join_keys_round_trip_through_the_stack()
    {
        var client = Client(tenant: "t-data-layer");

        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("sys-crm", AssetKind.System, "CRM platform", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("da-customers", AssetKind.DataArea, "Customer master data", Lifecycle.Active,
                DataArea: new DataAreaDetails(Realisation: "microservice")), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("ds-customers", AssetKind.Dataset, "Customers", Lifecycle.Active,
                Dataset: new DatasetDetails(PhysicalName: "dbo.customers", Owner: "CRM team")), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("col-customer-id", AssetKind.Column, "customer_id", Lifecycle.Active,
                Column: new ColumnDetails(DataType: "uuid", Nullable: false)), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("ds-invoices", AssetKind.Dataset, "Invoices", Lifecycle.Active,
                Dataset: new DatasetDetails(PhysicalName: "dbo.invoices", Owner: "Finance team")), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("col-invoice-customer-id", AssetKind.Column, "customer_id", Lifecycle.Active,
                Column: new ColumnDetails(DataType: "uuid", Nullable: false)), Json);

        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r-da", "da-customers", "sys-crm", RelationshipType.PartOf), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r-ds", "ds-customers", "da-customers", RelationshipType.PartOf), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r-col", "col-customer-id", "ds-customers", RelationshipType.PartOf), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r-col-2", "col-invoice-customer-id", "ds-invoices", RelationshipType.PartOf), Json);

        var join = await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r-key", "col-invoice-customer-id", "col-customer-id", RelationshipType.JoinsOn,
                "Invoice.customer_id joins Customer.customer_id"), Json);

        Assert.Equal(HttpStatusCode.Created, join.StatusCode);

        var datasets = await client.GetFromJsonAsync<List<Asset>>("/api/v1/assets?kind=dataset", Json);
        Assert.NotNull(datasets);
        Assert.Equal(2, datasets!.Count);
        Assert.All(datasets, asset => Assert.Equal(AssetKind.Dataset, asset.Kind));

        var landscape = await client.GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);
        Assert.NotNull(landscape);
        Assert.Contains(landscape!.Assets, a => a.Id == "da-customers" && a.DataArea?.Realisation == "microservice");
        Assert.Contains(landscape.Assets, a => a.Id == "ds-customers" && a.Dataset?.PhysicalName == "dbo.customers");
        Assert.Contains(landscape.Assets, a => a.Id == "col-customer-id" && a.Column?.Nullable == false);
        Assert.Contains(landscape.Relationships, r => r.Type == RelationshipType.JoinsOn && r.FromId == "col-invoice-customer-id" && r.ToId == "col-customer-id");
    }

    [Fact]
    public async Task Manual_relationship_requires_both_endpoints_to_exist()
    {
        var client = Client(tenant: "t-rel");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app"), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv", AssetKind.Server, "srv", Lifecycle.Active), Json);

        var ok = await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "srv", RelationshipType.RunsOn), Json);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var missing = await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r2", "app", "ghost", RelationshipType.RunsOn), Json);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_asset_removes_its_relationships()
    {
        var client = Client(tenant: "t-cascade");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app"), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv", AssetKind.Server, "srv-01", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "srv", RelationshipType.RunsOn), Json);

        var deleted = await client.DeleteAsync("/api/v1/assets/srv");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var landscape = await client.GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);
        Assert.NotNull(landscape);
        Assert.DoesNotContain(landscape!.Assets, a => a.Id == "srv");
        Assert.Empty(landscape.Relationships);

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e =>
            e.Action == "atlas.relationship.deleted" &&
            e.Tenant.TenantId == "t-cascade" &&
            e.Resource.Value == "atlas:relationship/r1");
    }

    [Fact]
    public async Task Assets_are_isolated_by_tenant()
    {
        await Client(tenant: "tenant-a").PostAsJsonAsync("/api/v1/assets", SampleApp("shared-id"), Json);

        var otherTenant = await Client(tenant: "tenant-b").GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json);

        Assert.NotNull(otherTenant);
        Assert.Empty(otherTenant!);
    }

    [Fact]
    public async Task A_read_only_customer_cannot_write()
    {
        var readOnly = Client(tenant: "t-authz", principal: "viewer", roles: "AtlasCustomer");

        var response = await readOnly.PostAsJsonAsync("/api/v1/assets", SampleApp("nope"), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("role_missing", problem.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Paid_capability_is_entitlement_denied_in_community()
    {
        var client = Client(tenant: "t-paid");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app-paid"), Json);

        var response = await client.GetAsync("/api/v1/assets/app-paid/integration-mapping");

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entitlement_denied", body.GetProperty("reasonCode").GetString());
        Assert.Equal("atlas.integration.mapping", body.GetProperty("capability").GetString());
    }

    [Fact]
    public async Task Landscape_read_composes_assets_and_relationships_for_the_tenant()
    {
        var client = Client(tenant: "t-landscape");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app"), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv", AssetKind.Server, "srv-01", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "srv", RelationshipType.RunsOn), Json);

        var landscape = await client.GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);

        Assert.NotNull(landscape);
        Assert.Equal(2, landscape!.Assets.Length);
        Assert.Single(landscape.Relationships);
        Assert.Equal("app", landscape.Relationships[0].FromId);
        Assert.NotNull(landscape.ExportedAt);
    }

    [Fact]
    public async Task Landscape_read_is_isolated_by_tenant()
    {
        await Client(tenant: "t-ls-a").PostAsJsonAsync("/api/v1/assets", SampleApp("only-a"), Json);

        var other = await Client(tenant: "t-ls-b").GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);

        Assert.NotNull(other);
        Assert.Empty(other!.Assets);
    }

    [Fact]
    public async Task Landscape_visualisation_page_is_served_at_the_root()
    {
        var response = await factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/landscape", body);
        Assert.Contains("Search columns or datasets", body);
    }

    [Fact]
    public async Task A_read_only_customer_can_browse_the_landscape()
    {
        var author = Client(tenant: "t-read-browse");
        await author.PostAsJsonAsync("/api/v1/assets",
            new Asset("sys-crm", AssetKind.System, "CRM", Lifecycle.Active), Json);
        await author.PostAsJsonAsync("/api/v1/assets",
            new Asset("da-customer", AssetKind.DataArea, "Customer data", Lifecycle.Active,
                DataArea: new DataAreaDetails("microservice")), Json);
        await author.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r-part-of", "da-customer", "sys-crm", RelationshipType.PartOf), Json);

        var readOnly = Client(tenant: "t-read-browse", principal: "viewer", roles: "AtlasCustomer");
        var response = await readOnly.GetAsync("/api/v1/landscape");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var landscape = await response.Content.ReadFromJsonAsync<LandscapeDocument>(Json);
        Assert.NotNull(landscape);
        Assert.Equal(2, landscape!.Assets.Length);
        Assert.Single(landscape.Relationships);
    }

    [Fact]
    public async Task Capabilities_report_author_for_an_architect()
    {
        var caps = await Client(roles: "AtlasArchitect")
            .GetFromJsonAsync<JsonElement>("/api/v1/capabilities", Json);

        Assert.True(caps.GetProperty("canAuthor").GetBoolean());
    }

    [Fact]
    public async Task Capabilities_report_read_only_for_a_customer()
    {
        var caps = await Client(principal: "viewer", roles: "AtlasCustomer")
            .GetFromJsonAsync<JsonElement>("/api/v1/capabilities", Json);

        Assert.False(caps.GetProperty("canAuthor").GetBoolean());
    }

    [Fact]
    public async Task Mutations_emit_audit_events()
    {
        var client = Client(tenant: "t-audit");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app-audit"), Json);

        var audit = factory.Services.GetRequiredService<InMemoryAuditSink>();

        Assert.Contains(audit.Events, e =>
            e.Action == "atlas.asset.created" &&
            e.Tenant.TenantId == "t-audit" &&
            e.Resource.Value == "atlas:asset/app-audit");
    }

    [Fact]
    public async Task Asset_history_endpoint_returns_created_by_created_at_and_last_updated()
    {
        var asset = SampleApp("app-history");
        await Client(tenant: "t-history", principal: "alice")
            .PostAsJsonAsync("/api/v1/assets", asset, Json);

        var updated = asset with { Name = "Checkout v2" };
        await Client(tenant: "t-history", principal: "bob")
            .PutAsJsonAsync("/api/v1/assets/app-history", updated, Json);

        var response = await Client(tenant: "t-history", principal: "viewer", roles: "AtlasCustomer")
            .GetAsync("/api/v1/assets/app-history/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("alice", body.GetProperty("createdBy").GetString());
        Assert.True(body.GetProperty("createdAt").GetDateTimeOffset() <= body.GetProperty("lastUpdatedAt").GetDateTimeOffset());

        var entries = body.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal("Asset details updated", entries[0].GetProperty("summary").GetString());
        Assert.Equal("bob", entries[0].GetProperty("actor").GetString());
        Assert.Equal("Asset created", entries[1].GetProperty("summary").GetString());
        Assert.Equal("alice", entries[1].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Asset_history_endpoint_is_not_found_for_a_missing_asset()
    {
        var response = await Client(tenant: "t-history-missing", principal: "viewer", roles: "AtlasCustomer")
            .GetAsync("/api/v1/assets/ghost/history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Portability surface (issue #12): customer-owned export + import ---

    [Fact]
    public async Task Export_downloads_the_landscape_as_a_schema_valid_contract_document()
    {
        var client = Client(tenant: "t-export");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("app"), Json);
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv", AssetKind.Server, "srv-01", Lifecycle.Active), Json);
        await client.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "srv", RelationshipType.RunsOn), Json);

        var response = await client.GetAsync("/api/v1/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        // Customer-owned export: an attachment the user downloads and keeps.
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.Equal("attachment", disposition?.DispositionType);
        Assert.Equal("atlas-landscape.json", disposition?.FileNameStar ?? disposition?.FileName);

        // The bytes must round-trip through the published contract type with its canonical serializer:
        // that is schema conformance in practice — the wire shape the schemas describe.
        var raw = await response.Content.ReadAsStringAsync();
        var document = JsonSerializer.Deserialize<LandscapeDocument>(raw, AtlasContracts.SerializerOptions);
        Assert.NotNull(document);
        Assert.Equal(2, document!.Assets.Length);
        Assert.Single(document.Relationships);
        // Versioned compatibility: the document declares the contract major version and its provenance.
        Assert.Contains("\"contractVersion\":\"1\"", raw);
        Assert.Equal("Atlas Community", document.Generator?.Name);
    }

    [Fact]
    public async Task Import_merge_upserts_assets_and_relationships()
    {
        var client = Client(tenant: "t-import-merge");
        // An asset already in the catalogue, to prove merge updates rather than duplicates.
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app", AssetKind.Application, "Old name", Lifecycle.Active), Json);

        var bundle = new ImportBundle(
            Assets:
            [
                new ImportAsset(AssetKind.Application, "New name", Lifecycle.Active, Id: "app"),
                new ImportAsset(AssetKind.Server, "srv-01", Lifecycle.Active, ExternalId: "srv"),
            ],
            Relationships:
            [
                new ImportRelationship("app", "srv", RelationshipType.RunsOn, Id: "r1"),
            ],
            Mode: ImportMode.Merge);

        var response = await client.PostAsJsonAsync("/api/v1/import", bundle, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("merge", result.GetProperty("mode").GetString());
        Assert.Equal(1, result.GetProperty("assetsCreated").GetInt32());
        Assert.Equal(1, result.GetProperty("assetsUpdated").GetInt32());
        Assert.Equal(0, result.GetProperty("assetsDeleted").GetInt32());
        Assert.Equal(1, result.GetProperty("relationshipsImported").GetInt32());

        // The existing asset was replaced in place; the externalId asset was created under that id.
        var updated = await client.GetFromJsonAsync<Asset>("/api/v1/assets/app", Json);
        Assert.Equal("New name", updated!.Name);
        var created = await client.GetFromJsonAsync<Asset>("/api/v1/assets/srv", Json);
        Assert.Equal(AssetKind.Server, created!.Kind);
    }

    [Fact]
    public async Task Import_is_idempotent_on_external_id()
    {
        var client = Client(tenant: "t-import-idem");
        var bundle = new ImportBundle(
            Assets: [new ImportAsset(AssetKind.Server, "srv-01", Lifecycle.Active, ExternalId: "srv")],
            Mode: ImportMode.Merge);

        var first = await (await client.PostAsJsonAsync("/api/v1/import", bundle, Json))
            .Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await client.PostAsJsonAsync("/api/v1/import", bundle, Json))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, first.GetProperty("assetsCreated").GetInt32());
        // Re-importing the same externalId updates the same asset — no duplicate.
        Assert.Equal(0, second.GetProperty("assetsCreated").GetInt32());
        Assert.Equal(1, second.GetProperty("assetsUpdated").GetInt32());

        var all = await client.GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json);
        Assert.Single(all!);
    }

    [Fact]
    public async Task Import_replace_makes_the_catalogue_match_the_bundle()
    {
        var client = Client(tenant: "t-import-replace");
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("stale"), Json);
        await client.PostAsJsonAsync("/api/v1/assets", SampleApp("keep"), Json);

        var bundle = new ImportBundle(
            Assets: [new ImportAsset(AssetKind.Application, "Kept", Lifecycle.Active, Id: "keep")],
            Mode: ImportMode.Replace);

        var result = await (await client.PostAsJsonAsync("/api/v1/import", bundle, Json))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("replace", result.GetProperty("mode").GetString());
        Assert.Equal(1, result.GetProperty("assetsDeleted").GetInt32());

        var all = await client.GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json);
        Assert.Single(all!);
        Assert.Equal("keep", all![0].Id);
    }

    [Fact]
    public async Task Import_rejects_an_unresolved_relationship_reference()
    {
        var client = Client(tenant: "t-import-bad");
        var bundle = new ImportBundle(
            Assets: [new ImportAsset(AssetKind.Application, "App", Lifecycle.Active, Id: "app")],
            Relationships: [new ImportRelationship("app", "ghost", RelationshipType.RunsOn)],
            Mode: ImportMode.Merge);

        var response = await client.PostAsJsonAsync("/api/v1/import", bundle, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Nothing is written when validation fails: the app asset must not have been created.
        var all = await client.GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json);
        Assert.Empty(all!);
    }

    [Fact]
    public async Task Export_then_import_round_trips_into_a_second_tenant()
    {
        var source = Client(tenant: "t-rt-source");
        await source.PostAsJsonAsync("/api/v1/assets", SampleApp("app"), Json);
        await source.PostAsJsonAsync("/api/v1/assets",
            new Asset("srv", AssetKind.Server, "srv-01", Lifecycle.Active), Json);
        await source.PostAsJsonAsync("/api/v1/relationships",
            new Relationship("r1", "app", "srv", RelationshipType.RunsOn), Json);

        // Export the source landscape in the published contract form...
        var exported = await source.GetFromJsonAsync<LandscapeDocument>("/api/v1/export", Json);

        // ...turn it into an import bundle (what a portability client does) and import into a fresh tenant.
        var bundle = new ImportBundle(
            Assets: [.. exported!.Assets.Select(a =>
                new ImportAsset(a.Kind, a.Name, a.Lifecycle, Id: a.Id, Description: a.Description,
                    Tags: a.Tags, Application: a.Application, Server: a.Server, Infrastructure: a.Infrastructure))],
            Relationships: [.. exported.Relationships.Select(r =>
                new ImportRelationship(r.FromId, r.ToId, r.Type, Id: r.Id, Description: r.Description))],
            Mode: ImportMode.Merge);

        var target = Client(tenant: "t-rt-target");
        var import = await target.PostAsJsonAsync("/api/v1/import", bundle, Json);
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        var landscape = await target.GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);
        Assert.Equal(2, landscape!.Assets.Length);
        Assert.Single(landscape.Relationships);
        Assert.Contains(landscape.Assets, a => a.Id == "app" && a.Name == "Checkout");
        Assert.Equal("app", landscape.Relationships[0].FromId);
    }

    [Fact]
    public async Task Import_is_isolated_by_tenant()
    {
        var bundle = new ImportBundle(
            Assets: [new ImportAsset(AssetKind.Application, "Only A", Lifecycle.Active, Id: "only-a")],
            Mode: ImportMode.Merge);
        await Client(tenant: "t-imp-a").PostAsJsonAsync("/api/v1/import", bundle, Json);

        var other = await Client(tenant: "t-imp-b").GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json);
        Assert.Empty(other!);
    }

    [Fact]
    public async Task A_read_only_customer_cannot_import()
    {
        var readOnly = Client(tenant: "t-imp-authz", principal: "viewer", roles: "AtlasCustomer");
        var bundle = new ImportBundle(
            Assets: [new ImportAsset(AssetKind.Application, "Nope", Lifecycle.Active, Id: "nope")],
            Mode: ImportMode.Merge);

        var response = await readOnly.PostAsJsonAsync("/api/v1/import", bundle, Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_export_format_is_a_bad_request()
    {
        var response = await Client(tenant: "t-fmt").GetAsync("/api/v1/export?format=archimate");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_relationship_missing_an_endpoint_is_a_bad_request_not_a_500()
    {
        var client = Client(tenant: "t-imp-nullref");
        // Structurally valid JSON, but the relationship omits toRef — must be a 400, never an unhandled 500.
        var body = new StringContent(
            """
            {"kind":"import","mode":"merge",
             "assets":[{"id":"app","kind":"application","name":"App","lifecycle":"active"}],
             "relationships":[{"fromRef":"app","type":"runs-on"}]}
            """,
            System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/v1/import", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json))!);
    }

    // --- Per-asset edit authorization (atlas#76): creator can edit even without write role ---

    [Fact]
    public async Task Creator_can_edit_own_asset_without_write_role()
    {
        // Create asset as architect (has write role)
        var architect = Client(tenant: "t-creator-edit", principal: "alice", roles: "AtlasArchitect");
        await architect.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-alice", AssetKind.Application, "Alice App", Lifecycle.Active), Json);

        // Now alice as a read-only customer can still edit because she's the creator
        var aliceReader = Client(tenant: "t-creator-edit", principal: "alice", roles: "AtlasCustomer");
        var updated = new Asset("app-alice", AssetKind.Application, "Alice App v2", Lifecycle.Active, Description: "Updated by creator");
        var put = await aliceReader.PutAsJsonAsync("/api/v1/assets/app-alice", updated, Json);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var result = await put.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Alice App v2", result.GetProperty("name").GetString());
        Assert.Equal("alice", result.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task Non_creator_without_write_role_cannot_edit()
    {
        // Create asset as architect (has write role)
        var architect = Client(tenant: "t-non-creator", principal: "alice", roles: "AtlasArchitect");
        await architect.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-alice-2", AssetKind.Application, "Alice App", Lifecycle.Active), Json);

        // Bob is not the creator and has no write role — denied
        var bob = Client(tenant: "t-non-creator", principal: "bob", roles: "AtlasCustomer");
        var updated = new Asset("app-alice-2", AssetKind.Application, "Bob Edit", Lifecycle.Active);
        var response = await bob.PutAsJsonAsync("/api/v1/assets/app-alice-2", updated, Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("role_missing", problem.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Architect_can_edit_any_asset_regardless_of_creator()
    {
        // Create asset as alice (read-only customer) — but needs architect to create first
        var creator = Client(tenant: "t-arch-edit", principal: "alice", roles: "AtlasArchitect");
        await creator.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-arch", AssetKind.Application, "Alice App", Lifecycle.Active), Json);

        // Bob is an architect — can edit alice's asset
        var bob = Client(tenant: "t-arch-edit", principal: "bob", roles: "AtlasArchitect");
        var updated = new Asset("app-arch", AssetKind.Application, "Bob Edit", Lifecycle.Active);
        var response = await bob.PutAsJsonAsync("/api/v1/assets/app-arch", updated, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Asset_payload_includes_created_by()
    {
        var client = Client(tenant: "t-created-by", principal: "creator-user");
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-created-by", AssetKind.Application, "Test", Lifecycle.Active), Json);

        var response = await client.GetFromJsonAsync<JsonElement>("/api/v1/assets/app-created-by", Json);
        Assert.Equal("creator-user", response.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task Asset_identifier_remains_immutable_on_update()
    {
        var client = Client(tenant: "t-immutable-id", principal: "alice");
        await client.PostAsJsonAsync("/api/v1/assets",
            new Asset("app-original", AssetKind.Application, "Original", Lifecycle.Active), Json);

        var updated = new Asset("app-renamed", AssetKind.Application, "Renamed", Lifecycle.Active);
        var response = await client.PutAsJsonAsync("/api/v1/assets/app-original", updated, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
