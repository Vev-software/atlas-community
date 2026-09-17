# Optional usage heatmap

Collection is off by default. Atlas ships a small first-party collector, with no third-party SDK,
autocapture, session replay, tracking cookie, fingerprint or default receiving service. A local
heatmap and navigation counts appear under **Usage privacy → This tab’s heatmap and navigation
counts** after consent. This dashboard uses only this tab's in-memory counts, not landscape data.

To enable the consent prompt, an operator sets `Atlas__UsageAnalytics__Enabled=true` and
`Atlas__UsageAnalytics__Endpoint=https://your-collector.example/events`. Only HTTPS destinations
without credentials, query parameters or fragments are accepted. Invalid or absent configuration
keeps collection disabled. Host the receiver in your required region; any hosted deployment using
this feature must use an EU-resident receiver and storage. No hosted endpoint is bundled.

The consent dialog names the receiver and focuses **Decline**. Enter, Escape, Close or Decline
without accepting leaves collection off, and all catalogue functions remain usable. **Allow usage
data** enables collection for the current tab and exact destination. The choice is kept in
sessionStorage; a new tab/session or changed endpoint requires its own choice. **Withdraw consent**
stops collection, discards queued events, aborts pending sends, and clears the local heatmap.
Previously delivered events cannot be recalled. Disable the operator setting and reload clients to
turn collection off deployment-wide. No historical events are replayed after accepting consent.

## Exact event contract

The receiver accepts JSON POSTs shaped as `{"version":1,"events":[...]}`. Each event contains only:

| Field | Values |
| --- | --- |
| `view`, `from` | landscape, systems, capabilities, roadmaps, lifecycle, reviews |
| `to` | one of those views, or details |
| `control` | view, kind-filter, search, asset-open, dependencies-visible, canvas, refresh, new-asset, context-pack, ai-open, getting-started, export, import, paste-landscape |
| `step` | tab-local sequence from 1 through 250; no session identifier |
| `elapsed` | under-5s, 5-30s, over-30s since consent/page load |
| `cell` | null, or 0–23 for a coarse 6-column × 4-row click grid over the visible landscape area |

Search records only that the search control was used. No query, keypress, DOM text, HTML, asset
name, asset/relationship ID, description, tags, tenant/principal identity, page URL/hash, exact
coordinates, screen dimensions or timestamps are read into events. Keyboard activation has no cell.
The find/inspect counts show search use, asset opens, and dependency sections made visible; they
are interaction counts, not unique-user conversions. There is no cross-tab or cross-user linkage.

Batches contain at most 20 events and send after 500 ms. At most 250 events are counted per page
load. Queues are memory-only; failures are dropped without retry and never affect the app. Leaving
the page drops queued events. The request omits credentials and referrers, rejects redirects, and
has a five-second timeout. The receiver must permit CORS POST with Content-Type: application/json.
No authentication token or cookie is sent, so use a dedicated receiver and rate-limit it.

The event payload excludes PII. Ordinary HTTP transport still exposes source IP, user agent and
origin to the receiver; the operator must disable identifying access logs, avoid enriching events
with these headers, and set retention/access rules. The app does not claim that transport metadata
is anonymous. Inspect requests to the configured endpoint to verify the payload and use the local
dashboard to compare counts. Browser tests cover disabled, declined, accepted and withdrawn states.
