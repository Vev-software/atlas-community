#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run Atlas Community locally (no container), outside Visual Studio.

.DESCRIPTION
    Restores packages from nuget.org and starts the API with `dotnet run`. The API creates its
    SQLite schema on first run, serves the read-only landscape UI from wwwroot, and exposes
    OpenAPI + /health.

    In Development (the default here) identity runs in `dev-headers` mode, so the DEVELOPMENT.md
    "Try it" curl examples work verbatim: pass X-Tenant-Id / X-Principal-Id / X-Principal-Roles.

.PARAMETER Port
    HTTP port to listen on. Default 5199 (matches the docs examples).

.PARAMETER SingleTenant
    Run in single-tenant identity mode (fixed tenant "community" from config; X-* headers ignored)
    instead of the default Development dev-headers shim. Note: `dotnet run` defaults to the Production
    environment here (there is no launchSettings.json), where Atlas fails closed with no identity
    provider — so this script always sets one of the two modes explicitly.

.EXAMPLE
    ./start.ps1

#>
[CmdletBinding()]
param(
    [int]$Port = 5199,
    # Run in single-tenant identity mode (the self-host default) instead of the dev-headers shim.
    [switch]$SingleTenant,
    # Anything after the named switches is forwarded to `dotnet run` — e.g.
    #   ./start.ps1 --Atlas:Identity:Mode=single-tenant
    # (append the extra args directly; do not use a `--` separator, PowerShell reparses after it).
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot                    # atlas-community

function Assert-Command($name, $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required command '$name' not found on PATH. $hint"
    }
}

Assert-Command dotnet 'Install the .NET 10 SDK: https://dotnet.microsoft.com/download'

# --- Run ----------------------------------------------------------------------------------
$env:ASPNETCORE_URLS = "http://localhost:$Port"

# With no launchSettings.json, `dotnet run` defaults to the Production environment, in which Atlas
# fails closed (no identity provider wired) and refuses to start. Select an identity mode explicitly:
#   default        -> Development, so the dev-headers shim is allowed (X-* headers stand in for Fabric)
#   -SingleTenant  -> the self-host mode (fixed tenant from config; X-* headers ignored), any environment
if ($SingleTenant) {
    $env:Atlas__Identity__Mode = 'single-tenant'
    $identity = 'single-tenant (fixed tenant "community"; X-* headers ignored)'
} else {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Atlas__Identity__Mode = $null
    $identity = 'dev-headers (X-Tenant-Id / X-Principal-Id / X-Principal-Roles)'
}

Write-Host ""
Write-Host "Atlas Community — local run" -ForegroundColor Green
Write-Host "  UI      : http://localhost:$Port/"
Write-Host "  Health  : http://localhost:$Port/health"
Write-Host "  OpenAPI : http://localhost:$Port/openapi/v1.json"
Write-Host "  Identity: $identity"
Write-Host "  Ctrl+C to stop."
Write-Host ""

$project = Join-Path $repo 'src/Atlas.Api'
if ($AppArgs) {
    dotnet run --project $project @AppArgs
} else {
    dotnet run --project $project
}
