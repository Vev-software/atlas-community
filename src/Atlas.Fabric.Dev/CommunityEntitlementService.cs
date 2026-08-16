using System.Text.Json;
using Microsoft.Extensions.Options;
using Vev.Atlas.Fabric;
using Vev.Fabric.Contracts.Entitlements;

namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Atlas-side local entitlement service. It consumes the public Fabric entitlement contract and evaluates a
/// signed local snapshot on the request path; when no snapshot source is configured it falls back to the
/// Community empty-set behaviour Atlas already shipped with.
/// </summary>
public sealed class CommunityEntitlementService : IEntitlementService, IEntitlementAllowanceProvider
{
    private const string AiStructureCapability = "atlas.ai.structure";
    private const string CommunitySource = "entitlement:community-default";
    private const string SnapshotConfigSource = "entitlement:snapshot-config";
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, EntitlementAllowanceSnapshot> DefaultCommunityAllowances =
        new Dictionary<string, EntitlementAllowanceSnapshot>(StringComparer.Ordinal)
        {
            [AiStructureCapability] =
                EntitlementAllowanceSnapshot.FixedWindow(3, EntitlementAllowanceWindows.Day, CommunitySource)
        };

    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly IReadOnlySet<string>? fallbackGrantedCapabilities;
    private readonly IReadOnlyDictionary<string, EntitlementAllowanceSnapshot>? fallbackAllowances;
    private JsonSignedEntitlementSnapshotVerifier? verifier;
    private LocalEntitlementEvaluator? evaluator;
    private bool hasConfiguredSource;
    private string configuredSource = CommunitySource;
    private string? lastFailureReasonCode;
    private int communityAiStructureDailyLimit = 3;

    /// <summary>The default Community evaluator: no paid grants and a small visible AI structuring allowance.</summary>
    public static CommunityEntitlementService Community { get; } =
        new(new HashSet<string>(StringComparer.Ordinal), DefaultCommunityAllowances);

    /// <summary>
    /// Test-friendly constructor for fixed Community grants and allowances.
    /// </summary>
    public CommunityEntitlementService(
        IReadOnlySet<string> grantedCapabilities,
        IReadOnlyDictionary<string, EntitlementAllowanceSnapshot>? allowances = null,
        TimeProvider? timeProvider = null)
    {
        fallbackGrantedCapabilities = grantedCapabilities;
        fallbackAllowances = allowances;
        clock = timeProvider ?? TimeProvider.System;
        communityAiStructureDailyLimit = allowances is not null &&
                                         allowances.TryGetValue(AiStructureCapability, out var allowance) &&
                                         allowance.Limit is { } limit
            ? limit
            : 3;
    }

    /// <summary>
    /// Runtime constructor: binds to the public Fabric snapshot verifier/evaluator and reloads when config changes.
    /// </summary>
    public CommunityEntitlementService(IOptionsMonitor<AtlasEntitlementOptions> options, TimeProvider timeProvider)
    {
        clock = timeProvider;
        Reload(options.CurrentValue);
        options.OnChange(Reload);
    }

    /// <inheritdoc />
    public EntitlementDecision Evaluate(EntitlementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Principal);

        lock (sync)
        {
            if (evaluator is not null)
            {
                return evaluator.Evaluate(request);
            }

            var now = clock.GetUtcNow();
            if (hasConfiguredSource)
            {
                return EntitlementDecision.Deny(
                    request.Capability,
                    lastFailureReasonCode ?? ReasonCodes.EntitlementUnavailable,
                    configuredSource,
                    now);
            }

            if (fallbackGrantedCapabilities?.Contains(request.Capability.Value) == true)
            {
                return EntitlementDecision.Allow(request.Capability, CommunitySource, now);
            }

            return EntitlementDecision.Deny(request.Capability, ReasonCodes.EntitlementDenied, CommunitySource, now);
        }
    }

    /// <inheritdoc />
    public EntitlementAllowanceSnapshot Describe(EntitlementAllowanceRequest request)
    {
        var decision = Evaluate(new EntitlementRequest(request.Tenant, request.Capability, request.Principal, request.Resource));
        if (decision.Allowed)
        {
            return EntitlementAllowanceSnapshot.UnlimitedAllowance(decision.Source);
        }

        lock (sync)
        {
            if (!hasConfiguredSource)
            {
                if (fallbackAllowances?.TryGetValue(request.Capability.Value, out var explicitAllowance) == true)
                {
                    return explicitAllowance;
                }

                if (string.Equals(request.Capability.Value, AiStructureCapability, StringComparison.Ordinal) &&
                    communityAiStructureDailyLimit > 0)
                {
                    return EntitlementAllowanceSnapshot.FixedWindow(
                        communityAiStructureDailyLimit,
                        EntitlementAllowanceWindows.Day,
                        CommunitySource);
                }
            }
        }

        return EntitlementAllowanceSnapshot.Deny(decision.ReasonCode, decision.Source);
    }

    /// <summary>
    /// Refresh the connected signed snapshot, if a remote snapshot URL is configured.
    /// </summary>
    public async Task RefreshFromRemoteAsync(HttpClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        string? url;
        lock (sync)
        {
            url = hasConfiguredSource && configuredSource.StartsWith("entitlement:snapshot-url:", StringComparison.Ordinal)
                ? configuredSource["entitlement:snapshot-url:".Length..]
                : null;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var payload = await client.GetStringAsync(url, ct);
            LoadSignedSnapshot(payload, $"entitlement:snapshot-url:{url}");
        }
        catch
        {
            lock (sync)
            {
                lastFailureReasonCode ??= ReasonCodes.EntitlementUnavailable;
            }
        }
    }

    private void Reload(AtlasEntitlementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (sync)
        {
            communityAiStructureDailyLimit = Math.Max(0, options.CommunityAiStructureDailyLimit);
            verifier = CreateVerifier(options);
            evaluator = null;
            hasConfiguredSource = false;
            configuredSource = CommunitySource;
            lastFailureReasonCode = null;

            if (!string.IsNullOrWhiteSpace(options.SnapshotDocumentJson))
            {
                LoadSignedSnapshot(options.SnapshotDocumentJson, "entitlement:snapshot-inline");
                return;
            }

            if (!string.IsNullOrWhiteSpace(options.SnapshotDocumentPath))
            {
                hasConfiguredSource = true;
                configuredSource = $"entitlement:snapshot-path:{options.SnapshotDocumentPath}";
                if (File.Exists(options.SnapshotDocumentPath))
                {
                    LoadSignedSnapshot(File.ReadAllText(options.SnapshotDocumentPath), configuredSource);
                }
                else
                {
                    evaluator = null;
                    lastFailureReasonCode = ReasonCodes.EntitlementUnavailable;
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(options.SnapshotDocumentUrl))
            {
                hasConfiguredSource = true;
                configuredSource = $"entitlement:snapshot-url:{options.SnapshotDocumentUrl}";
            }
        }
    }

    private static JsonSignedEntitlementSnapshotVerifier CreateVerifier(AtlasEntitlementOptions options)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pair in options.TrustedKeys)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            keys[pair.Key] = Convert.FromBase64String(pair.Value);
        }

        return new JsonSignedEntitlementSnapshotVerifier(new HmacSha256SignatureVerifier(keys));
    }

    private void LoadSignedSnapshot(string json, string source)
    {
        hasConfiguredSource = true;
        configuredSource = source;

        try
        {
            var document = JsonSerializer.Deserialize<SignedEntitlementSnapshot>(json, SnapshotJson);
            if (document is null)
            {
                evaluator = null;
                lastFailureReasonCode = ReasonCodes.EntitlementSnapshotInvalid;
                return;
            }

            var snapshotVerifier = verifier ?? throw new InvalidOperationException("Snapshot verifier is not configured.");
            var candidate = new LocalEntitlementEvaluator(snapshotVerifier, clock);
            var verification = candidate.LoadSnapshot(document);
            if (!verification.IsValid)
            {
                evaluator = null;
                lastFailureReasonCode = verification.ReasonCode;
                return;
            }

            evaluator = candidate;
            lastFailureReasonCode = null;
        }
        catch (JsonException)
        {
            evaluator = null;
            lastFailureReasonCode = ReasonCodes.EntitlementSnapshotInvalid;
        }
    }
}
