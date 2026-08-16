using Vev.Atlas.Fabric;
using Vev.Fabric.Contracts.Entitlements;

namespace Vev.Atlas.Domain;

/// <summary>
/// Thrown when a Fabric decision (authorization or entitlement) denies an operation. Carries the reason
/// code and source so the API surfaces machine-readable denial context, never a bare 403.
/// </summary>
public sealed class AccessDeniedException(AccessDeniedDetails decision, string message)
    : Exception(message)
{
    public static AccessDeniedException FromAuthorization(Decision decision, string message) =>
        new(new AccessDeniedDetails(decision.ReasonCode, decision.Source), message);

    public static AccessDeniedException FromEntitlement(EntitlementDecision decision, string message) =>
        new(new AccessDeniedDetails(decision.ReasonCode, decision.Source), message);

    /// <summary>The denying decision, including reason code and source.</summary>
    public AccessDeniedDetails Decision { get; } = decision;
}

/// <summary>Minimal denial payload carried through the API boundary.</summary>
public sealed record AccessDeniedDetails(string ReasonCode, string Source);

/// <summary>Thrown when a catalogue invariant is violated (e.g. a relationship endpoint is missing).</summary>
public sealed class CatalogueValidationException(string message) : Exception(message);

/// <summary>Thrown when creating an entity whose id already exists in the tenant.</summary>
public sealed class CatalogueConflictException(string message) : Exception(message);
