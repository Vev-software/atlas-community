# Atlas Community Edition — development

Early scaffolding for the free, self-hostable **asset-management** edition (handbook `12 §Phase 0`,
`11`). Catalogue your systems, applications, servers, infrastructure and data layer, plus manual
relationships, join keys and tags. Analysis and other paid Atlas capabilities live outside this
public repository.

> **Security posture.** The sections below cover individual controls (identity, tenant isolation,
> encryption at rest, supply chain). For the consolidated threat-model note and compatibility statement,
> see [`threat-model.md`](./threat-model.md).

## Layout

```
src/
  Atlas.Fabric.Abstractions  Atlas-owned seam types over the public Vev.Fabric.Contracts package
                             (request context, authz/audit adapters, allowance UX helpers)
  Atlas.Fabric.Dev           Local implementations over that seam (single tenant, role-gated authz,
                             signed-snapshot entitlements, in-memory audit)
  Atlas.Domain               Asset catalogue domain; consumes the public atlas-contracts model
  Atlas.Persistence          EF Core / SQLite behind the repository port
  Atlas.Api                  ASP.NET Core minimal API (API/SDK-first) + OpenAPI
tests/
  Atlas.Architecture.Tests   Boundary fitness tests (dependency direction, no `plan==`, no direct
                             AI-provider calls) — these fail the build on a violation
  Atlas.Api.Tests            Full-stack integration tests over an in-memory database
```

## Prerequisites

- .NET 10 SDK
- Access to nuget.org to restore the public `Vev.Atlas.Contracts` package.

## Build, test, run

```bash
dotnet test
dotnet run --project src/Atlas.Api
```

The API creates its SQLite schema on first run. OpenAPI is at `/openapi/v1.json`; health at `/health`.
The catalogue file holds reconnaissance-grade landscape data — for any real deployment, encrypt it at
rest ([Encryption at rest](#encryption-at-rest)).

## Entitlements & signed snapshots

Atlas now consumes the public `Vev.Fabric.Contracts` entitlement contract directly. The request path
never calls a control-plane API synchronously: `PaidCapabilityGate` asks the local
`CommunityEntitlementService`, which evaluates a cached signed snapshot with the Fabric
`LocalEntitlementEvaluator`.

- With **no snapshot source configured**, Community behaves exactly like before: paid capabilities are
  denied with `entitlement_denied`, and the visible free AI-structuring allowance remains local.
- With a **signed snapshot document** configured, Atlas verifies it against the configured trust
  anchors and evaluates grants locally and fail-static.
- With a **snapshot URL** configured, Atlas refreshes the cached signed snapshot periodically in the
  background; request-time decisions still stay local.

Configuration lives under `Atlas:Entitlements` (`Atlas__Entitlements__*` via env vars):

| Key | Meaning |
|---|---|
| `SnapshotDocumentJson` | Inline signed snapshot document JSON to import at startup. |
| `SnapshotDocumentPath` | Path to a signed snapshot document for offline / air-gapped installs. |
| `SnapshotDocumentUrl` | Connected source returning a signed snapshot document; refreshed in the background. |
| `SnapshotRefreshSeconds` | Refresh interval for `SnapshotDocumentUrl` (minimum effective interval 30s). |
| `TrustedKeys:<key-id>` | Base64-encoded symmetric key for the current Fabric HMAC verifier. |
| `CommunityAiStructureDailyLimit` | Local visible free allowance for `atlas.ai.structure` when no snapshot is configured. |

### Try it

```bash
# Create an asset (dev headers stand in for Fabric identity until it lands)
curl -X POST http://localhost:5199/api/v1/assets -H "Content-Type: application/json" \
  -H "X-Tenant-Id: demo" \
  -d '{"id":"app-1","kind":"application","name":"Checkout","lifecycle":"active"}'

# List the catalogue
curl http://localhost:5199/api/v1/assets -H "X-Tenant-Id: demo"

# A missing route returns 404
curl http://localhost:5199/api/v1/does-not-exist -H "X-Tenant-Id: demo"
```

## Portability: export & import

Customer-owned portability is a product promise (`README`, handbook `11 §2`), so it lives in the
runtime as a first-class surface, not an afterthought (issue #12). Everything speaks the published
`atlas-contracts` form.

```bash
# Export the whole tenant landscape as a portable, downloadable document (customer-owned export)
curl -OJ http://localhost:5199/api/v1/export -H "X-Tenant-Id: demo"   # → atlas-landscape.json

# Import a bundle back in. mode "merge" upserts; mode "replace" makes the tenant match the bundle.
curl -X POST http://localhost:5199/api/v1/import -H "Content-Type: application/json" \
  -H "X-Tenant-Id: demo" \
  -d '{
        "kind": "import",
        "mode": "merge",
        "assets": [
          { "externalId": "srv-01", "kind": "server", "name": "srv-01", "lifecycle": "active" }
        ],
        "relationships": []
      }'
```

- **Export** (`GET /api/v1/export`) returns a `LandscapeDocument` (assets + manual relationships)
  as a `Content-Disposition: attachment` download (`atlas-landscape.json`). It reuses the same read
  model as `GET /api/v1/landscape`, and stamps `generator` provenance (`"Atlas Community"` + build).
  A full-map export is the highest-value reconnaissance read, so it is **hardened** beyond a plain
  browse (atlas#36): it requires the elevated `atlas.landscape.export` authorization — a read-only
  `AtlasCustomer` is denied (403 + `role_missing`) — it emits exactly one `atlas.landscape.exported`
  audit record (actor, tenant, time, scope, format), and it is **rate-limited** per tenant (a fixed
  window, `429` when exceeded; tune with `Atlas:Export:PermitLimit` / `Atlas:Export:WindowSeconds`).
- **Import** (`POST /api/v1/import`) takes an `ImportBundle`, validates it, and applies it under write
  authorization with an audit event (`atlas.landscape.imported`). Each asset is matched by a stable
  catalogue id — its explicit `id` when given, otherwise its `externalId` — so **re-importing the same
  bundle is idempotent**. Relationship endpoints must resolve to an asset in the bundle or already in
  the catalogue; an unresolved reference rejects the whole bundle (400) before anything is written.
  - `mode: merge` upserts the assets/relationships in the bundle; everything else is left alone.
  - `mode: replace` makes the tenant match the bundle: assets not in the bundle are removed. Use a
    self-contained bundle (a full export) for replace.

### The format-adapter seam

The **core portability boundary is the canonical contract form** — `LandscapeDocument` out,
`ImportBundle` in. Format adapters only translate *between* some external format and that canonical
form; the tenant-scoped, authorized, audited apply logic in `AssetService` never changes. This is the
explicit seam future community adapters (ArchiMate, BPMN, report) compose onto:

```
Atlas.Domain/Portability/
  ILandscapeFormat.cs          ILandscapeExporter / ILandscapeImporter (the seam) + format ids
  AtlasJsonLandscapeFormat.cs  the canonical atlas-contracts JSON adapter (always registered)
  LandscapeFormatRegistry.cs   resolves a `?format=` id to its adapter (unknown → 400)
```

To add a community format, implement `ILandscapeExporter` and/or `ILandscapeImporter` (translating
your format to/from `LandscapeDocument` / `ImportBundle`), give it a lowercase, kebab-case `Format`
id, and register it in `AtlasCommunityRegistration`. The `/export` and `/import` endpoints select it
via `?format=…`. No core code changes.

### The open-core line for modules

Community modules add value **at the edges** — importers/exporters, connectors, UI panels — through the
capabilities and permissions they declare. What a module may **never** do is declare or satisfy a
**reserved paid capability** (`atlas.integration.mapping`, `atlas.eol.tracking`, `atlas.portfolio.apm`,
`atlas.roadmap.generate`, `atlas.ai.review`). The free/paid line is **entitlement-only**: a paid feature
stays behind `PaidCapabilityGate` → the Fabric entitlement decision, and no module can flip it to
allowed (atlas#22, engineering#3).

Any module install path runs the module's manifest through `ModuleInstallGuard.EnsureInstallableAsync`
first (`Atlas.Domain/ModuleInstallGuard.cs`). A manifest that claims a reserved paid capability is
**refused** — a `ModuleRejectedException` with the `reserved_capability` reason code, and an
`atlas.module.rejected` audit record. The reserved set (`AtlasCapabilities.ReservedPaid`) is pinned by a
fitness test, so it cannot silently lose an id. The generic manifest schema and extension model live in
the platform Module Author Guide (handbook §16) and the Fabric extension model (fabric#10).

### Compatibility & versioning

- Every exported `LandscapeDocument` (and every `ImportBundle`) carries a `contractVersion` — the
  **major** version of the `atlas-contracts` schema it conforms to (currently `"1"`). This is the
  compatibility contract: a consumer reads `contractVersion` to decide whether it understands the
  document.
- Within a major version, the schema only grows in backward-compatible ways (new optional fields), so
  a newer Community can still import an older document and older readers ignore fields they don't know.
- A breaking change is a new major version in `atlas-contracts` (gated by an ADR + migration there);
  the runtime would then accept both during a transition. Wire vocabulary stays kebab-case
  (`runs-on`, `part-of`, `depends-on`) with lowercase kinds — the contract owns those values.
- Schema conformance is proven in the test suite by round-tripping exported bytes through the
  published contract types with the canonical serializer, and by a full export → import round trip.

## Run with Docker

Atlas Community ships a container image and a Compose file so a self-hoster gets the API, the
read-only landscape UI and a persistent SQLite database with one command. Compose builds directly
from this repository; the Dockerfile restores `Vev.Atlas.Contracts` from nuget.org like the local
CLI build does.

```bash
# From the atlas-community repo root. Works with Docker or Podman.
docker compose up --build          # podman compose up --build
```

Then:

```bash
curl http://localhost:8080/health                        # {"status":"ok"}
curl http://localhost:8080/api/v1/assets                 # single-tenant self-host; no identity header needed
# The landscape UI: open http://localhost:8080/ in a browser.
```

The container runs in **single-tenant** identity mode (`Atlas__Identity__Mode=single-tenant`, set in the
`Dockerfile`): the whole catalogue is one fixed tenant and request identity comes from configuration,
not from `X-*` headers — see [Identity & tenancy](#identity--tenancy).

The catalogue is stored in SQLite on the `atlas-data` volume (`/data/atlas.db` in the container), so
it survives `docker compose down` / restarts. Remove it with `docker compose down -v`. That file holds
the whole landscape map — protect it at rest: see [Encryption at rest](#encryption-at-rest).

To build or run the image directly:

```bash
docker build -t atlas-community:local .
docker run --rm -p 8080:8080 -v atlas-data:/data atlas-community:local
```

The image runs as a non-root user, listens on port 8080, and carries a `HEALTHCHECK` that polls
`/health`. Configuration is standard ASP.NET Core — override the database location with
`ConnectionStrings__Atlas`, e.g. `-e ConnectionStrings__Atlas="Data Source=/data/atlas.db"`.

## URLs & hosting

Hostnames and paths are **deployment configuration, never a hard-coded VEV identity** (handbook `04`,
ADR `0002`): Atlas assumes no `vev.software` hostname, and the default is a **flat single-host shape** —
public UI, sign-in and the API all on one host at the root. A self-hoster changes nothing. Configure it
under `Atlas:Urls` (env vars use `Atlas__Urls__*`):

| Key | Default | Meaning |
|---|---|---|
| `PublicBaseUrl` | *(empty)* | Absolute external base for links built without a request (e.g. `https://atlas.example.com`). Empty → derived from the incoming request. |
| `PathBase` | *(empty)* | Sub-path the app is hosted under behind a reverse proxy (e.g. `/atlas`). Empty → host root. |
| `LoginPath` | `/login` | Where sign-in lives (under `PathBase`). |
| `ApiBasePath` | `/api` | Where the product API is mounted (under `PathBase`); `v1` hangs off it. |

The static UI reads its API base from `GET /app-config.js` (`window.__ATLAS__.apiBase`), so it is never
hard-coded to `/api` and follows `PathBase`/`ApiBasePath` automatically.

**Single-host (default).** Nothing to set: UI at `/`, API at `/api/v1/…`, sign-in at `/login`.

**White-label host.** Serve under the customer's own hostname; only set `PublicBaseUrl` if you generate
absolute links (e.g. emails). Requests still resolve their own host, so no config is needed just to change
the hostname:

```jsonc
// appsettings.Production.json
{ "Atlas": { "Urls": { "PublicBaseUrl": "https://atlas.acme.example" } } }
```

**Reverse-proxy sub-path.** Host Atlas under a path segment (proxy forwards `/atlas/*`):

```bash
# env-var form
Atlas__Urls__PathBase=/atlas
# → UI at /atlas/, API at /atlas/api/v1/…, health at /atlas/health, app-config reports apiBase "/atlas/api"
```

**Custom API mount.** Point the API somewhere other than `/api` (e.g. behind an API gateway prefix):

```bash
Atlas__Urls__ApiBasePath=/gateway   # API now at /gateway/v1/…, and the UI follows via app-config.js
```

## Identity & tenancy

Atlas holds reconnaissance-grade landscape data, so request identity must come from a trustworthy
source, never be asserted by the caller. `Atlas:Identity:Mode` (env: `Atlas__Identity__Mode`) selects one
of three modes, and Atlas **fails closed** when no trustworthy source is available (atlas#34):

| Mode | Identity source | Where it's allowed |
|---|---|---|
| `dev-headers` | `X-Tenant-Id` / `X-Principal-Id` / `X-Principal-Roles` request headers, defaulting to a single dev tenant with the `AtlasArchitect` role | **Development environment only** |
| `single-tenant` | A fixed tenant + roles from config (`Atlas:Identity:Tenant`, default `community`; `Atlas:Identity:Principal`, default `self-host`; `Atlas:Identity:Roles`, default `AtlasArchitect`). `X-*` headers are ignored, so no caller can name another tenant or escalate roles. | Any environment — this is the self-host default |
| `fabric-oidc` | A verified OIDC bearer token: the token is validated against the configured provider and its claims are mapped to the tenant + principal. The real multi-tenant identity source (fabric#3). | Any environment once a provider (`Atlas:Identity:Oidc:Authority`) is configured |

When the mode is unset it defaults from the environment: **Development → `dev-headers`**; any other
environment → **`fabric-oidc`**. Outside Development with no OIDC provider configured, the host **refuses
to start** rather than silently trust request headers. The self-hosted container therefore sets
`single-tenant` explicitly (see the `Dockerfile`). The header shim (`dev-headers`) can never be forced
on outside Development: the host refuses to start if you try.

### Fabric OIDC (`fabric-oidc`)

Real multi-tenant identity. Atlas validates a signed OIDC JWT bearer token against a provider you bring
(handbook `05 §6` — VEV ships no identity server of its own; adopt an OIDC/SCIM provider), then reads the
tenant, principal and roles from the token's claims. A request without a valid, tenant-bound token is
refused (`401`); only `/health` is exempt. Configuration keys (env form doubles each `:` as `__`):

| Key | Default | Meaning |
|---|---|---|
| `Atlas:Identity:Oidc:Authority` | — (required) | OIDC issuer/authority URL. Without it, the host fails closed. |
| `Atlas:Identity:Oidc:Audience` | — | Expected token audience (client id). When unset, audience is not validated. |
| `Atlas:Identity:Oidc:TenantClaim` | `tenant` | Claim carrying the tenant id. |
| `Atlas:Identity:Oidc:PrincipalClaim` | `sub` | Claim carrying the stable principal id. |
| `Atlas:Identity:Oidc:NameClaim` | `name` | Claim carrying the display name. |
| `Atlas:Identity:Oidc:RolesClaim` | `roles` | Claim carrying role names (may repeat); values are Atlas roles such as `AtlasArchitect`. |
| `Atlas:Identity:Oidc:RequireHttpsMetadata` | `true` | Require HTTPS for provider metadata. Set `false` only for a local HTTP dev provider. |

Your provider must emit a **flat `roles` claim** (an array of Atlas role names) and a **`tenant` claim**.
For Keycloak that means a *realm role* mapper writing to `roles` and a *user attribute* mapper writing to
`tenant` — exactly what the bundled dev realm below sets up.

### Log in with the dev Keycloak

For local end-to-end login, a bundled Keycloak (dev only — **do not ship it to production**) layers on top
of the base compose and switches Atlas to `fabric-oidc`:

```bash
docker compose -f docker-compose.yml -f docker-compose.oidc.yml up --build
#   podman: podman compose -f docker-compose.yml -f docker-compose.oidc.yml up --build
```

This imports the [`keycloak/atlas-realm.json`](../keycloak/atlas-realm.json) realm: a public client
`atlas-api`, a seeded user `architect` / `architect` with role `AtlasArchitect` and tenant `community`, and
the `roles` + `tenant` mappers. Get a token (direct grant) and call the API with it:

```bash
# Fetch an access token for the seeded user (the issuer inside the token is http://keycloak:8080/realms/atlas,
# which is what Atlas validates against inside the compose network).
TOKEN=$(curl -s http://localhost:8081/realms/atlas/protocol/openid-connect/token \
  -d grant_type=password -d client_id=atlas-api \
  -d username=architect -d password=architect | python -c "import sys,json;print(json.load(sys.stdin)['access_token'])")

# Create an asset as the token's tenant (community); no X-* headers are consulted.
curl -X POST http://localhost:8080/api/v1/assets -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"id":"app-1","kind":"application","name":"Checkout","lifecycle":"active"}'

# Without a token, the same call is refused.
curl -i http://localhost:8080/api/v1/assets            # → 401
curl http://localhost:8080/health                       # → {"status":"ok"} (health needs no token)
```

The Keycloak admin console is at `http://localhost:8081` (admin / admin). This is the swap point for
real identity: point `Atlas:Identity:Oidc:Authority` at your own OIDC provider in production and drop the
overlay (handbook `11 §4`).

## Tenant isolation

The catalogue is reconnaissance-grade landscape data, so tenant isolation is enforced by the model,
not by every query remembering a predicate. `AtlasDbContext` puts an **EF Core global query filter**
on each tenant-scoped entity (`assets`, `relationships`), keyed on the ambient request tenant. A query
that forgets an explicit `TenantId` predicate is still scoped to the caller's tenant by default — the
filter never fails open.

The only way past it is EF's explicit `IgnoreQueryFilters()` opt-out. That is reserved for the rare,
legitimate cross-tenant read and must be annotated with a `cross-tenant:` marker; an architecture
fitness test (`ForbiddenPatternTests.Bypassing_the_tenant_filter_requires_an_explicit_audited_opt_out`)
fails the build on any un-annotated use, and `TenantIsolationTests` fails the build if the global
filter itself is removed (atlas#35).

## Encryption at rest

The catalogue is the tenant's whole landscape map — reconnaissance-grade data — and it lives in a
SQLite database file on disk (`atlas.db`, or the `atlas-data` volume under Docker). **Atlas does not
encrypt that file for you.** A self-hoster is responsible for protecting it at rest; the expectation
for any deployment holding real landscape data is that the database file is encrypted at rest by one of
the two approaches below. Do not leave the map in plaintext on a disk you do not fully control.

**Baseline — encrypt the volume/disk (recommended default).** Put `atlas.db` on an encrypted
filesystem or volume and let the platform do the work:

- Linux host: LUKS/dm-crypt on the partition backing the Docker volume, or an encrypted ZFS/LVM dataset.
- Cloud: an encrypted block volume (EBS encryption, GCP PD/CMEK, Azure disk encryption) mounted where
  the `atlas-data` volume lives.
- Point the database at that location with `ConnectionStrings__Atlas` (see [Run with Docker](#run-with-docker)).

Trade-offs: transparent to Atlas (no build or config change, no schema change, full SQLite tooling
still works), and it protects the whole volume — backups, WAL/journal files and temp files included.
But it only protects data at rest against a stolen disk/volume: once the filesystem is mounted and the
process is running, the file is readable by anything on the host, so it pairs with host hardening and
access control rather than replacing them. This is the right default for most self-hosters.

**Stronger — an encrypted database (SQLCipher).** For defence in depth where the host itself is not
fully trusted, back SQLite with [SQLCipher](https://www.zetetic.net/sqlcipher/), which transparently
encrypts the database file with a key the application supplies (via `PRAGMA key`) rather than relying on
the surrounding volume.

Trade-offs: the file is ciphertext even on a mounted, running host, so a copied `atlas.db` is useless
without the key — but you must now manage that key (supply it from a secret store / KMS, never bake it
into the image or Compose file), standard SQLite tools can no longer open the file, and there is a small
crypto overhead per query. It also needs a SQLCipher-capable native SQLite build wired into
`Atlas.Persistence` (e.g. a SQLCipher `SQLitePCLRaw` bundle plus the keyed connection string); that
runtime option is not shipped yet — track it in the backlog if you need it. Choose this when volume
encryption alone does not meet your threat model.

Either way, the same expectation extends to **backups and exports**: an export (`/api/v1/export`) is a
full plaintext copy of the map, so treat exported files with the same care as the database itself.

## Supply chain: SBOM & signed provenance

Atlas is a container image you run next to your landscape map, so you should be able to verify *what*
you are running and *where it came from* — increasingly a regulatory expectation (EU Cyber Resilience
Act). Every tagged release (`v*.*.*`) publishes the image to the GitHub Container Registry with a full
supply-chain trail (atlas#38), built by [`.github/workflows/release.yml`](../.github/workflows/release.yml):

- **Published image:** `ghcr.io/vev-software/atlas-community:<version>`.
- **Signature:** a keyless [cosign](https://docs.sigstore.dev/) signature over the image digest, tied to
  the release workflow's GitHub OIDC identity and recorded in the public Rekor transparency log.
- **Provenance:** a max-mode SLSA build-provenance attestation attached to the image.
- **SBOM:** a CycloneDX SBOM attached to the image as a signed cosign attestation *and* uploaded as an
  asset on the GitHub Release.

### Verify before you run

Verify the signature (the OIDC identity is the release workflow on a version tag):

```bash
IMAGE=ghcr.io/vev-software/atlas-community:<version>
cosign verify "$IMAGE" \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github.com/Vev-software/atlas-community/\.github/workflows/release\.yml@refs/tags/v'
```

Verify — and print — the CycloneDX SBOM attestation:

```bash
cosign verify-attestation --type cyclonedx "$IMAGE" \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github.com/Vev-software/atlas-community/\.github/workflows/release\.yml@refs/tags/v'
```

Inspect the SLSA provenance (or fetch the SBOM from the release page):

```bash
cosign download attestation "$IMAGE" | jq .   # provenance + sbom predicates
```

An **unsigned image, a tampered image, or one built by anything other than this release workflow** has
no matching signature in the transparency log for that identity, so `cosign verify` fails — do not run
it. Pin deployments to an immutable digest (`ghcr.io/vev-software/atlas-community@sha256:…`), not just a
moving tag, once you have verified it.
