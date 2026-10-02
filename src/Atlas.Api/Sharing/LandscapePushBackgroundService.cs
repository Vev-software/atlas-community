using System.Collections.Concurrent;
using Vev.Atlas.Domain;
using Vev.Atlas.Domain.Sharing.Push;
using Vev.Atlas.Fabric;
using Vev.Atlas.Fabric.Dev;

namespace Vev.Atlas.Api.Sharing;

/// <summary>
/// Remembers which tenants' landscapes changed and when, so connected consumers are updated shortly after the last edit of a burst
/// rather than on every edit.
/// </summary>
public sealed class LandscapeChangeNotifier(TimeProvider clock) : ILandscapeChangeNotifier
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _changed = new(StringComparer.Ordinal);

    public void Notify(string tenantId) => _changed[tenantId] = clock.GetUtcNow();

    /// <summary>The tenants whose last change is at least <paramref name="debounce"/> old, with the time of that change.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Settled(TimeSpan debounce)
    {
        var now = clock.GetUtcNow();
        return _changed.Where(pair => now - pair.Value >= debounce).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>Forget a change that was handled. A newer change in the meantime is kept.</summary>
    public void Handled(string tenantId, DateTimeOffset changedAt) =>
        _changed.TryRemove(new KeyValuePair<string, DateTimeOffset>(tenantId, changedAt));
}

/// <summary>
/// Keeps connected consumers up to date (atlas#176): a debounced push after a change, and at least one push a day. Each push runs inside
/// its own tenant under a system identity that exists only for this, and never starts on its own: with no connected consumer there is
/// nothing to do.
/// </summary>
public sealed class LandscapePushBackgroundService(
    IServiceScopeFactory scopes,
    LandscapePushSettings settings,
    LandscapeChangeNotifier changes,
    TimeProvider clock,
    ILogger<LandscapePushBackgroundService> logger) : BackgroundService
{
    public static readonly PrincipalContext SystemPrincipal = new("system:landscape-share-push", "Landscape share push", [AtlasRoles.Architect]);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first round comes after one interval, not at startup: nothing is pushed in the moment an installation starts.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, settings.PollSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The kind of failure only: nothing from a landscape or a consumer belongs in a log.
                logger.LogWarning("Landscape share push round failed ({ErrorType})", ex.GetType().Name);
            }
        }
    }

    /// <summary>One scheduling round. Public so a test can drive it without waiting for the timer.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var settled = changes.Settled(TimeSpan.FromSeconds(settings.DebounceSeconds));
        IReadOnlyList<DueConsumer> due;
        using (var scope = scopes.CreateScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<IConnectedConsumerStore>()
                .ListDueAsync(clock.GetUtcNow(), TimeSpan.FromHours(settings.DailyHours), settled.Keys.ToHashSet(StringComparer.Ordinal), ct);
        }

        foreach (var tenant in due.GroupBy(d => d.TenantId))
        {
            using var scope = scopes.CreateScope();
            using var ambient = AmbientRequestContextAccessor.BeginScope(new TenantContext(tenant.Key), SystemPrincipal, $"share-push-{Guid.NewGuid():N}");
            var push = scope.ServiceProvider.GetRequiredService<LandscapePushService>();
            foreach (var consumer in tenant)
            {
                try
                {
                    await push.PushAsync(consumer.ConsumerId, force: false, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Landscape share push failed ({ErrorType})", ex.GetType().Name);
                }
            }
        }

        // Every settled change has now been seen by the scheduler: a consumer that failed is retried by its own schedule, not by the change.
        foreach (var (tenantId, changedAt) in settled) changes.Handled(tenantId, changedAt);
    }
}
