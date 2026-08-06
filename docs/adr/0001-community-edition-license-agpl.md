# ADR 0001 — Atlas Community Edition runtime is AGPL-3.0

- Status: Accepted
- Date: 2026-08-05
- Deciders: atlas-maintainers

## Context

Atlas is run as **open core** (handbook `00 §2`, `02 §5`, `11`). The free Community Edition
(`atlas-community`, asset management) is the public adoption hook; the commercial capabilities that
*work with* the data are private and entitlement-gated. The repository-strategy licence matrix
(`02 §3`, `AGENTS.md §2`) requires an explicit, ADR-recorded licence decision for a **product
runtime** — it must never be a default drift.

## Decision

- The **Atlas Community Edition runtime** in this repository is licensed **AGPL-3.0**.
- The public **data model and interop contracts** live in the separate `atlas-contracts` repository
  under **Apache-2.0** — the runtime *consumes* those contracts; it does not define them here.
- Contributions require a **CLA** (AGPL/BSL runtimes require one — `17 §3`, `02 §4`), enforced in CI
  before feature merges.

## Rationale

- AGPL keeps the free runtime genuinely open and self-hostable while protecting against a hyperscaler
  hosting it as a competing service without reciprocity — a named failure mode (`00 §5`).
- Apache-2.0 on the contracts maximises interoperability: third-party importers/exporters and the
  Fabric platform build against the schemas without touching the AGPL runtime.
- The free/paid line is **entitlement data, not a licence fork** (`09 §3`, `11 §4`): the same binary
  becomes the commercial product when an entitlement grants the paid capabilities.

## Consequences

- Every source file in this repo is AGPL-3.0; the commercial core lives in a separate private repo.
- A public build must never require a private feed (`AGENTS.md §1.9`); the only external contract
  dependency, `atlas-contracts`, is public Apache-2.0.
- Breaking a published contract is an `atlas-contracts` concern requiring its own ADR + migration.
