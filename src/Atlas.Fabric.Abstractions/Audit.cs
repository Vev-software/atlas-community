namespace Vev.Atlas.Fabric;

/// <summary>
/// Atlas's product-side audit sink over the published Fabric audit envelope
/// (<see cref="AuditEvent"/>, fabric#6). Fabric owns the envelope; Atlas owns the append-only,
/// async, queryable plumbing the product needs (a real store is I/O-bound, and the AI allowance is
/// metered off the trail). Implemented by Fabric in production; a dev shim in the Community build.
/// </summary>
public interface IAtlasAuditSink
{
    /// <summary>Record an audit event. Implementations must be append-only and reject unsafe payloads.</summary>
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

/// <summary>
/// Builds the Fabric <see cref="AuditEvent"/> envelope with Atlas's cross-cutting defaults — the
/// emitting <see cref="Source"/>, a generated event id, the redaction-safe actor projection
/// (<see cref="AuditActor.FromPrincipal"/> drops principal claims) and the request correlation id —
/// so call sites supply only the product-meaningful action and resource, plus category and outcome
/// when it is not an ordinary successful data change. This keeps every emission consistent in
/// exactly the places (source, redaction, correlation) where drift would be a security or ops risk.
/// </summary>
public static class AtlasAudit
{
    /// <summary>The emitting component recorded on every Atlas audit event.</summary>
    public const string Source = "atlas";

    /// <summary>Build an audit event for the current request context.</summary>
    /// <param name="context">The bound tenant, principal and correlation id.</param>
    /// <param name="clock">Time source for <c>OccurredAt</c>.</param>
    /// <param name="action">Product-supplied action value, e.g. <c>atlas.asset.created</c>.</param>
    /// <param name="resource">Product-supplied resource identifier, e.g. <c>atlas:asset/app-checkout</c>.</param>
    /// <param name="category">Data (default) for ordinary asset changes; Admin/Security for governed events.</param>
    /// <param name="outcome">Success (default), Failure, or Denied.</param>
    public static AuditEvent Event(
        IRequestContextAccessor context,
        TimeProvider clock,
        string action,
        string resource,
        AuditCategory category = AuditCategory.Data,
        AuditOutcome outcome = AuditOutcome.Success) =>
        new(
            EventId: Guid.NewGuid().ToString("N"),
            OccurredAt: clock.GetUtcNow(),
            Tenant: context.Tenant,
            Actor: AuditActor.FromPrincipal(context.Principal),
            Source: Source,
            Action: action,
            Resource: new AuditResource(resource),
            Category: category,
            Outcome: outcome,
            CorrelationId: context.CorrelationId);
}
