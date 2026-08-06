using System.Collections.Concurrent;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Registry a product uses to declare its role→permission definitions on top of the Fabric authz
/// mechanism (fabric#5 DoD: "a product registers role definitions without owning the engine").
/// Atlas populates this at startup with its own role names; Fabric owns the decision.
/// </summary>
public sealed class AuthorizationPolicyRegistry
{
    private readonly ConcurrentDictionary<string, string[]> _actionRequiredRoles = new(StringComparer.Ordinal);

    /// <summary>Declare that <paramref name="action"/> requires the principal to hold any of <paramref name="anyOfRoles"/>.</summary>
    public AuthorizationPolicyRegistry Require(string action, params string[] anyOfRoles)
    {
        _actionRequiredRoles[action] = anyOfRoles;
        return this;
    }

    internal bool TryGetRequiredRoles(string action, out string[] roles)
        => _actionRequiredRoles.TryGetValue(action, out roles!);
}

/// <summary>
/// Dev implementation of the Fabric <see cref="IAuthorizer"/> mechanism. It knows nothing about Atlas
/// domain concepts — it evaluates coarse roles registered through <see cref="AuthorizationPolicyRegistry"/>.
/// </summary>
public sealed class DevAuthorizer(AuthorizationPolicyRegistry policies) : IAuthorizer
{
    private const string Source = "dev-authorizer";

    /// <inheritdoc />
    public Decision Authorize(TenantContext tenant, PrincipalContext principal, string action, ResourceId resource)
    {
        if (!policies.TryGetRequiredRoles(action, out var required) || required.Length == 0)
        {
            // No role requirement registered for this action → allowed for any authenticated principal.
            return Decision.Allow(Source);
        }

        var holdsRole = principal.Roles.Any(r => required.Contains(r, StringComparer.Ordinal));
        return holdsRole
            ? Decision.Allow(Source)
            : Decision.Deny(ReasonCodes.RoleMissing, Source);
    }
}
