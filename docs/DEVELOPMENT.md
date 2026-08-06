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

## Dev request context (temporary)

Real identity/tenancy comes from Fabric (fabric#3). Until then the `X-Tenant-Id`, `X-Principal-Id`
and `X-Principal-Roles` headers bind the ambient context, defaulting to a single dev tenant with the
`AtlasArchitect` role. This is the swap point: when `Vev.Fabric.*` lands, replace `Atlas.Fabric.Dev`
and the request-context middleware with the Fabric-provided authentication (handbook `11 §4`).
