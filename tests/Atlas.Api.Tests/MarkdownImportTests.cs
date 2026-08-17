using System.Net;
using System.Text;
using System.Text.Json;
using Vev.Atlas.Contracts;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>End-to-end tests for the Markdown landscape import (issue #92).</summary>
public sealed class MarkdownImportTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
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
    public async Task Markdown_import_parses_assets_and_relationships()
    {
        var client = Client(tenant: "t-md-import");

        var md = @"
## Systems

- sys-crm | CRM Platform | active
  description: Customer relationship management

## Applications

- app-checkout | Checkout Service | active
  description: Handles payment processing
  version: 2.1.0
  vendor: in-house
  businessOwner: CTO

## Servers

- srv-prod-01 | Production Server | active
  hostname: prod01.internal
  environment: production
  os: Ubuntu 22.04

## Relationships

- app-checkout runs-on srv-prod-01
- sys-crm part-of sys-crm description: Self-reference

## Mode

merge
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await client.PostAsync("/api/v1/import?format=atlas-md", content);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var errBody = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Import failed with {response.StatusCode}: {errBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("merge", result.GetProperty("mode").GetString());
        Assert.Equal(3, result.GetProperty("assetsCreated").GetInt32());
        Assert.Equal(2, result.GetProperty("relationshipsImported").GetInt32());

        var landscape = await client.GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);
        Assert.NotNull(landscape);
        Assert.Equal(3, landscape!.Assets.Length);
        Assert.Equal(2, landscape.Relationships.Length);
        Assert.Contains(landscape.Assets, a => a.Id == "sys-crm" && a.Name == "CRM Platform");
        Assert.Contains(landscape.Assets, a => a.Id == "app-checkout" && a.Application?.Version == "2.1.0");
        Assert.Contains(landscape.Assets, a => a.Id == "srv-prod-01" && a.Server?.Hostname == "prod01.internal");
    }

    [Fact]
    public async Task Markdown_import_handles_data_layer_assets()
    {
        var client = Client(tenant: "t-md-data-layer");

        var md = @"
## Data Areas

- da-customers | Customer Master Data | active
  realisation: microservice

## Datasets

- ds-customers | Customers | active
  physical_name: dbo.customers
  owner: CRM team

## Columns

- col-customer-id | customer_id | active
  data_type: uuid
  nullable: false

## Relationships

- ds-customers part-of da-customers
- col-customer-id part-of ds-customers

## Mode

merge
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await client.PostAsync("/api/v1/import?format=atlas-md", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, result.GetProperty("assetsCreated").GetInt32());
        Assert.Equal(2, result.GetProperty("relationshipsImported").GetInt32());

        var landscape = await client.GetFromJsonAsync<LandscapeDocument>("/api/v1/landscape", Json);
        Assert.NotNull(landscape);
        Assert.Contains(landscape!.Assets, a => a.Id == "da-customers" && a.DataArea?.Realisation == "microservice");
        Assert.Contains(landscape.Assets, a => a.Id == "ds-customers" && a.Dataset?.PhysicalName == "dbo.customers");
        Assert.Contains(landscape.Assets, a => a.Id == "col-customer-id" && a.Column?.DataType == "uuid" && a.Column?.Nullable == false);
    }

    [Fact]
    public async Task Markdown_import_replace_mode_deletes_extra_assets()
    {
        var client = Client(tenant: "t-md-replace");

        var md = @"
## Systems

- sys-only | Only This | active

## Mode

replace
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await client.PostAsync("/api/v1/import?format=atlas-md", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("replace", result.GetProperty("mode").GetString());
        Assert.Equal(1, result.GetProperty("assetsCreated").GetInt32());

        var all = await client.GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json);
        Assert.Single(all!);
        Assert.Equal("sys-only", all![0].Id);
    }

    [Fact]
    public async Task Markdown_import_malformed_input_returns_400()
    {
        var client = Client(tenant: "t-md-malformed");

        var md = @"
## Systems

- missing-pipes | No pipes here

## Mode

merge
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await client.PostAsync("/api/v1/import?format=atlas-md", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var all = await client.GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json);
        Assert.Empty(all!);
    }

    [Fact]
    public async Task Markdown_import_unknown_lifecycle_returns_400()
    {
        var client = Client(tenant: "t-md-bad-lifecycle");

        var md = @"
## Systems

- sys-bad | Bad Lifecycle | zombie

## Mode

merge
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await client.PostAsync("/api/v1/import?format=atlas-md", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Markdown_import_unknown_relationship_type_returns_400()
    {
        var client = Client(tenant: "t-md-bad-rel");

        var md = @"
## Systems

- sys-a | System A | active
- sys-b | System B | active

## Relationships

- sys-a telepathic sys-b

## Mode

merge
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await client.PostAsync("/api/v1/import?format=atlas-md", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Markdown_import_unknown_mode_returns_400()
    {
        var client = Client(tenant: "t-md-bad-mode");

        var md = @"
## Systems

- sys-a | System A | active

## Mode

destroy
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await client.PostAsync("/api/v1/import?format=atlas-md", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Markdown_import_tags_are_parsed()
    {
        var client = Client(tenant: "t-md-tags");

        var md = @"
## Applications

- app-tagged | Tagged App | active
  tag: tier: critical
  tag: env: production

## Mode

merge
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await client.PostAsync("/api/v1/import?format=atlas-md", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var asset = await client.GetFromJsonAsync<Asset>("/api/v1/assets/app-tagged", Json);
        Assert.NotNull(asset);
        Assert.Contains(asset!.Tags, t => t is { Key: "tier", Value: "critical" });
        Assert.Contains(asset.Tags, t => t is { Key: "env", Value: "production" });
    }

    [Fact]
    public async Task Markdown_import_with_infrastructure_and_tags()
    {
        var client = Client(tenant: "t-md-infra");

        var md = @"
## Infrastructure

- net-vpc | AWS VPC | active
  category: network
  location: eu-west-1

## Mode

merge
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await client.PostAsync("/api/v1/import?format=atlas-md", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var asset = await client.GetFromJsonAsync<Asset>("/api/v1/assets/net-vpc", Json);
        Assert.NotNull(asset);
        Assert.Equal(AssetKind.Infrastructure, asset!.Kind);
        Assert.Equal("network", asset.Infrastructure?.Category);
        Assert.Equal("eu-west-1", asset.Infrastructure?.Location);
    }

    [Fact]
    public async Task A_read_only_customer_cannot_import_markdown()
    {
        var readOnly = Client(tenant: "t-md-authz", principal: "viewer", roles: "AtlasCustomer");

        var md = @"
## Systems

- sys-a | System A | active

## Mode

merge
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        var response = await readOnly.PostAsync("/api/v1/import?format=atlas-md", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Markdown_import_is_isolated_by_tenant()
    {
        var md = @"
## Systems

- sys-only | Only This | active

## Mode

merge
";

        var content = new StringContent(md, Encoding.UTF8, "text/markdown");
        await Client(tenant: "t-md-iso-a").PostAsync("/api/v1/import?format=atlas-md", content);

        var other = await Client(tenant: "t-md-iso-b").GetFromJsonAsync<List<Asset>>("/api/v1/assets", Json);
        Assert.Empty(other!);
    }
}