# Atlas Community — security posture

A concise, product-scoped **threat-model note** and **compatibility statement** for the Atlas Community
runtime (handbook `15 §6`). This is the Community edition's safety posture, not enterprise compliance
collateral. Reporting a vulnerability follows the private disclosure path in
[`SECURITY.md`](../SECURITY.md) — never a public issue.

## What Atlas Community holds

The catalogue is a tenant's landscape map — systems, applications, servers, infrastructure and the data
layer down to individual columns, plus the relationships between them. That is **reconnaissance-grade
data**: it does not contain credentials or customer records, but it tells an attacker how the estate is
built and where to look. The security goal is the **confidentiality and integrity of that map**, and a
**trustworthy audit trail** of who changed or exported it.

## Trust boundaries

- **Caller → API.** Requests are not trusted to name their own tenant or roles; identity is established
  by the configured identity mode, and every write and every export is authorized.
- **API → Fabric substrate.** Identity, authorization, audit and entitlements are consumed through the
  Fabric contracts. In Community these run as a local shim (`Atlas.Fabric.Dev`) until the real
  `Vev.Fabric.*` services land (`fabric#3`–`#7`); the seam, not the shim, is the contract.
- **Runtime → disk.** The whole map persists to a SQLite file on a mounted volume. The file is a
  reconnaissance-grade artifact at rest.
- **Image → operator.** The self-hoster runs a container they did not build; they need to verify what it
  is before trusting it with the map.

## Controls

What defends the map today, and where each control lives:

| Threat | Control | Reference |
|---|---|---|
| A caller asserts a tenant or role it does not hold | Identity modes: the header shim is **Development-only**; self-host uses a fixed **single-tenant** identity (headers ignored); any non-dev host without a real provider **fails closed** (refuses to start) | `atlas#34` · [Identity & tenancy](./DEVELOPMENT.md#identity--tenancy) |
| One tenant reads another tenant's map | **EF Core global query filter** keyed on the ambient tenant — isolation holds by default even if a query forgets the predicate; a fitness test fails the build if the filter is removed, and the only bypass is an audited `cross-tenant:` opt-out | `atlas#35` · [Tenant isolation](./DEVELOPMENT.md#tenant-isolation) |
| Silent bulk exfiltration of the whole map | `/export` is **hardened**: an elevated `atlas.landscape.export` authorization (read-only customers denied `403`), exactly one audit record per export, and per-tenant **rate limiting** | `atlas#36` · [Portability](./DEVELOPMENT.md#portability-export--import) |
| No record of who changed or read what | An append-only **audit envelope** on every write and every export (actor, tenant, action, resource, time), with no secrets or customer content in it | `fabric#6` |
| A paid capability is reached in the free edition | The **entitlement seam** denies reserved paid capabilities in Community (fail-static), so the free/paid line is data, not code — enforced by a fitness test that forbids `if (plan == …)` | `atlas#8` |
| The map is read from a stolen disk or volume | Documented **encryption-at-rest** expectation (encrypt the volume, or an encrypted-database option), plus the same care for exports and backups | `atlas#37` · [Encryption at rest](./DEVELOPMENT.md#encryption-at-rest) |
| Running a tampered or unknown image | Tagged releases publish a **signed** image with an **SBOM** and **SLSA provenance**; an unsigned or tampered image fails `cosign verify` | `atlas#38` · [Supply chain](./DEVELOPMENT.md#supply-chain-sbom--signed-provenance) |
| Architecture erosion (a boundary or provider leak) | **Architecture fitness tests** fail the build on a dependency-direction violation, a `plan ==` check, or a direct AI-provider call | `Atlas.Architecture.Tests` |

## Assumptions & residual risks

Scoped to the Community, self-hosted deployment:

- **The operator owns the host and network perimeter.** Atlas does not provide its own network
  isolation, WAF or TLS termination; run it behind your own ingress and access control.
- **Single-tenant self-host.** The container serves one fixed tenant; it is not a multi-tenant SaaS.
  Real multi-tenant identity (OIDC, per-request tenants) is Fabric OIDC (`fabric#3`), not yet wired — a
  non-development host without it fails closed rather than trusting headers.
- **The database file is not encrypted by Atlas itself** by default; encryption at rest is the
  operator's responsibility (see the control above).
- **The development identity shim trusts request headers** and is therefore restricted to the
  Development environment; it cannot be forced on elsewhere.
- **Secrets and customer content are kept out of logs and audit by design** (`E4/E5`), but exported
  documents and backups are full plaintext copies of the map and must be protected in kind.
- **No AI or external-provider calls exist in Community**, so there is no prompt-injection or
  data-egress-to-a-model surface in this edition.

## Compatibility statement

**Supported runtime**

- .NET 10 / ASP.NET Core (minimal API), pinned by `global.json`.
- SQLite persistence for self-hosted Community (the `atlas-data` volume).
- A Linux container image, `ghcr.io/vev-software/atlas-community`, published per tagged release.

**Deployment posture**

- **Self-hosted, single-tenant** via the container (single-tenant identity mode) — the supported
  production posture for this edition.
- **Local development** via `dotnet run` (Development-only header identity shim).
- A non-development host without a configured identity provider **fails closed** (`atlas#34`).

**Contract compatibility**

- Import/export speak the public **`atlas-contracts` major version 1** (`contractVersion: "1"`). Within a
  major version the schema only grows in backward-compatible ways; a breaking change is a new major
  version, gated by an ADR + migration in `atlas-contracts`. Details in
  [Compatibility & versioning](./DEVELOPMENT.md#compatibility--versioning).

**Support & maintenance expectations**

- Community is provided as-is under **AGPL-3.0**. Security issues follow the private disclosure path in
  [`SECURITY.md`](../SECURITY.md).
- The controls above are enforced by tests that **fail the build**, so a regression in tenant isolation,
  the paid/free line, or the architecture boundaries is caught in CI rather than shipped.
