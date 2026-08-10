# ADR 0002 — Open-core repository topology: public Community, separate proprietary Enterprise

- Status: Accepted
- Date: 2026-08-09
- Deciders: atlas-maintainers
- Refines: [ADR 0001](./0001-community-edition-license-agpl.md)

## Context

ADR 0001 fixed the Community runtime as AGPL-3.0 and described the free/paid line at a product
level. This follow-up records the repository and licence boundary more precisely so the public
Community codebase stays clear about what belongs here and what does not.

- **Community is public AGPL software.** It stands on its own as the free, self-hostable
  catalogue product.
- **Enterprise is proprietary.** Paid capabilities are developed and distributed separately from
  the public Community repository.
- **Shared contracts stay permissive.** Public interop and substrate contracts remain the boundary
  both products build on.

## Decision

1. **Community is the free public product.** `atlas-community` (AGPL-3.0) contains the
   self-hostable catalogue runtime and public extension points only.

2. **Enterprise is a separate proprietary product.** Paid capabilities live outside the public
   Community repository and are not shipped as open-source Community code.

3. **The shared foundation is the permissive contract surface.** Both products build on
   `atlas-contracts` (Apache-2.0) and the Fabric contracts, not on AGPL Community internals.

4. **The public repo must not become a product-protection boundary.** Community may expose product
   seams and public contracts, but implementation details of proprietary distribution and control
   stay outside this repository.

## Rationale

- Separating Community and Enterprise keeps the AGPL boundary clean and avoids ambiguity about what
  is public product code versus proprietary product code.
- Building both products on the permissive contracts resolves the AGPL-vs-proprietary conflict
  without relicensing Community, keeping AGPL's defensive value.
- Keeping proprietary implementation details out of the public repo reduces unnecessary disclosure
  about commercial-product internals while leaving the public architecture understandable.

## Consequences

- Moving from Community to Enterprise is a product-boundary change, not a relicensing of the public
  Community runtime.
- Community stays focused on the public catalogue runtime, public contracts and public extension
  seams.
- Public documentation should explain the repository boundary clearly without documenting
  proprietary-product control paths or internal commercial topology.
