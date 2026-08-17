using System.Text.Json;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// The audit trail carries no secrets or customer content by default (fabric#6, E4/E5). Atlas emits
/// through the Fabric envelope, and the sink enforces the Fabric redaction guard on write.
/// </summary>
public sealed class AuditRedactionTests
{
    [Fact]
    public async Task Sink_rejects_an_event_whose_metadata_looks_like_a_secret()
    {
        var sink = new InMemoryAuditSink();
        var poisoned = Event(new Dictionary<string, string> { ["apiKey"] = "sk-123" });

        var exception = await Assert.ThrowsAsync<AuditRedactionException>(
            async () => await sink.WriteAsync(poisoned));

        Assert.Equal("apiKey", exception.OffendingKey);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task Sink_records_an_event_with_redaction_safe_metadata()
    {
        var sink = new InMemoryAuditSink();
        await sink.WriteAsync(Event(new Dictionary<string, string> { ["changeKind"] = "update" }));

        Assert.Single(sink.Events);
    }

    [Fact]
    public void Actor_projection_carries_no_principal_claims()
    {
        // The envelope's actor is a claims-free projection: email and other provider claims on the
        // principal never reach the audit trail.
        var principal = new PrincipalContext(
            "u-1",
            "Architect",
            ["AtlasArchitect"],
            new Dictionary<string, string> { ["email"] = "architect@example.test" });

        var actor = AuditActor.FromPrincipal(principal);

        Assert.Equal("u-1", actor.PrincipalId);
        Assert.Equal(["AtlasArchitect"], actor.Roles);

        var json = JsonSerializer.Serialize(actor, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("architect@example.test", json);
        Assert.DoesNotContain("claims", json);
    }

    private static AuditEvent Event(IReadOnlyDictionary<string, string> metadata) =>
        new(
            EventId: Guid.NewGuid().ToString("N"),
            OccurredAt: TimeProvider.System.GetUtcNow(),
            Tenant: new TenantContext("t-redaction"),
            Actor: new AuditActor("u-1"),
            Source: "atlas",
            Action: "atlas.asset.created",
            Resource: new AuditResource("atlas:asset/app-1"),
            Category: AuditCategory.Data,
            Outcome: AuditOutcome.Success,
            CorrelationId: "corr-1",
            Metadata: metadata);
}
