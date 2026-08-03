# Atlas

**A living map of your architecture, always current.**

Atlas is VEV's enterprise-architecture platform, run as open core. It catalogues the systems,
applications, servers and infrastructure you run and keeps that picture current — then turns the
catalogue into insight: integration mapping, end-of-life intelligence, application-portfolio
management, roadmapping and AI-assisted architecture review.

The free Community Edition is deliberately simple — it holds and browses your landscape, which is
genuinely useful on its own. The commercial capabilities that *work with* that data are part of
the same product and unlock through entitlements, so there is no second install and no migration
when you grow into them. Atlas is opinionated and, in its commercial form, arrives populated via
discovery rather than handing you an empty canvas to fill in. It uses AI where AI earns its
place, and never becomes "an AI product."

> **Status: early.** This repository is being set up. What follows describes the shape Atlas is
> built toward; treat anything not yet present in the source tree as intended direction rather
> than a shipped feature.

## What you get

**Community Edition (free, self-hostable)** — asset management:

- An asset repository for systems, applications, servers and infrastructure — create, edit, hold
  and browse.
- Basic visualisation of the landscape you hold.
- Manual relationships and tags on assets.

**Commercial capabilities (entitlement-unlocked, same codebase)** — working with the data:

- **Integration mapping** across REST, file, Kafka and legacy links, with ownership and
  criticality.
- **End-of-life intelligence** from OS through application server, with the associated risk.
- **Application-portfolio management** — a business-value × technical-quality view with
  rationalisation proposals.
- **Roadmapping** and **AI-assisted architecture review**, generated from your data through a
  provider-neutral AI contract and always run through a draft → validate → approve → publish
  lifecycle — never auto-applied.

The free/paid boundary is entitlement data, not a code fork: the same binary becomes the
commercial product when the entitlement grants the additional capabilities.

## Interoperability & portability

Your data is yours, and the map is meant to interoperate with the tools you already own:

- **`atlas-contracts`** — the public data model and import/export schemas.
- **Community modules** — open format adapters such as ArchiMate import/export, BPMN import, and
  report exporters, built against the public contracts.
- **Customer-owned data export** — a portability guarantee, not an afterthought.

## How Atlas is built

- **Standalone and independently deployable.** Atlas solves a real problem on its own, with no
  hard runtime dependency on any other VEV product.
- **On the shared substrate.** Identity, tenancy, RBAC, audit, telemetry, configuration and
  entitlements come from VEV's shared substrate (Fabric) through explicit, versioned contracts —
  never through another product's internals. Atlas owns the enterprise-architecture domain; the
  substrate owns the cross-cutting concerns.
- **AI-native, never vendor-bound.** AI review and roadmapping go through a provider-neutral AI
  contract, so the capability is permanent and the provider behind it is disposable. Routing
  Atlas's AI through VEV's [Portic](https://github.com/Vev-software/Portic) gateway is an
  optional integration, never a requirement.
- **API and SDK first.** The UI orchestrates the API; it is never the only way in.

## Deployment

The same product runs in three modes:

- **Self-hosted (Community)** — you run it, no VEV cloud dependency.
- **Enterprise self-hosted** — adds governance and signed entitlement snapshots.
- **VEV-hosted (managed)** — VEV operates it; you own your data; EU-resident.

## Repository layout

This repository currently contains project scaffolding (license, ignore rules, CI). Source,
build and run instructions will land here as the product takes shape, at which point this
section becomes a real quickstart rather than a placeholder.

## License

This repository — the Atlas **Community Edition** — is licensed under the **GNU Affero General
Public License v3.0** (AGPL-3.0); see [LICENSE](./LICENSE).

Atlas is open core: the Community Edition here is free and open source, while the commercial
capabilities are separately licensed and unlocked through entitlements. The public data-model
and import/export contracts are published separately under the Apache License 2.0.

---

© VEV Software ApS.
