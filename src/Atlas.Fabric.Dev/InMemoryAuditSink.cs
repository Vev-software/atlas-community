using System.Collections.Concurrent;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Dev implementation of the Fabric <see cref="IAuditSink"/>: append-only, in memory. Real Fabric
/// persists these into the append-only audit store. Kept queryable so tests can assert emission.
/// </summary>
public sealed class InMemoryAuditSink : IAuditSink
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    /// <inheritdoc />
    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(auditEvent);
        return ValueTask.CompletedTask;
    }

    /// <summary>The events recorded so far, in order.</summary>
    public IReadOnlyCollection<AuditEvent> Events => _events.ToArray();
}
