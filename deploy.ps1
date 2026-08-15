#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build and run Atlas Community as a container, outside Visual Studio, using Podman (or Docker).

.DESCRIPTION
    Builds the self-host image from this repository and runs it single-tenant on a persistent
    named volume.

    The container listens on 8080, runs as non-root, stores the SQLite catalogue on the
    `atlas-data` volume (/data/atlas.db), and runs in single-tenant identity mode (set in the
    Dockerfile) — no X-* headers needed.

    Engine: prefers Podman (installed here), falls back to Docker.

.PARAMETER Port
    Host port to publish the container's 8080 on. Default 8080.

.PARAMETER Rebuild
    Force a fresh image build (no layer cache).

.PARAMETER Down
    Stop and remove the running container (keeps the data volume) and exit.

.PARAMETER Purge
    With -Down, also remove the atlas-data volume (DESTROYS the catalogue).

.PARAMETER Logs
    Follow the container logs after it starts.

.EXAMPLE
    ./deploy.ps1

.EXAMPLE
    ./deploy.ps1 -Down
#>
[CmdletBinding()]
param(
    [int]$Port = 8080,
    [switch]$Rebuild,
    [switch]$Down,
    [switch]$Purge,
    [switch]$Logs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot                    # atlas-community
$image = 'atlas-community:local'
$name = 'atlas-community'
$volume = 'atlas-data'

# --- Pick a container engine --------------------------------------------------------------
$engine = $null
foreach ($candidate in 'podman', 'docker') {
    if (Get-Command $candidate -ErrorAction SilentlyContinue) { $engine = $candidate; break }
}
if (-not $engine) { throw "Neither 'podman' nor 'docker' found on PATH. Install one to deploy a container." }
Write-Host "Using container engine: $engine" -ForegroundColor Cyan

# --- Tear down ----------------------------------------------------------------------------
if ($Down) {
    & $engine rm -f $name 2>$null | Out-Null
    Write-Host "Removed container '$name' (if it was running)." -ForegroundColor Yellow
    if ($Purge) {
        & $engine volume rm $volume 2>$null | Out-Null
        Write-Host "Removed volume '$volume' — the catalogue is gone." -ForegroundColor Red
    }
    return
}

# --- Build --------------------------------------------------------------------------------
$buildArgs = @('build', '-f', (Join-Path $repo 'Dockerfile'), '-t', $image)
if ($Rebuild) { $buildArgs += '--no-cache' }
$buildArgs += $repo

Write-Host "Building image '$image' (context: $repo) ..." -ForegroundColor Cyan
& $engine @buildArgs
if ($LASTEXITCODE -ne 0) { throw "$engine build failed." }

# --- Run ----------------------------------------------------------------------------------
# Replace any previous container of the same name so re-deploys are idempotent.
& $engine rm -f $name 2>$null | Out-Null

Write-Host "Starting container '$name' on host port $Port ..." -ForegroundColor Cyan
& $engine run -d --name $name -p "${Port}:8080" -v "${volume}:/data" --restart unless-stopped $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw "$engine run failed." }

# --- Wait for health ----------------------------------------------------------------------
$healthUrl = "http://localhost:$Port/health"
Write-Host "Waiting for $healthUrl ..." -NoNewline
$ok = $false
foreach ($_ in 1..30) {
    try {
        $r = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2
        if ($r.status -eq 'ok') { $ok = $true; break }
    } catch { }
    Start-Sleep -Seconds 1
    Write-Host "." -NoNewline
}
Write-Host ""

if ($ok) {
    Write-Host ""
    Write-Host "Atlas Community is up." -ForegroundColor Green
    Write-Host "  UI      : http://localhost:$Port/"
    Write-Host "  Health  : http://localhost:$Port/health"
    Write-Host "  Assets  : http://localhost:$Port/api/v1/assets   (single-tenant; no headers needed)"
    Write-Host "  Data    : volume '$volume' -> /data/atlas.db (survives restarts)"
    Write-Host ""
    Write-Host "  Logs    : $engine logs -f $name"
    Write-Host "  Stop    : ./deploy.ps1 -Down"
} else {
    Write-Warning "Container did not report healthy in time. Check logs: $engine logs $name"
}

if ($Logs) { & $engine logs -f $name }
