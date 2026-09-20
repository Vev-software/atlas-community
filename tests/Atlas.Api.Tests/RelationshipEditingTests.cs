using System.Net;
using System.Net.Http.Json;
using Vev.Atlas.Contracts;
using Xunit;

namespace Vev.Atlas.Api.Tests;

public sealed class RelationshipEditingTests(AtlasApiFactory factory) : IClassFixture<AtlasApiFactory>
{
    [Fact]
    public async Task Editing_preserves_identity_and_enforces_tenant_authorization_and_endpoints()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "relationship-edit");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");
        foreach (var id in new[] { "edit-a", "edit-b" })
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/assets",
                new Asset(id, AssetKind.Application, id, Lifecycle.Active))).StatusCode);
        var relationship = new Relationship("edit-r", "edit-a", "edit-b", RelationshipType.DependsOn, "original");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/relationships", relationship)).StatusCode);
        var updated = relationship with { Description = "changed" };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/relationships/edit-r", updated)).StatusCode);
        Assert.Contains("changed", await client.GetStringAsync("/api/v1/relationships"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/relationships/edit-r", updated with { ToId = "absent" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/relationships/other", updated)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Principal-Roles");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasCustomer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/relationships/edit-r", updated)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "another-tenant");
        Assert.DoesNotContain("edit-r", await client.GetStringAsync("/api/v1/relationships"));
    }
}
