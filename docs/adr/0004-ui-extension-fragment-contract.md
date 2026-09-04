# ADR 0004 — Versioned fragment-only UI extension mount contract

- Status: Accepted
- Date: 2026-09-04
- Deciders: atlas-maintainers
- Refines: [ADR 0002](./0002-open-core-topology.md)

## Context

Atlas Community already ships a host slot and `GET /api/v1/extensions/ui` for entitlement-gated
UI extensions. The first end-to-end proof uses a separately delivered panel fragment, but the
public host contract is still too implicit:

- the response shape is not explicitly versioned
- the mount shape is inferred from a `fragmentUrl` field rather than named as a contract
- compatibility behavior is undocumented when host and extension expectations drift

The repository rules are already clear on the architectural boundary: Community exposes public,
typed extension seams, not a universal plug-in host, and it must fail closed when a shape is not
explicitly supported.

## Decision

1. **`GET /api/v1/extensions/ui` is a versioned host-offer contract.** The endpoint returns a top-level
   `contractVersion` plus `extensions`.

2. **Each offered extension carries a typed `mount` object.** In V1 the public shape is:
   `id`, `slot`, `title`, and `mount`.

3. **The first supported mount posture remains fragment-only.** V1 supports one mount kind only:
   `mount.kind = "fragment"`, rendered by the Community client inside a sandboxed iframe. There is
   no in-process module path, no bundle loader, and no host-side script bridge in this contract.

4. **Compatibility is exact-match and fail-closed.** The host offers only mount kinds and
   `mount.contractVersion` values it explicitly supports, and the browser mounts only the same
   known pairs. Unknown kinds or unknown versions are omitted server-side and ignored client-side.

5. **Unconfigured content stays representable without widening the contract.** An entitled extension
   may still be listed with `mount.url = null`, which means the deployment has not configured a
   fragment source yet.

## Rationale

- A typed `mount` object keeps the contract small while leaving room for future, explicitly named
  mount shapes without pretending Community is a generic plug-in runtime.
- Keeping V1 fragment-only matches what is already proven and avoids committing to a broader loader
  or local-composition story before there is a concrete implementation path.
- Exact-match compatibility is the safest first rule. A host that does not know a shape or version
  must not guess; it should act as though no mount is available.

## Consequences

- Consumers of `GET /api/v1/extensions/ui` must read `contractVersion` and `mount.kind` /
  `mount.contractVersion`, not infer the mount behavior from ad hoc fields.
- A future mount kind or a breaking change to the fragment contract requires a new ADR and a new
  version rather than silently widening the current one.
- The current response remains deliberately small: mount metadata only, never non-entitled
  capability details or delivery internals.

## Alternatives considered

- **Keep the flat `fragmentUrl` field.** Rejected because it bakes the first delivery shape into the
  top-level object and leaves no explicit compatibility rule.
- **Add a general plug-in or bundle-loading framework now.** Rejected because it widens the
  extension model far beyond the need proven so far and violates the closed-set extension posture.
- **Support a composed/module path in the first public contract.** Rejected because there is no
  concrete, agreed implementation or testable host behavior for that posture yet.

## Compliance

- Upholds `engineering/AGENTS.md` §1.2 and §1.7: depend on published contracts, not another
  product's internals, and keep extensibility as a small closed set of typed extension points.
- Upholds the public-repo documentation rule in `engineering/AGENTS.md` §4: the contract change is
  recorded with docs and compatibility notes alongside the code.
