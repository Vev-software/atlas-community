using Vev.Atlas.Fabric;
using Vev.Fabric.Contracts;
using Vev.Fabric.Contracts.Entitlements;
using Vev.Fabric.Contracts.Sharing;

namespace Vev.Atlas.Domain.Sharing.Push;

public enum PushStatus
{
    /// <summary>Nothing was sent: not active, or nothing changed since the last accepted push.</summary>
    Skipped,
    Accepted,

    /// <summary>The consumer refused; Atlas stopped pushing to it.</summary>
    Denied,

    /// <summary>A transient failure; the next attempt is scheduled with backoff.</summary>
    WillRetry
}

public sealed record PushReport(PushStatus Status, ConnectedConsumer Consumer, string? Detail = null);

/// <summary>
/// One push of the signed landscape digest to one connected consumer (atlas#176). Each push is a fresh digest with a higher sequence,
/// is audited exactly once, and never writes payload content to a log or an audit record. A refusal by the consumer stops pushing and is
/// shown; a transient failure is retried with growing waits.
/// </summary>
public sealed class LandscapePushService(
    IRequestContextAccessor context,
    IAuthorizer authorizer,
    IAtlasAuditSink audit,
    IConnectedConsumerStore store,
    IShareConsumerClient client,
    LandscapeShareService share,
    LandscapePushSettings settings,
    TimeProvider clock)
{
    public async Task<PushReport> PushAsync(string consumerId, bool force = false, CancellationToken ct = default)
    {
        // Authorized before anything is looked up, so a caller without the elevated permission learns nothing about which consumers exist.
        var decision = authorizer.Authorize(context.Tenant, context.Principal, AtlasActions.LandscapeShare, new ResourceId("atlas:landscape"));
        if (!decision.Allowed)
        {
            throw AccessDeniedException.FromAuthorization(decision, $"'{AtlasActions.LandscapeShare}' denied ({decision.ReasonCode}).");
        }

        var consumer = await store.GetAsync(context.Tenant, consumerId, ct) ?? throw new ConnectedConsumerException("The consumer was not found.", notFound: true);
        var now = clock.GetUtcNow();
        if (consumer.State != ConnectedConsumerState.Active) return new PushReport(PushStatus.Skipped, consumer, $"The consumer is {consumer.State.ToString().ToLowerInvariant()}.");

        // An expired credential cannot push; this is a stop, not a retry. Reconnecting issues a new one.
        if (consumer.CredentialExpiresAt is { } expires && expires <= now)
        {
            return await StopAsync(consumer, ReasonCodes.SharingCredentialExpired, "The credential has expired. Connect again with a new activation code.", now, ct);
        }

        var credential = await store.GetCredentialAsync(context.Tenant, consumer.Id, ct);
        if (credential is null) return await StopAsync(consumer, "credential_missing", "The credential is missing. Connect again with a new activation code.", now, ct);

        var fingerprint = await share.ContentFingerprintAsync(consumer.Scope, ct);
        var staleAfter = TimeSpan.FromHours(settings.DailyHours);
        var fresh = consumer.LastSuccessAt is { } last && now - last < staleAfter;
        if (!force && fresh && string.Equals(fingerprint, consumer.LastContentFingerprint, StringComparison.Ordinal))
        {
            return new PushReport(PushStatus.Skipped, consumer, "Nothing has changed since the last accepted push.");
        }

        var signed = await share.CreateForPushAsync(consumer.Scope, ct);
        var bytes = LandscapeShareService.Render(signed);
        PushResult result;
        try
        {
            result = await client.PushAsync(consumer.DestinationUrl, credential, bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only the kind of failure is kept, never a message that could carry content.
            result = new PushResult(PushOutcome.Retry, Error: $"Could not reach the consumer ({ex.GetType().Name}).");
        }

        var resource = $"atlas:landscape/share/push/{context.Tenant.TenantId}/{consumer.EnrollmentId}?digest={signed.Digest.DigestId}&sequence={signed.Digest.Sequence}&items={signed.Digest.Items.Length}";
        switch (result.Outcome)
        {
            case PushOutcome.Accepted:
            {
                var updated = consumer with
                {
                    LastAttemptAt = now, LastSuccessAt = now, LastSuccessSequence = signed.Digest.Sequence, LastError = null,
                    FailureCount = 0, NextAttemptAt = null, LastContentFingerprint = fingerprint,
                };
                await store.UpdateAsync(context.Tenant, updated, ct);
                await EmitAsync(DataSharingAuditVocabulary.PushAcceptAction, resource, AuditOutcome.Success, ct);
                return new PushReport(PushStatus.Accepted, updated);
            }

            case PushOutcome.Denied:
            {
                var reason = string.IsNullOrWhiteSpace(result.ReasonCode) ? "denied" : result.ReasonCode!;
                await EmitAsync(DataSharingAuditVocabulary.PushDenyAction, $"{resource}&reason={SafeCode(reason)}", AuditOutcome.Denied, ct);
                return await StopAsync(consumer, reason, Explain(reason), now, ct, audited: true);
            }

            default:
            {
                var failures = consumer.FailureCount + 1;
                var detail = result.Outcome == PushOutcome.Replayed ? "The consumer saw a digest it had already seen; the next push uses a higher sequence." : result.Error ?? "The push failed.";
                var updated = consumer with { LastAttemptAt = now, LastError = detail, FailureCount = failures, NextAttemptAt = now + Backoff(failures, settings.MaxBackoffMinutes) };
                await store.UpdateAsync(context.Tenant, updated, ct);
                await EmitAsync("atlas.landscape.share.push.fail", $"{resource}&failures={failures}", AuditOutcome.Failure, ct);
                return new PushReport(PushStatus.WillRetry, updated, detail);
            }
        }
    }

    /// <summary>One minute, doubling, up to the configured ceiling.</summary>
    public static TimeSpan Backoff(int failures, int maxMinutes)
    {
        var minutes = Math.Pow(2, Math.Clamp(failures - 1, 0, 16));
        return TimeSpan.FromMinutes(Math.Min(minutes, Math.Max(1, maxMinutes)));
    }

    private async Task<PushReport> StopAsync(ConnectedConsumer consumer, string reason, string explanation, DateTimeOffset now, CancellationToken ct, bool audited = false)
    {
        var stopped = consumer with { State = ConnectedConsumerState.Stopped, StopReason = SafeCode(reason), LastAttemptAt = now, LastError = explanation, NextAttemptAt = null };
        await store.UpdateAsync(context.Tenant, stopped, ct);
        await store.ClearCredentialAsync(context.Tenant, consumer.Id, ct);
        if (!audited) await EmitAsync(DataSharingAuditVocabulary.PushDenyAction, $"atlas:landscape/share/consumer/{consumer.Id}?reason={SafeCode(reason)}", AuditOutcome.Denied, ct);
        return new PushReport(PushStatus.Denied, stopped, explanation);
    }

    private ValueTask EmitAsync(string action, string resource, AuditOutcome outcome, CancellationToken ct) =>
        audit.WriteAsync(AtlasAudit.Event(context, clock, action, resource, AuditCategory.Data, outcome), ct);

    private static string Explain(string reason) => reason switch
    {
        ReasonCodes.SharingEnrollmentRevoked => "The consumer revoked the sharing. Pushing has stopped.",
        ReasonCodes.SharingEnrollmentSuspended => "The consumer suspended the sharing. Pushing has stopped; ask them to resume or connect again.",
        ReasonCodes.SharingCredentialExpired => "The credential has expired. Connect again with a new activation code.",
        ReasonCodes.SharingBindingMismatch => "The consumer says these digests do not belong to the account it connected. Pushing has stopped.",
        ReasonCodes.SharingEnrollmentPending => "The consumer has not activated the sharing yet. Pushing has stopped.",
        _ => $"The consumer refused the push ({SafeCode(reason)}). Pushing has stopped.",
    };

    private static string SafeCode(string value) => new(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.').Take(64).ToArray());
}
