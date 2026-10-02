using System.Collections.Immutable;
using Vev.Atlas.Contracts;
using Vev.Atlas.Fabric;
using Vev.Fabric.Contracts.Sharing;

namespace Vev.Atlas.Domain.Sharing.Push;

/// <summary>Operator settings of the outbound push. Off by default: with no connected consumer nothing is ever sent.</summary>
public sealed class LandscapePushSettings
{
    /// <summary>How often the scheduler looks for consumers that are due.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>A change is pushed this long after the last change, so a burst of edits is one push.</summary>
    public int DebounceSeconds { get; set; } = 60;

    /// <summary>A consumer is also pushed to at least this often, changed or not, so it can tell the sharing is alive.</summary>
    public int DailyHours { get; set; } = 24;

    /// <summary>The longest wait between retries of a failing push.</summary>
    public int MaxBackoffMinutes { get; set; } = 360;

    public int MaxConsumersPerTenant { get; set; } = 5;

    /// <summary>Allow destinations that resolve to loopback or private addresses. Off: a hosted installation never reaches into its own network.</summary>
    public bool AllowPrivateDestinations { get; set; }

    /// <summary>Allow plain http destinations. For local development only.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>When set, only these hosts may be connected.</summary>
    public List<string> AllowedHosts { get; set; } = [];
}

/// <summary>Why an admin's request was refused, in words fit to show.</summary>
public sealed class ConnectedConsumerException(string message, bool notFound = false) : Exception(message)
{
    public bool NotFound { get; } = notFound;
}

/// <summary>
/// Connect, pause, resume and revoke the consumers an installation pushes its landscape digest to (atlas#176). Every operation needs
/// the elevated <c>atlas.landscape.share</c> authorization. Connecting redeems the consumer's one-time activation code at the
/// destination the admin entered; the credential that comes back is stored protected and is never shown again.
/// </summary>
public sealed class ConnectedConsumerService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAtlasAuditSink audit,
    IConnectedConsumerStore store,
    IShareConsumerClient client,
    IDigestSigner signer,
    LandscapePushSettings settings,
    TimeProvider clock)
{
    private static readonly ResourceId LandscapeResource = new("atlas:landscape");

    public async Task<ImmutableArray<ConnectedConsumer>> ListAsync(CancellationToken ct = default)
    {
        Authorize();
        return await store.ListAsync(context.Tenant, ct);
    }

    public async Task<ConnectedConsumer> ConnectAsync(string? name, string? destinationUrl, string? activationCode, LandscapeDigestScope scope, CancellationToken ct = default)
    {
        Authorize();
        var (url, problem) = DestinationPolicy.Validate(destinationUrl, settings.AllowInsecureHttp, settings.AllowedHosts);
        if (url is null) throw new ConnectedConsumerException(problem!);
        if (string.IsNullOrWhiteSpace(activationCode)) throw new ConnectedConsumerException("Enter the activation code the consumer showed you.");
        if (scope.Kinds.Length == 0) throw new ConnectedConsumerException("Choose at least one kind to share.");

        var existing = await store.ListAsync(context.Tenant, ct);
        var live = existing.Where(c => c.State != ConnectedConsumerState.Revoked).ToList();
        if (live.Count >= settings.MaxConsumersPerTenant) throw new ConnectedConsumerException($"At most {settings.MaxConsumersPerTenant} consumers can be connected. Revoke one first.");
        if (live.Any(c => string.Equals(c.DestinationUrl, url, StringComparison.OrdinalIgnoreCase)))
            throw new ConnectedConsumerException("This destination is already connected. Revoke it first to connect again.");

        // The consumer pins the key that signs the digests it will receive, so it is told which key that is.
        var key = await signer.SignAsync("atlas-share-activation"u8.ToArray(), ct);
        var result = await client.ActivateAsync(url, activationCode.Trim(), key.KeyId, key.PublicKey, ct);
        if (!result.Accepted || string.IsNullOrWhiteSpace(result.Credential) || string.IsNullOrWhiteSpace(result.EnrollmentId))
        {
            await EmitAsync(DataSharingAuditVocabulary.EnrollmentActivateAction, $"atlas:landscape/share/consumer?result=refused&reason={Safe(result.ReasonCode)}", AuditOutcome.Denied, ct);
            throw new ConnectedConsumerException(Describe(result));
        }

        var consumer = new ConnectedConsumer(
            Guid.NewGuid().ToString("N"), CleanName(name, url), url, result.EnrollmentId!, ConnectedConsumerState.Active, scope,
            clock.GetUtcNow(), context.Principal.PrincipalId, result.CredentialExpiresAt);
        await store.AddAsync(context.Tenant, consumer, result.Credential!, ct);
        await EmitAsync(DataSharingAuditVocabulary.EnrollmentActivateAction, $"atlas:landscape/share/consumer/{consumer.Id}?enrollment={consumer.EnrollmentId}", AuditOutcome.Success, ct);
        return consumer;
    }

    public async Task<ConnectedConsumer> UpdateScopeAsync(string id, LandscapeDigestScope scope, CancellationToken ct = default)
    {
        Authorize();
        if (scope.Kinds.Length == 0) throw new ConnectedConsumerException("Choose at least one kind to share.");
        var consumer = await RequireAsync(id, ct);
        if (consumer.State == ConnectedConsumerState.Revoked) throw new ConnectedConsumerException("This consumer is revoked.");
        // The new scope applies from the next push; forgetting the fingerprint makes that push happen soon.
        var updated = consumer with { Scope = scope, LastContentFingerprint = null, NextAttemptAt = null };
        await store.UpdateAsync(context.Tenant, updated, ct);
        await EmitAsync("atlas.landscape.share.scope.update", $"atlas:landscape/share/consumer/{id}?kinds={scope.Kinds.Length}&tags={scope.Tags.Length}", AuditOutcome.Success, ct);
        return updated;
    }

    public async Task<ConnectedConsumer> PauseAsync(string id, CancellationToken ct = default)
    {
        Authorize();
        var consumer = await RequireAsync(id, ct);
        if (consumer.State != ConnectedConsumerState.Active) throw new ConnectedConsumerException("Only an active consumer can be paused.");
        var updated = consumer with { State = ConnectedConsumerState.Paused };
        await store.UpdateAsync(context.Tenant, updated, ct);
        await EmitAsync(DataSharingAuditVocabulary.EnrollmentSuspendAction, $"atlas:landscape/share/consumer/{id}?by=source", AuditOutcome.Success, ct);
        return updated;
    }

    public async Task<ConnectedConsumer> ResumeAsync(string id, CancellationToken ct = default)
    {
        Authorize();
        var consumer = await RequireAsync(id, ct);
        if (consumer.State == ConnectedConsumerState.Stopped)
            throw new ConnectedConsumerException("The consumer stopped accepting pushes. Ask them for a new activation code and connect again.");
        if (consumer.State != ConnectedConsumerState.Paused) throw new ConnectedConsumerException("Only a paused consumer can be resumed.");
        var updated = consumer with { State = ConnectedConsumerState.Active, NextAttemptAt = null, FailureCount = 0, LastContentFingerprint = null };
        await store.UpdateAsync(context.Tenant, updated, ct);
        await EmitAsync("atlas.landscape.share.resume", $"atlas:landscape/share/consumer/{id}", AuditOutcome.Success, ct);
        return updated;
    }

    /// <summary>Revokes from this side: pushes stop at once, the credential is forgotten, and the consumer is told (best effort).</summary>
    public async Task<ConnectedConsumer> RevokeAsync(string id, CancellationToken ct = default)
    {
        Authorize();
        var consumer = await RequireAsync(id, ct);
        if (consumer.State == ConnectedConsumerState.Revoked) return consumer;
        var credential = await store.GetCredentialAsync(context.Tenant, id, ct);
        var updated = consumer with { State = ConnectedConsumerState.Revoked, StopReason = "revoked by this installation", NextAttemptAt = null };
        await store.UpdateAsync(context.Tenant, updated, ct);
        await store.ClearCredentialAsync(context.Tenant, id, ct);
        await EmitAsync(DataSharingAuditVocabulary.EnrollmentRevokeAction, $"atlas:landscape/share/consumer/{id}?by=source", AuditOutcome.Success, ct);
        if (credential is not null)
        {
            try { await client.RevokeAsync(consumer.DestinationUrl, credential, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* the local revocation stands; the consumer's own revoke or expiry closes its side */ }
        }

        return updated;
    }

    private async Task<ConnectedConsumer> RequireAsync(string id, CancellationToken ct) =>
        await store.GetAsync(context.Tenant, id, ct) ?? throw new ConnectedConsumerException("The consumer was not found.", notFound: true);

    private void Authorize()
    {
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.LandscapeShare, LandscapeResource);
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.LandscapeShare}' denied ({decision.ReasonCode}).");
        }
    }

    private ValueTask EmitAsync(string action, string resource, AuditOutcome outcome, CancellationToken ct) =>
        audit.WriteAsync(AtlasAudit.Event(context, clock, action, resource, AuditCategory.Admin, outcome), ct);

    /// <summary>What to tell the admin when the consumer refused the activation. Reason codes are Fabric's.</summary>
    private static string Describe(ConsumerActivationResult result) => result.ReasonCode switch
    {
        "sharing_activation_code_invalid" => "The consumer did not accept that code. Check it and try again.",
        "sharing_activation_code_expired" => "That code has expired. Ask the consumer for a new one.",
        "sharing_activation_code_used" => "That code was already used. Ask the consumer for a new one.",
        "sharing_enrollment_revoked" => "The consumer revoked this enrollment. Ask them for a new one.",
        _ => result.Error ?? "The consumer could not be activated. Check the address and the code.",
    };

    private static string CleanName(string? name, string url)
    {
        var text = string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : name.Trim();
        return text.Length <= 80 ? text : text[..80];
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : new string(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').Take(64).ToArray());
}
