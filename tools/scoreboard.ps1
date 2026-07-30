<#
.SYNOPSIS
  Multi-title DetPS2 commercial scoreboard (fixed cycle budgets).

.DESCRIPTION
  Builds once, runs each fleet title with tools/run-title.ps1, writes:
    out/traces/scoreboard-YYYYMMDD-HHMMSS.md
    out/traces/scoreboard-YYYYMMDD-HHMMSS.json

  Does NOT claim MENU YES — prints metrics + heuristic only.
  Prefer -Budget diagnose (20M) while iterating; -Budget claim only when asserting menu.

.PARAMETER Budget
  diagnose | verify | claim

.PARAMETER Titles
  Optional subset of fleet ids (e.g. mk-shaolin-monks,burnout-3)

.PARAMETER FleetConfig
  Path to scoreboard-fleet.json

.EXAMPLE
  pwsh ./tools/scoreboard.ps1 -Budget diagnose
  pwsh ./tools/scoreboard.ps1 -Budget verify -Titles mk-shaolin-monks,blood-omen-2
#>
[CmdletBinding()]
param(
    [ValidateSet("diagnose", "verify", "claim")]
    [string]$Budget = "diagnose",
    [string[]]$Titles = @(),
    [string]$FleetConfig = "",
    [string]$BuildOut = "out/scoreboard-build",
    [string]$TraceDir = "out/traces",
    [switch]$SkipBuild,
    [switch]$NoHostPresent,
    [switch]$UpdateDoc
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

if (-not $FleetConfig) {
    $FleetConfig = Join-Path $PSScriptRoot "scoreboard-fleet.json"
}
$fleet = Get-Content $FleetConfig -Raw | ConvertFrom-Json
$runTitle = Join-Path $PSScriptRoot "run-title.ps1"

$selected = $fleet.titles
if ($Titles.Count -gt 0) {
    $selected = $fleet.titles | Where-Object { $Titles -contains $_.id }
}

New-Item -ItemType Directory -Force -Path $TraceDir | Out-Null
if (-not $SkipBuild) {
    Write-Host "=== Build once ==="
    dotnet build (Join-Path $repoRoot "src/DetPS2.Core/DetPS2.Core.csproj") -c Release -o $BuildOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$results = @()

Write-Host "=== Scoreboard budget=$Budget titles=$($selected.Count) ==="
foreach ($t in $selected) {
    $media = Join-Path $repoRoot $t.media
    if (-not (Test-Path $media)) {
        Write-Warning "SKIP $($t.id) — missing media config $($t.media)"
        $results += [pscustomobject]@{
            id = $t.id; name = $t.name; serial = $t.serial
            status = "SKIP-NO-MEDIA"; menuHeuristic = "N/A"
        }
        continue
    }
    # Check ISO path inside media
    try {
        $cfg = Get-Content $media -Raw | ConvertFrom-Json
        $iso = $cfg.titles[0].path
        if (-not (Test-Path -LiteralPath $iso)) {
            Write-Warning "SKIP $($t.id) — ISO missing: $iso"
            $results += [pscustomobject]@{
                id = $t.id; name = $t.name; serial = $t.serial
                status = "SKIP-NO-ISO"; menuHeuristic = "N/A"; path = $iso
            }
            continue
        }
    } catch {
        Write-Warning "SKIP $($t.id) — bad media json"
        continue
    }

    Write-Host ""
    Write-Host ">>> $($t.name) ($($t.id))"
    $hp = -not $NoHostPresent
    $r = & $runTitle -Media $t.media -Budget $Budget -BuildOut $BuildOut -TraceDir $TraceDir `
        -SkipBuild -HostPresent:$hp
    if ($r) {
        $row = [pscustomobject]@{
            id             = $t.id
            name           = $t.name
            serial         = $t.serial
            menuKind       = $t.menuKind
            status         = "RAN"
            menuHeuristic  = $r.menuHeuristic
            pc             = $r.pc
            px             = $r.px
            gifPath3       = $r.gifPath3
            dmac           = $r.dmac
            cdvd           = $r.cdvd
            syscalls       = $r.syscalls
            binds          = $r.binds
            calls          = $r.calls
            exitReq        = $r.exitReq
            elapsedSec     = $r.elapsedSec
            outLog         = $r.outLog
        }
        $results += $row
    }
}

# Markdown table
$mdPath = Join-Path $TraceDir "scoreboard-$stamp.md"
$jsonPath = Join-Path $TraceDir "scoreboard-$stamp.json"

$md = @()
$md += "# DetPS2 scoreboard — $stamp"
$md += ""
$md += "- **Budget:** $Budget"
$md += "- **Build:** $BuildOut"
$md += "- **HostPresent:** $(-not $NoHostPresent)"
$md += "- **Policy:** Soft-GS metrics only (no dGPU required). MENU YES is manual/claim, not this heuristic."
$md += ""
$md += "| Title | Serial | Heuristic | PC | px | gifP3 | dmac | cdvd | binds/calls | sec |"
$md += "|-------|--------|-----------|----|----|-------|------|------|-------------|-----|"
foreach ($r in $results) {
    $bc = if ($r.binds -or $r.calls) { "$($r.binds)/$($r.calls)" } else { "" }
    $md += "| $($r.name) | $($r.serial) | **$($r.menuHeuristic)** | $($r.pc) | $($r.px) | $($r.gifPath3) | $($r.dmac) | $($r.cdvd) | $bc | $($r.elapsedSec) |"
}
$md += ""
$md += "## Menu evidence bar (manual)"
$md += ""
$md += "| Kind | Claim MENU when |"
$md += "|------|-----------------|"
$md += "| mk-mainmenu | Selection index + second chrome / full interactive (not soft NEAR) |"
$md += "| logo-frontend | Non-black Soft-GS after FRONTEND / logo spine |"
$md += "| mainmenu-bg2 | MAINMENU draw px>>logo; pad interactive if applicable |"
$md += "| first-gs-interactive | px>0 non-black + pad (GoW/SotC — not MK MAINMENU) |"
$md += "| midway-menu | gameart/UI stream + interactive chrome |"
$md += ""
$md += "Play! oracle: ``pwsh ./tools/play-lookup.ps1 -Serial <serial>``  "
$md += "Agent SOP: ``docs/AGENT_SOP.md``"
$md += ""

$md -join "`n" | Set-Content $mdPath -Encoding utf8
$results | ConvertTo-Json -Depth 6 | Set-Content $jsonPath -Encoding utf8

# Optional: refresh committed campaign scoreboard doc (opt-in — avoid dirtying git on every diagnose)
$scoreDoc = Join-Path $repoRoot "docs/title-ports/SCOREBOARD.md"
if ($UpdateDoc) {
    $md | Set-Content $scoreDoc -Encoding utf8
}

Write-Host ""
Write-Host "=== SCOREBOARD WRITTEN ==="
Write-Host $mdPath
Write-Host $jsonPath
if ($UpdateDoc) { Write-Host $scoreDoc }
Write-Host ""
Get-Content $mdPath
