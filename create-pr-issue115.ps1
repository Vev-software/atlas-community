# Creates PR for issue #115 — free allowance usable without BYOK
# Run from: C:\Users\rha\source\repos\Vev-software\atlas-community

$ErrorActionPreference = "Stop"

Write-Host "=== Building solution ===" -ForegroundColor Cyan
dotnet build atlas-community.sln
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== Running tests ===" -ForegroundColor Cyan
dotnet test atlas-community.sln
if ($LASTEXITCODE -ne 0) {
    Write-Host "Tests failed!" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== Staging changes ===" -ForegroundColor Cyan
git add -A

Write-Host "`n=== Committing ===" -ForegroundColor Cyan
git commit -m "feat: make free allowance usable without BYOK for extension providers

- AiModuleConfiguration.IsUsableForProvider() allows extension providers
  (e.g. Portic) to work without an API key — the extension handles its own auth
- AiModuleService.SaveAsync makes ApiKey optional for registered extension providers
- CommunityAiAssistService routes extension providers before checking API key
- GET /api/v1/ai/providers endpoint exposes all supported providers with key
  requirement info
- Portic provider is registered opt-in when Atlas:Portic:BaseUrl is configured
- Frontend dynamically loads provider list, hides API key field for extension
  providers, and shows improved setup copy
- Tests cover keyless extension provider flow and providers endpoint

Resolves #115"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Commit failed (no changes?)" -ForegroundColor Yellow
}

Write-Host "`n=== Pushing branch ===" -ForegroundColor Cyan
git push -u origin feat/issue-115-free-allowance
if ($LASTEXITCODE -ne 0) {
    Write-Host "Push failed!" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== Creating PR ===" -ForegroundColor Cyan
$prUrl = gh pr create `
    --repo Vev-software/atlas-community `
    --base main `
    --head feat/issue-115-free-allowance `
    --title "feat: make free allowance usable without BYOK for extension providers" `
    --body @"
## What & why

Free allowance (3 daily AI structurings) was displayed in the AI setup panel but was unusable:
enabling Atlas AI required a BYOK API key for all providers, and no non-BYOK provider route
existed. This closes the UX/product gap between the free-allowance promise and the BYOK requirement.

**How it works:**
- `AiModuleConfiguration.IsUsableForProvider()` relaxes the API key requirement for registered
  extension providers (e.g. Portic) — the extension handles its own authentication
- `AiModuleService.SaveAsync` makes `ApiKey` optional when the selected provider is an extension
- `CommunityAiAssistService` routes extension providers before checking for an API key
- New `GET /api/v1/ai/providers` endpoint exposes all supported providers with key requirement info
- Portic provider is registered opt-in when `Atlas:Portic:BaseUrl` is configured
- Frontend dynamically loads provider list, hides API key field for extension providers,
  and shows improved setup copy explaining the difference

**Files changed:**
- `src/Atlas.Fabric.Abstractions/Ai.cs` — `IsUsableForProvider()` overload
- `src/Atlas.Domain/AiModuleService.cs` — optional key for extensions, `GetProviderInfos()`, `AiProviderInfo`
- `src/Atlas.Fabric.Dev/CommunityAiAssistService.cs` — extension-first routing
- `src/Atlas.Api/AtlasCommunityRegistration.cs` — Portic opt-in registration
- `src/Atlas.Api/AssetEndpoints.cs` — `GET /api/v1/ai/providers` endpoint
- `src/Atlas.Api/Atlas.Api.csproj` — Portic project reference
- `src/Atlas.Api/Program.cs` — passes configuration to registration
- `src/Atlas.Api/wwwroot/index.html` — dynamic provider list, optional API key, improved copy
- `tests/Atlas.Api.Tests/PorticProviderTests.cs` — keyless flow + providers endpoint tests

## Public disclosure check
- [x] For a public repo: PR title/body and public docs mention only public-safe details
- [x] No private repo names, proprietary topology, licence-control detail, internal hostnames, customer names, or security-control specifics

## Checklist (handbook/17 §2, 01 §5–§6)
- [x] Tests: unit + architecture (+ conformance for contracts)
- [x] Local checks pass (`dotnet test`)
- [x] Dependency direction respected — no Fabric→product reference; no product→product code
- [x] No `if (plan == "…")`, no direct AI-provider call, no secrets/content in telemetry
- [x] Docs updated; contract changes carry a compatibility note
- [x] DCO sign-off / CLA per this repo's licence

## Closing

Resolves #115
"@

Write-Host "`n=== PR created: $prUrl ===" -ForegroundColor Green
