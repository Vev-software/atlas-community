using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Vev.Atlas.Fabric.Dev;

/// <summary>
/// Periodically refreshes the signed entitlement snapshot when Atlas is configured with a connected source.
/// The request path always stays local and fail-static; this service only updates the cached snapshot.
/// </summary>
public sealed class EntitlementSnapshotRefreshService(
    CommunityEntitlementService entitlements,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<AtlasEntitlementOptions> options) : BackgroundService
{
    public const string HttpClientName = "atlas-entitlements";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await entitlements.RefreshFromRemoteAsync(httpClientFactory.CreateClient(HttpClientName), stoppingToken);

            var refreshSeconds = Math.Max(30, options.CurrentValue.SnapshotRefreshSeconds);
            await Task.Delay(TimeSpan.FromSeconds(refreshSeconds), stoppingToken);
        }
    }
}
