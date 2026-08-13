# Atlas Community Edition — development

Early scaffolding for the free, self-hostable **asset-management** edition (handbook `12 §Phase 0`,
`11`). Catalogue your systems, applications, servers, infrastructure and data layer, plus manual
relationships, join keys and tags. Analysis and other paid Atlas capabilities live outside this
public repository.

## Layout

```
src/
  Atlas.Fabric.Abstractions  Fabric contract shim: tenant/principal, authz, audit, entitlements
                             (shaped to match the forthcoming Vev.Fabric.* packages — fabric#3-7)
  Atlas.Fabric.Dev           Dev implementations of the shim (single tenant, role-gated authz,
                             fail-static community entitlements, in-memory audit)
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
- The public `Vev.Atlas.Contracts` package. Until it is published to nuget.org, build it from the
  sibling `atlas-contracts` repo into the shared local feed:

  ```bash
  dotnet pack ../atlas-contracts/sdk/dotnet/Vev.Atlas.Contracts/Vev.Atlas.Contracts.csproj \
    -c Release -o ../.local-nuget
  ```

  `nuget.config` maps only `Vev.Atlas.*` to that folder; everything else comes from nuget.org, so the
  public-build rule (`AGENTS.md §1.9`) still holds.

## Build, test, run

```bash
dotnet test
dotnet run --project src/Atlas.Api
```

The API creates its SQLite schema on first run. OpenAPI is at `/openapi/v1.json`; health at `/health`.
The catalogue file holds reconnaissance-grade landscape data — for any real deployment, encrypt it at
rest ([Encryption at rest](#encryption-at-rest)).

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
read-only landscape UI and a persistent SQLite database with one command. Compose builds with the
**monorepo root as the build context** so the temporary sibling contracts feed (`.local-nuget`) is
available to the build — the same reconstruction CI does. Once `Vev.Atlas.Contracts` is on nuget.org
(`atlas#10`), the context can narrow to this repo and the `.local-nuget` copy in the `Dockerfile`
drops out.

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

To build or run the image directly (note the `..` context — the monorepo root):

```bash
docker build -f Dockerfile -t atlas-community:local ..
docker run --rm -p 8080:8080 -v atlas-data:/data atlas-community:local
```

The image runs as a non-root user, listens on port 8080, and carries a `HEALTHCHECK` that polls
`/health`. Configuration is standard ASP.NET Core — override the database location with
`ConnectionStrings__Atlas`, e.g. `-e ConnectionStrings__Atlas="Data Source=/data/atlas.db"`.

## Identity & tenancy

Atlas holds reconnaissance-grade landscape data, so request identity must come from a trustworthy
source, never be asserted by the caller. Full multi-tenant identity is Fabric OIDC (fabric#3); until it
lands, `Atlas:Identity:Mode` (env: `Atlas__Identity__Mode`) selects one of three modes, and Atlas
**fails closed** when no trustworthy source is available (atlas#34):

| Mode | Identity source | Where it's allowed |
|---|---|---|
| `dev-headers` | `X-Tenant-Id` / `X-Principal-Id` / `X-Principal-Roles` request headers, defaulting to a single dev tenant with the `AtlasArchitect` role | **Development environment only** |
| `single-tenant` | A fixed tenant + roles from config (`Atlas:Identity:Tenant`, default `community`; `Atlas:Identity:Principal`, default `self-host`; `Atlas:Identity:Roles`, default `AtlasArchitect`). `X-*` headers are ignored, so no caller can name another tenant or escalate roles. | Any environment — this is the self-host default |
| `fabric-oidc` | A verified Fabric OIDC token (fabric#3) | Any environment once the provider is wired |

When the mode is unset it defaults from the environment: **Development → `dev-headers`**; any other
environment → **`fabric-oidc`**, which — since that provider is not wired yet — makes the host **refuse
to start** rather than silently trust request headers. The self-hosted container therefore sets
`single-tenant` explicitly (see the `Dockerfile`). The header shim (`dev-headers`) can never be forced
on outside Development: the host refuses to start if you try.

This is the swap point for real identity: when `Vev.Fabric.*` lands, `fabric-oidc` resolves the same
tenant + principal context from OIDC and becomes the non-development default (handbook `11 §4`).

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
