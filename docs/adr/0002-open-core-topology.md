# ADR 0002 — Open-core topology: free Community, proprietary Enterprise, shared permissive contracts

- Status: Accepted
- Date: 2026-08-09
- Deciders: atlas-maintainers
- Refines: [ADR 0001](./0001-community-edition-license-agpl.md) — the "same binary becomes the commercial product" rationale

## Context

ADR 0001 fixed the Community runtime as AGPL-3.0 and framed the free/paid line as "entitlement
data, not a licence fork," adding that "the same binary becomes the commercial product when an
entitlement grants the paid capabilities." Designing how a paid capability — and a time-limited
**Enterprise trial** — actually reaches a customer surfaced a licence-topology question that
"same binary + entitlement flip" leaves unresolved:

- **AGPL is copyleft.** Proprietary Enterprise code cannot statically build on AGPL Community
  internals without itself becoming AGPL. So "the same binary" cannot literally be both the free
  AGPL product and a closed commercial one.
- **An open-source gate is not a protection boundary.** A licence/entitlement *verifier* placed
  inside the public Community codebase is inspectable and patchable on hardware the customer
  controls.
- **A self-hoster of free Community holds no VEV licence and needs none** — there is nothing to
  verify.

## Decision

1. **Community is the free product and carries no licence logic.** `atlas-community` (AGPL-3.0) is
   a minimal, self-hostable catalogue that performs **no licence verification**. Its
   paid-capability seam is a *static deny-with-upsell*: paid capabilities always return `402` with
   a reason code and an upgrade hint — which `CommunityEntitlementService.Community` (an empty grant
   set) already does. No signed entitlement and no verifier ship in the open repo.

2. **Enterprise is a separate proprietary product.** The paid capabilities live in a closed
   `atlas-enterprise` distribution that is not open source. It is obtained under access control and
   enforces licensing either (a) as **SaaS**, VEV-hosted, or (b) **self-hosted Enterprise** talking
   to a VEV **licence server**.

3. **The shared foundation is the *permissive* contracts, never the AGPL Community internals.** Both
   products build on `atlas-contracts` (Apache-2.0) and the Fabric contracts. Enterprise owns its
   runtime shell on that permissive base and never links AGPL Community code.

4. **Licence/entitlement verification lives only in Enterprise/Fabric**, never in the open Community
   codebase.

## Rationale

- Nothing in Community is protected, so nothing there can be "cracked" — removing the gate is a
  stronger position than hardening an inspectable one.
- The paid code is never shipped in the open, and for SaaS never shipped at all, so the "extract the
  paid features from source" attack surface is zero.
- Building both products on the permissive contracts resolves the AGPL-vs-proprietary conflict
  **without relicensing Community**, keeping AGPL's defensive value (ADR 0001).
- Honest security posture: **SaaS is genuinely unhackable** (code never leaves VEV);
  **self-hosted Enterprise is hardened, not cryptographically unbreakable** — the standard
  commercial-software position (raise the crack cost above the value; periodic signed phone-home;
  node-locking; backed by licence terms).

## Consequences

- **"Switch, not a migration" (ADR 0001 / README) becomes data compatibility, not an in-process
  flip.** Moving from Community to self-hosted Enterprise means deploying the Enterprise artifact
  against the *same catalogue data* — preserved by the shared schema and the import/export
  portability surface (#12) — not flipping an entitlement inside one running AGPL process. This
  refines ADR 0001's "same binary" wording.
- Community stays trivial: **no module loader, no signed private feed, no verifier**. The
  plugin-fetch machinery considered during design is unnecessary under this topology.
- The existing `PaidCapabilityGate` / `IEntitlementService` in Community remain valid **as a static
  deny-and-upsell breadcrumb** and must not grow into licence verification.
- **Trials prefer the hosted path**: a SaaS trial tenant that expires (clean, unhackable). A
  self-hosted Enterprise trial is a time-bound licence enforced by the licence server (hardened,
  not unbreakable).
- Enterprise owns (and partly duplicates) its runtime shell on the permissive core; this cost is
  accepted deliberately as the price of clean licence separation.
- The Community fitness rule stands: the free/paid line is entitlement **data**, never
  `if (plan == …)`.
