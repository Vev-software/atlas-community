using System.Collections.Immutable;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;

namespace Vev.Atlas.Domain.Sharing.Push;

/// <summary>Where a connected consumer stands. Nothing is pushed unless it is <see cref="Active"/>.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ConnectedConsumerState>))]
public enum ConnectedConsumerState
{
    /// <summary>Connected and being kept up to date.</summary>
    Active,

    /// <summary>An admin paused the sharing; nothing is pushed until it is resumed.</summary>
    Paused,

    /// <summary>The consumer refused pushes (it revoked or suspended the enrollment, or the credential expired). Atlas stopped and says why.</summary>
    Stopped,

    /// <summary>An admin revoked it on this side. The credential is gone and it cannot be resumed.</summary>
    Revoked
}

/// <summary>One consumer an admin connected: where to push, what to share, and how the last pushes went. Never holds the credential.</summary>
public sealed record ConnectedConsumer(
    string Id,
    string Name,
    string DestinationUrl,
    string EnrollmentId,
    ConnectedConsumerState State,
    LandscapeDigestScope Scope,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? CredentialExpiresAt = null,
    string? StopReason = null,
    DateTimeOffset? LastAttemptAt = null,
    DateTimeOffset? LastSuccessAt = null,
    int? LastSuccessSequence = null,
    string? LastError = null,
    int FailureCount = 0,
    DateTimeOffset? NextAttemptAt = null,
    string? LastContentFingerprint = null);

/// <summary>A due consumer found by the scheduler, with the tenant it belongs to.</summary>
public sealed record DueConsumer(string TenantId, string ConsumerId);

/// <summary>
/// Tenant-scoped storage of connected consumers and their credentials. The credential is stored protected and is only ever
/// read to make a push or a revoke call; it is never part of <see cref="ConnectedConsumer"/> and never shown.
/// </summary>
public interface IConnectedConsumerStore
{
    Task<ImmutableArray<ConnectedConsumer>> ListAsync(TenantContext tenant, CancellationToken ct = default);

    Task<ConnectedConsumer?> GetAsync(TenantContext tenant, string id, CancellationToken ct = default);

    Task AddAsync(TenantContext tenant, ConnectedConsumer consumer, string credential, CancellationToken ct = default);

    Task UpdateAsync(TenantContext tenant, ConnectedConsumer consumer, CancellationToken ct = default);

    /// <summary>The stored credential, or null when none is held (never connected, or revoked).</summary>
    Task<string?> GetCredentialAsync(TenantContext tenant, string id, CancellationToken ct = default);

    /// <summary>Forget the credential. Used when a consumer is revoked or has stopped.</summary>
    Task ClearCredentialAsync(TenantContext tenant, string id, CancellationToken ct = default);

    /// <summary>
    /// Active consumers across all tenants that should be tried now: a scheduled retry that has come due, or else one that has never
    /// succeeded, whose last success is older than <paramref name="staleAfter"/>, or whose tenant is in <paramref name="changedTenants"/>.
    /// Only ids are returned; each push then runs inside its own tenant.
    /// </summary>
    Task<ImmutableArray<DueConsumer>> ListDueAsync(DateTimeOffset now, TimeSpan staleAfter, IReadOnlySet<string> changedTenants, CancellationToken ct = default);
}

/// <summary>Told when a tenant's landscape changed, so connected consumers can be updated soon (debounced by the scheduler).</summary>
public interface ILandscapeChangeNotifier
{
    void Notify(string tenantId);
}

/// <summary>What the consumer answered when asked to activate with a one-time code.</summary>
public sealed record ConsumerActivationResult(bool Accepted, string? EnrollmentId, string? Credential, DateTimeOffset? CredentialExpiresAt, string? ReasonCode, string? Error);

public enum PushOutcome
{
    /// <summary>The consumer accepted the digest.</summary>
    Accepted,

    /// <summary>The consumer refused the push for a reason it named (a Fabric reason code). Retrying will not help.</summary>
    Denied,

    /// <summary>The consumer saw the digest as a replay (its sequence was not higher than one already accepted).</summary>
    Replayed,

    /// <summary>A transient failure: the consumer was unreachable or answered with a server error or a throttle. Try again later.</summary>
    Retry
}

public sealed record PushResult(PushOutcome Outcome, string? ReasonCode = null, string? Error = null);

/// <summary>
/// The outbound call to a consuming product, behind a port so the transport is replaceable and testable. Only the destination the
/// admin entered is ever contacted, and nothing but the signed digest is ever sent.
/// </summary>
public interface IShareConsumerClient
{
    /// <summary>Redeem the one-time activation code at the consumer. The public key lets the consumer pin who signs the digests.</summary>
    Task<ConsumerActivationResult> ActivateAsync(string destinationUrl, string activationCode, string keyId, string publicKey, CancellationToken ct = default);

    Task<PushResult> PushAsync(string destinationUrl, string credential, byte[] signedDigestJson, CancellationToken ct = default);

    /// <summary>Tell the consumer the sharing is revoked from this side. Best effort: the local revocation stands either way.</summary>
    Task RevokeAsync(string destinationUrl, string credential, CancellationToken ct = default);
}

/// <summary>Where pushes may go. A destination is a customer-chosen URL that Atlas will call, so it is restricted.</summary>
public static class DestinationPolicy
{
    /// <summary>Returns the normalized base URL, or the reason it is refused.</summary>
    public static (string? Url, string? Problem) Validate(string? input, bool allowInsecureHttp, IReadOnlyCollection<string>? allowedHosts = null)
    {
        if (string.IsNullOrWhiteSpace(input) || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
        {
            return (null, "Enter the destination as a full https:// address.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            return (null, "The destination must use https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return (null, "The destination must be a plain address without credentials, a query or a fragment.");
        }

        if (allowedHosts is { Count: > 0 } && !allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return (null, "This destination is not on the list of hosts this installation may push to.");
        }

        return (uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), null);
    }

    /// <summary>
    /// True when a resolved address must not be pushed to by default: loopback, private, link-local, unique-local, multicast or
    /// unspecified ranges. The transport checks every address it actually connects to, which also defeats DNS tricks.
    /// </summary>
    public static bool IsInternalAddress(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(address) || address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (bytes[0] & 0xFE) == 0xFC;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127)   // carrier-grade NAT
            || b[0] == 0
            || b[0] >= 224;
    }
}
