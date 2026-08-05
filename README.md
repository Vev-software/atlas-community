# Atlas · Community Edition

**A living map of your architecture, always current.**

Atlas is VEV's enterprise-architecture platform, run as open core. This repository is the
free, self-hostable **Community Edition** (`atlas-community`, AGPL-3.0): it catalogues the
systems, applications, servers and infrastructure you run and keeps that picture current.
The paid capabilities that *work with* that data live in **Atlas Enterprise**
(`atlas-enterprise`, private) and compose onto this runtime through entitlements.

> **Status: early.** This repository is being set up. What follows describes the shape Atlas
> is built toward; treat anything not yet present in the source tree as intended direction
> rather than a shipped feature.

## What you get (free, self-hostable)

Asset management — a genuinely useful place to hold your landscape, not crippled shareware:

- An asset repository for systems, applications, servers and infrastructure — create, edit,
  hold and browse.
- Basic visualisation of the landscape you hold.
- Manual relationships and tags on assets.
- Customer-owned data export — a portability guarantee, not an afterthought.

## Growing into Atlas Enterprise

The moment you want more than a catalogue — integration mapping, end-of-life intelligence,
application-portfolio management, roadmapping, AI-assisted architecture review, plus
discovery ingestion, enterprise connectors, governance and hosting — those are paid
capabilities in **[`atlas-enterprise`](https://github.com/Vev-software/atlas-enterprise)**
(private). They ship as private modules that compose onto this Community runtime and unlock
through Fabric entitlements, so growing into them is **a switch, not a migration** — no
second install, no data move.

## Interoperability & portability

Your data is yours, and the map is meant to interoperate with the tools you already own:

- **[`atlas-contracts`](https://github.com/Vev-software/atlas-contracts)** — the public data
  model and import/export schemas (Apache-2.0).
- **Community modules** — open format adapters such as ArchiMate import/export, BPMN import
  and report exporters, built against the public contracts.

## How Atlas is built

- **Standalone and independently deployable.** Atlas solves a real problem on its own, with
  no hard runtime dependency on any other VEV product.
- **On the shared substrate.** Identity, tenancy, RBAC, audit, telemetry, configuration and
  entitlements come from VEV's shared substrate (Fabric) through explicit, versioned
  contracts — never through another product's internals. Atlas owns the
  enterprise-architecture domain; the substrate owns the cross-cutting concerns.
- **AI-native, never vendor-bound.** AI features go through a provider-neutral AI contract,
  so the capability is permanent and the provider behind it is disposable. Routing Atlas's
  AI through VEV's [Portic](https://github.com/Vev-software/portic-community) gateway is an
  optional integration, never a requirement.
- **API and SDK first.** The UI orchestrates the API; it is never the only way in.

## Repository layout

This repository currently contains project scaffolding (license, ignore rules, CI). Source,
build and run instructions will land here as the product takes shape, at which point this
section becomes a real quickstart rather than a placeholder.

## License

This repository — the Atlas **Community Edition** — is licensed under the **GNU Affero
General Public License v3.0** (AGPL-3.0); see [LICENSE](./LICENSE).

Atlas is open core: the Community Edition here is free and open source, while the Enterprise
capabilities (`atlas-enterprise`) are separately licensed and unlocked through entitlements.
The public data-model and import/export contracts are published separately under the Apache
License 2.0.

---

© VEV Software ApS.
