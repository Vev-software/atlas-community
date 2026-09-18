using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

public sealed class DocumentStructureTests
{
    private static StructureDraftRequest Request(string type = "application/pdf", string? content = null) =>
        new("architecture notes", Documents: [new("private-file", type, content ?? Convert.ToBase64String([1, 2, 3]))]);

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("text/csv")]
    [InlineData("application/msword")]
    [InlineData("application/vnd.ms-excel")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task Documents_reach_capable_provider_and_require_explicit_import(string type)
    {
        using var factory = new AtlasApiFactory();
        var provider = new DocumentProvider();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IAiProviderExtension>(provider)));
        using var client = host.CreateClient();
        Identity(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/ai/module",
            new { enabled = true, consentAccepted = true, provider = "documents" })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/v1/structure/draft", Request(type));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await response.Content.ReadFromJsonAsync<StructureDraft>(AtlasContracts.SerializerOptions);
        Assert.Equal("available", draft!.Status);
        Assert.True(draft.ReviewRequired);
        Assert.Equal("structure-documents", provider.LastRequest.Purpose);
        Assert.Equal(type, Assert.Single(provider.LastRequest.Attachments!).ContentType);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/v1/assets")).GetArrayLength());
        var edited = draft.Proposal with { Assets = [draft.Proposal.Assets[0] with { Name = "Reviewed application" }] };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/import?format=atlas-json", edited, AtlasContracts.SerializerOptions)).StatusCode);
        Assert.Equal("Reviewed application", (await client.GetFromJsonAsync<JsonElement>("/api/v1/assets/app")).GetProperty("name").GetString());
        var audit = host.Services.GetRequiredService<InMemoryAuditSink>();
        Assert.Contains(audit.Events, e => e.Action == "atlas.ai.structure" && e.Resource.Value.Contains("documents=1"));
        Assert.DoesNotContain("private-file", JsonSerializer.Serialize(audit.Events));
    }

    [Fact]
    public async Task Mixed_images_and_documents_preserve_all_attachments()
    {
        using var factory = new AtlasApiFactory();
        var provider = new DocumentProvider();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IAiProviderExtension>(provider)));
        using var client = host.CreateClient(); Identity(client);
        await client.PutAsJsonAsync("/api/v1/ai/module", new { enabled = true, consentAccepted = true, provider = "documents" });
        var request = Request() with { Images = [new("image.png", "image/png", "AQID")] };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/structure/draft", request)).StatusCode);
        Assert.Equal(2, provider.LastRequest.Attachments!.Count);
        Assert.Equal("AQID", provider.LastRequest.Attachments[1].ContentBase64);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("text-only")]
    public async Task Missing_or_text_only_provider_degrades_and_uses_existing_allowance(string provider)
    {
        using var factory = new AtlasApiFactory();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IAiProviderExtension>(new TextOnlyProvider())));
        using var client = host.CreateClient(); Identity(client);
        if (provider != "none")
            await client.PutAsJsonAsync("/api/v1/ai/module", new { enabled = true, consentAccepted = true, provider, apiKey = "test-only" });
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/structure/draft", Request());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("ai_not_configured", (await response.Content.ReadFromJsonAsync<StructureDraft>(AtlasContracts.SerializerOptions))!.Status);
        }
        Assert.Equal(HttpStatusCode.PaymentRequired, (await client.PostAsJsonAsync("/api/v1/structure/draft", Request())).StatusCode);
    }

    [Fact]
    public async Task Invalid_attachments_are_rejected_without_consuming_allowance()
    {
        using var factory = new AtlasApiFactory();
        using var client = factory.CreateClient(); Identity(client);
        var full = Convert.ToBase64String(new byte[3 * StructureDraftService.MaxAttachmentBytes / 4]);
        var invalid = new[] { Request("application/zip"), Request(content: "not base64"), Request(content: ""),
            Request(content: Convert.ToBase64String(new byte[StructureDraftService.MaxAttachmentBytes + 1])),
            Request() with { Documents = Enumerable.Repeat(Request().Documents![0], 5).ToArray() },
            Request() with { Documents = Enumerable.Repeat(new StructureDraftDocument("large.pdf", "application/pdf", full), 3).ToArray() },
            Request() with { Documents = [null!] }, Request() with { Text = new string('x', 65537) } };
        foreach (var request in invalid)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/structure/draft", request)).StatusCode);
        Assert.DoesNotContain(factory.Services.GetRequiredService<InMemoryAuditSink>().Events, e => e.Action == "atlas.ai.structure");
    }

    [Fact]
    public async Task Documents_require_a_verified_principal()
    {
        using var host = new OidcTestHost();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/structure/draft", Request())).StatusCode);
    }

    private static void Identity(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "documents");
        client.DefaultRequestHeaders.Add("X-Principal-Id", "author");
        client.DefaultRequestHeaders.Add("X-Principal-Roles", "AtlasArchitect");
    }

    private sealed class DocumentProvider : IAiProviderExtension
    {
        public string ProviderId => "documents";
        public AiAssistRequest LastRequest { get; private set; }
        public bool SupportsAttachment(string contentType) => true;
        public AiAssistResult Assist(AiAssistRequest request)
        {
            LastRequest = request;
            return AiAssistResult.Available(JsonSerializer.Serialize(new ImportBundle(
                [new ImportAsset(AssetKind.Application, "Proposed application", Lifecycle.Draft, Id: "app")], [], ImportMode.Merge),
                AtlasContracts.SerializerOptions), "ai:documents");
        }
    }
    private sealed class TextOnlyProvider : IAiProviderExtension
    {
        public string ProviderId => "text-only";
        public AiAssistResult Assist(AiAssistRequest request) => throw new InvalidOperationException("Attachments must not reach a text-only provider.");
    }
}
