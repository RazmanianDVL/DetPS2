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
    [switch]$UpdateDoc,
    # Prefer Core CLI scoreboard-metrics (clean JSON) over log-parsing run-title
    [switch]$NativeMetrics
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

if (-not $FleetConfig) {
    $FleetConfig = Join-Path $PSScriptRoot "scoreboard-fleet.json"
}
$fleet = Get-Content $FleetConfig -Raw | ConvertFrom-Json
$runTitle = Join-Path $PSScriptRoot "run-title.ps1"

# Normalize -Titles (PowerShell often passes "a,b" as a single string)
$titleIds = @()
foreach ($t in $Titles) {
    if ([string]::IsNullOrWhiteSpace($t)) { continue }
    $titleIds += ($t -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}
$selected = @($fleet.titles)
if ($titleIds.Count -gt 0) {
    $selected = @($fleet.titles | Where-Object { $titleIds -contains $_.id })
    if ($selected.Count -eq 0) {
        Write-Warning "No fleet titles matched: $($titleIds -join ', '). Known ids: $(($fleet.titles | ForEach-Object { $_.id }) -join ', ')"
    }
}

New-Item -ItemType Directory -Force -Path $TraceDir | Out-Null
if (-not $SkipBuild) {
    Write-Host "=== Build once ==="
    dotnet build (Join-Path $repoRoot "src/DetPS2.Core/DetPS2.Core.csproj") -c Release -o $BuildOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$results = @()
$budgetMap = @{ diagnose = [ulong]20000000; verify = [ulong]50000000; claim = [ulong]100000000 }
$cycles = $budgetMap[$Budget]
$dll = Join-Path $BuildOut "DetPS2.Core.dll"

function Get-MenuHeuristic([ulong]$pxN, [int]$gifN) {
    $menu = "No"
    if ($pxN -gt 0 -and $gifN -gt 0) { $menu = "GS?" }
    if ($pxN -gt 10000 -and $gifN -ge 10) { $menu = "NEAR?" }
    if ($pxN -gt 100000 -and $gifN -ge 12) { $menu = "LIKELY-NEAR" }
    return $menu
}

Write-Host "=== Scoreboard budget=$Budget titles=$($selected.Count) native=$NativeMetrics ==="
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

    if ($NativeMetrics -and (Test-Path $dll)) {
        $metricsPath = Join-Path $TraceDir "$($t.id)-$Budget-$stamp-metrics.json"
        $argList = @("exec", $dll, "scoreboard-metrics", $media, "--cycles=$cycles", "--out=$metricsPath")
        if ($hp) { $argList += "--host-present" }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        & dotnet @argList 2>&1 | Out-Null
        $sw.Stop()
        if (Test-Path $metricsPath) {
            $m = Get-Content $metricsPath -Raw | ConvertFrom-Json
            # multi-title media → array
            if ($m -is [array]) { $m = $m[0] }
            $pxN = [ulong]0; [void][ulong]::TryParse([string]$m.px, [ref]$pxN)
            $gifN = 0; [void][int]::TryParse([string]$m.gifPath3, [ref]$gifN)
            # Prefer live metrics serial when fleet entry is empty (Haven historically blank).
            $serial = if ($t.serial) { $t.serial } elseif ($m.serial) { $m.serial } else { "" }
            $results += [pscustomobject]@{
                id = $t.id; name = $t.name; serial = $serial; menuKind = $t.menuKind
                status = "RAN"; menuHeuristic = (Get-MenuHeuristic $pxN $gifN)
                pc = $m.pc; px = $m.px; gifPath3 = $m.gifPath3; dmac = $m.dmac
                cdvd = $m.cdvdSectors; syscalls = $m.syscalls
                binds = $m.binds; calls = $m.calls; exitReq = $m.exitRequested
                elapsedSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
                outLog = $metricsPath
            }
            continue
        }
        Write-Warning "Native metrics failed for $($t.id); falling back to run-title"
    }

    $r = & $runTitle -Media $t.media -Budget $Budget -BuildOut $BuildOut -TraceDir $TraceDir `
        -SkipBuild -HostPresent:$hp
    if ($r) {
        $results += [pscustomobject]@{
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
