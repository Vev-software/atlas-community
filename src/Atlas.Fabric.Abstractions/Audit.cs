namespace Vev.Atlas.Fabric;

/// <summary>
/// The Fabric audit envelope (fabric#6): a consistent, append-only record of a change. Carries the
/// actor, tenant, action, resource, time and a correlation id. The product supplies action/resource
/// <i>values</i>, not the schema (05 §3). <b>No secrets and no customer content</b> belong here (E4/E5).
/// </summary>
/// <param name="TenantId">The tenant the change happened in.</param>
/// <param name="ActorPrincipalId">The principal that made the change.</param>
/// <param name="Action">A stable action verb, e.g. <c>atlas.asset.created</c>.</param>
/// <param name="Resource">The resource affected, e.g. <c>atlas:asset/app-checkout</c>.</param>
/// <param name="OccurredAt">When the change occurred.</param>
/// <param name="CorrelationId">Ties product + substrate events for one request together.</param>
public sealed record AuditEvent(
    string TenantId,
    string ActorPrincipalId,
    string Action,
    string Resource,
    DateTimeOffset OccurredAt,
    string CorrelationId);

/// <summary>Append-only sink for <see cref="AuditEvent"/>s. Implemented by Fabric (dev shim for now).</summary>
public interface IAuditSink
{
    /// <summary>Record an audit event. Implementations must be append-only.</summary>
    ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>Read-only access to audit events for usage counting and similar bounded product mechanics.</summary>
public interface IAuditQueryService
{
    /// <summary>Return the matching events in the given time window.</summary>
    IReadOnlyCollection<AuditEvent> Query(
        string tenantId,
        string action,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive);
}
