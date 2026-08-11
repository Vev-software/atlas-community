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
curl http://localhost:8080/api/v1/assets -H "X-Tenant-Id: demo"
# The landscape UI: open http://localhost:8080/ in a browser.
```

The catalogue is stored in SQLite on the `atlas-data` volume (`/data/atlas.db` in the container), so
it survives `docker compose down` / restarts. Remove it with `docker compose down -v`.

To build or run the image directly (note the `..` context — the monorepo root):

```bash
docker build -f Dockerfile -t atlas-community:local ..
docker run --rm -p 8080:8080 -v atlas-data:/data atlas-community:local
```

The image runs as a non-root user, listens on port 8080, and carries a `HEALTHCHECK` that polls
`/health`. Configuration is standard ASP.NET Core — override the database location with
`ConnectionStrings__Atlas`, e.g. `-e ConnectionStrings__Atlas="Data Source=/data/atlas.db"`.

## Dev request context (temporary)

Real identity/tenancy comes from Fabric (fabric#3). Until then the `X-Tenant-Id`, `X-Principal-Id`
and `X-Principal-Roles` headers bind the ambient context, defaulting to a single dev tenant with the
`AtlasArchitect` role. This is the swap point: when `Vev.Fabric.*` lands, replace `Atlas.Fabric.Dev`
and the request-context middleware with the Fabric-provided authentication (handbook `11 §4`).

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
