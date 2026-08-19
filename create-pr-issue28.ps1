# Create a PR for issue #28: Chat with your landscape — conversational Q&A over the Atlas catalogue
# Usage: ./create-pr-issue28.ps1

param(
    [string]$BranchName = "issue/28-landscape-chat",
    [string]$RemoteName = "origin"
)

$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot
try {
    # Ensure we're on main and up to date
    git checkout main
    git pull $RemoteName main

    # Create feature branch
    git checkout -b $BranchName

    # Commit changes
    git add docs/DEVELOPMENT.md docs/threat-model.md src/Atlas.Domain/LandscapeChatService.cs src/Atlas.Api/AssetEndpoints.cs tests/Atlas.Api.Tests/AiModuleTests.cs tests/Atlas.Api.Tests/PorticProviderTests.cs

    git commit -m "feat: enforce AI allowance for landscape chat (issue #28)

Issue #28: Chat with your landscape — conversational Q&A over the Atlas catalogue.

Changes:
- LandscapeChatService now checks AiAllowanceService for atlas.ai.chat before answering
- LandscapeChatReply gains AllowanceDenied status for exhausted allowance
- docs/DEVELOPMENT.md adds chat endpoint documentation with curl examples
- docs/threat-model.md corrects AI surface area assumption and adds chat controls
- Test factories updated to provide unlimited chat allowance for test scenarios

Acceptance criteria met:
- Grounded in tenant catalogue; no cross-tenant leakage (EF Core global filter)
- Runs through Fabric AI contract; degrades to setup-required when no provider
- Entitlement-gated and metered under atlas.ai.chat
- Prompt/response content not logged by default
- Read-only: never mutates the catalogue"

    # Push and create PR
    git push $RemoteName $BranchName --set-upstream

    $PrUrl = gh pr create `
        --repo Vev-software/atlas-community `
        --base main `
        --head $BranchName `
        --title "feat: enforce AI allowance for landscape chat (issue #28)" `
        --body @"
## Summary

Issue #28: Chat with your landscape — conversational Q&A over the Atlas catalogue.

This completes the implementation of the grounded read-only chat surface over the tenant's own landscape.

## Changes

- **LandscapeChatService** — Now checks `AiAllowanceService` for `atlas.ai.chat` allowance before answering. Returns `allowance-denied` status when exhausted.
- **LandscapeChatReply** — New `AllowanceDenied` factory with status, remaining, and window fields.
- **docs/DEVELOPMENT.md** — Chat endpoint documentation with curl examples, how it works, enabling Atlas AI, non-goals, and capability/metering.
- **docs/threat-model.md** — Corrects "no AI calls exist in Community" to reflect tenant-borne AI chat surface. Adds AI chat controls to threat model matrix.
- **Tests** — `AiModuleTests` and `PorticProviderTests` factories updated to provide unlimited chat allowance.

## Acceptance Criteria

| Criteria | Status |
|---|---|
| Grounded in tenant catalogue; no cross-tenant leakage | ✅ EF Core global query filter |
| Runs through Fabric AI contract; degrades to "AI not configured" | ✅ `SetupRequired` response |
| Entitlement-gated under `atlas.ai.*` | ✅ `AiAllowanceService` check |
| Metered under `atlas.ai.*` | ✅ Audit trail + allowance enforcement |
| Prompt/response content not logged by default | ✅ |

## Verification

All 119 tests pass (13 architecture + 106 API integration tests).
"@

    Write-Host "`nPR created: $PrUrl" -ForegroundColor Green
}
finally {
    Pop-Location
}