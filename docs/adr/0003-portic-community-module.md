# ADR 0003 — Opt-in Portic AI provider extension module

- Status: Accepted
- Date: 2026-08-18
- Deciders: atlas-maintainers
- Refines: [ADR 0001](./0001-community-edition-license-agpl.md), [ADR 0002](./0002-open-core-topology.md)

## Context

Atlas Community Edition exposes a provider-neutral AI seam (`IAiAssistService`) behind the Fabric
contract. The built-in `CommunityAiAssistService` routes to OpenAI or Anthropic via raw `HttpClient`
calls — no provider SDK packages, keeping the dependency surface minimal. The provider list
(`AiProviders.All`) was hard-coded, and the `CommunityAiAssistService` switch statement grew with
every new provider.

A third-party AI gateway (Portic) needs to be available as a Community provider option. The
requirement is that the integration is **opt-in**: a self-hoster who does not register the Portic
module sees zero behavioural change and zero new dependencies.

## Decision

1. **Introduce `IAiProviderExtension` in the Fabric abstractions.** A lightweight interface carrying
   a stable provider id string and an `Assist()` method that accepts an `AiAssistRequest` and
   returns an `AiAssistResult`. This is the extension point — new providers implement the interface
   and register themselves via DI.

2. **`AiProviderExtensions` static registry.** A helper that discovers registered extensions from the
   service provider and builds a lookup from provider id to extension instance. The built-in
   OpenAI/Anthropic providers remain inline in `CommunityAiAssistService` for backward compatibility;
   extensions are consulted when the configured provider id does not match a built-in.

3. **Dynamic provider validation.** `AiModuleService.NormalizeProvider` and `AiProviders.All` now
   accept an optional `IEnumerable<IAiProviderExtension>` to include extension provider ids in the
   validation set. The hard-coded list becomes a baseline; extensions are layered on top at runtime.

4. **Isolated `Atlas.Fabric.Portic` project.** A standalone project implementing `IAiProviderExtension`
   for the Portic gateway. It speaks only `HttpClient` + JSON (no SDK), reads configuration from
   `Atlas:Portic` (`BaseUrl`, `Model`, `MaxTokens`), and exposes an opt-in extension method
   `builder.Services.AddPorticAiProvider()`. Without that call, Portic is invisible to the runtime.

5. **No core dependency on Portic.** The `Atlas.Fabric.Portic` project references only
   `Atlas.Fabric.Abstractions`. No domain, persistence, or API project depends on it. Registration
   happens in `Program.cs` or a composition root, not in `AtlasCommunityRegistration`.

## Rationale

- **Opt-in isolation.** The module adds zero runtime cost when not registered. A self-hoster who
  never calls `AddPorticAiProvider()` has the exact same behaviour as before — the same provider
  list, the same routing logic, the same dependency footprint.
- **Extension over modification.** New providers plug in through `IAiProviderExtension` rather than
  requiring changes to `CommunityAiAssistService`. The built-in switch statement shrinks to two
  cases; everything else is routed through the extension lookup.
- **AGPL boundary.** `Atlas.Fabric.Portic` is AGPL-3.0 like the rest of Community. The Portic
  `BaseUrl` comes exclusively from configuration — no hard-coded endpoints in core or domain code.
- **Consistent with existing patterns.** The portability format-adapter seam (exporters/importers)
  uses the same extension-model pattern: register implementations via DI, discover at runtime, no
  core changes required.

## Consequences

- **New provider support is additive.** A community contributor can add a new provider by creating
  a project that implements `IAiProviderExtension` and registers it — no core code changes.
- **`AiProviders.All` is no longer a compile-time constant.** It is computed at runtime from the
  built-in ids plus any registered extensions. Code that relied on it being a static `readonly`
  list must go through the service provider or accept the dynamic list.
- **Threat model — data egress.** When Portic is enabled, all AI-assist payloads (grounding facts,
  user questions, optional attachments) are sent to the configured `Atlas:Portic:BaseUrl`. The
  self-hoster is responsible for ensuring that endpoint meets their data-residency and privacy
  requirements. The `BaseUrl` is never hard-coded; it is always operator-supplied configuration.
  See `docs/threat-model.md` for the consolidated egress discussion.

## Threat model note: data egress

Enabling the Portic provider introduces a configurable data-egress path:

- **What leaves.** Every `AiAssistRequest` payload — the grounding context (landscape facts), the
  user's question, and any optional attachments — is serialized and sent to the Portic gateway
  configured in `Atlas:Portic:BaseUrl`.
- **Configuration control.** The `BaseUrl` is always operator-supplied. Atlas never hard-codes a
  Portic endpoint. The self-hoster decides where data flows.
- **Encryption.** All traffic uses HTTPS. The `HttpClient` inherits the default certificate
  validation behaviour.
- **Opt-in.** The Portic provider is invisible and inactive unless `AddPorticAiProvider()` is called
  during service registration. A deployment that does not register it has no Portic egress path.
- **Operator responsibility.** If the self-hoster's data-residency policy prohibits sending
  landscape data to an external gateway, they simply do not register the Portic provider.
