using System.Collections.Concurrent;
using Vev.Atlas.Fabric;
using Vev.Fabric.Contracts.Audit;

namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Dev implementation of the Atlas <see cref="IAtlasAuditSink"/>: append-only, in memory, over the
/// published Fabric <see cref="AuditEvent"/> envelope. Real Fabric persists these into the
/// append-only audit store. Enforces the Fabric redaction guard on write and stays queryable so
/// tests can assert emission.
/// </summary>
public sealed class InMemoryAuditSink : IAtlasAuditSink, IAuditQueryService
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    /// <inheritdoc />
    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // Redaction is baked into the contract: refuse a payload whose metadata looks like it
        // carries a secret or customer content (fabric#6, E4/E5) rather than persist it.
        if (!AuditRedaction.IsRedactionSafe(auditEvent.Metadata, out var offendingKey))
        {
            throw new AuditRedactionException(offendingKey!);
        }

        _events.Enqueue(auditEvent);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AuditEvent> Query(
        string tenantId,
        string action,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive) =>
        _events
            .Where(e =>
                string.Equals(e.Tenant.TenantId, tenantId, StringComparison.Ordinal) &&
                string.Equals(e.Action, action, StringComparison.Ordinal) &&
                e.OccurredAt >= fromInclusive &&
                e.OccurredAt < toExclusive)
            .ToArray();

    /// <summary>The events recorded so far, in order.</summary>
    public IReadOnlyCollection<AuditEvent> Events => _events.ToArray();
}
