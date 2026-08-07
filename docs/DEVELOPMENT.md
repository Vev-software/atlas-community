# Atlas Community Edition — development

Early scaffolding for the free, self-hostable **asset-management** edition (handbook `12 §Phase 0`,
`11`). Catalogue your systems, applications, servers and infrastructure, plus manual relationships
and tags. Analysis (integration mapping, EOL, APM, roadmap, AI review) is **paid Atlas core** and
lives elsewhere; here those seams exist but are entitlement-denied.

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

# A paid capability is entitlement-denied in Community (402 + reason code)
curl http://localhost:5199/api/v1/assets/app-1/integration-mapping -H "X-Tenant-Id: demo"
```

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
