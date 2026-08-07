using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain;

/// <summary>
/// Thrown when a Fabric decision (authorization or entitlement) denies an operation. Carries the
/// <see cref="Decision"/> so the API surfaces the machine-readable reason code, never a bare 403.
/// </summary>
public sealed class AccessDeniedException(Decision decision, string message)
    : Exception(message)
{
    /// <summary>The denying decision, including reason code and source.</summary>
    public Decision Decision { get; } = decision;
}

/// <summary>Thrown when a catalogue invariant is violated (e.g. a relationship endpoint is missing).</summary>
public sealed class CatalogueValidationException(string message) : Exception(message);

/// <summary>Thrown when creating an entity whose id already exists in the tenant.</summary>
public sealed class CatalogueConflictException(string message) : Exception(message);
