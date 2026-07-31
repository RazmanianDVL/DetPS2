<#
.SYNOPSIS
  Multi-title DetPS2 commercial scoreboard (fixed cycle budgets).

.DESCRIPTION
  Builds once, runs each fleet title with tools/run-title.ps1, writes:
    out/traces/scoreboard-YYYYMMDD-HHMMSS.md
    out/traces/scoreboard-YYYYMMDD-HHMMSS.json

  Does NOT claim MENU YES — prints metrics + heuristic only.
  Prefer -Budget diagnose (20M) while iterating; -Budget claim only when asserting menu.

  PL-001 / GX-001: emits play tiers T0–T7 and GFX columns G1–G4 (heuristic stubs OK).
  Schema: tools/SCOREBOARD_SCHEMA.md

.PARAMETER Budget
  diagnose | verify | claim

.PARAMETER Titles
  Optional subset of fleet ids (e.g. mk-shaolin-monks,burnout-3)

.PARAMETER FleetConfig
  Path to scoreboard-fleet.json

.PARAMETER DumpSoftGsDir
  If set, pass --dump-softgs=<dir>/<id>.ppm to scoreboard-metrics (NativeMetrics path).

.EXAMPLE
  pwsh ./tools/scoreboard.ps1 -Budget diagnose
  pwsh ./tools/scoreboard.ps1 -Budget verify -Titles mk-shaolin-monks,blood-omen-2
  pwsh ./tools/scoreboard.ps1 -Budget diagnose -NativeMetrics -Titles mk-shaolin-monks,god-of-war
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
    [switch]$NativeMetrics,
    [string]$DumpSoftGsDir = ""
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

function Get-MenuHeuristic([ulong]$pxN, [ulong]$gifAny) {
    # gifAny = gifP1+gifP2+gifP3 (Path2-only titles e.g. GoW must not read as "No")
    $menu = "No"
    if ($pxN -gt 0 -and $gifAny -gt 0) { $menu = "GS?" }
    if ($pxN -gt 10000 -and $gifAny -ge 6) { $menu = "NEAR?" }
    if ($pxN -gt 100000 -and $gifAny -ge 12) { $menu = "LIKELY-NEAR" }
    return $menu
}

# PL-001 / GX-001: recompute tiers when Core JSON lacks them (log fallback path).
function Get-TierSet {
    param(
        [long]$px = 0,
        [long]$prims = 0,
        [long]$imgBytes = 0,
        [long]$dispfbPx = 0,
        [long]$naturalDispfbPx = 0,
        [long]$expandHits = 0,
        [ulong]$gifP1 = 0,
        [ulong]$gifP2 = 0,
        [ulong]$gifP3 = 0,
        [ulong]$gifCompleted = 0,
        [ulong]$gifAborted = 0,
        [bool]$exitRequested = $false,
        [object]$T0 = $null, [object]$T1 = $null, [object]$T2 = $null, [object]$T3 = $null,
        [object]$T4 = $null, [object]$T5 = $null, [object]$T6 = $null, [object]$T7 = $null,
        [object]$G1 = $null, [object]$G2 = $null, [object]$G3 = $null, [object]$G4 = $null
    )
    $gifAny = $gifP1 + $gifP2 + $gifP3
    $t0 = if ($null -ne $T0 -and "$T0" -ne "") { "$T0" } else { if (-not $exitRequested) { "Y" } else { "N" } }
    $t1 = if ($null -ne $T1 -and "$T1" -ne "") { "$T1" } else {
        if ($px -gt 0 -and $gifAny -gt 0) {
            if ($px -gt 100000 -and $gifP3 -ge 12) { "Y?" } else { "NEAR?" }
        } else { "N" }
    }
    $t2 = if ($null -ne $T2 -and "$T2" -ne "") { "$T2" } else { "?" }
    $t3 = if ($null -ne $T3 -and "$T3" -ne "") { "$T3" } else {
        if ($prims -ge 10 -or $imgBytes -gt 0 -or $dispfbPx -gt 0 -or $gifP3 -ge 20) { "Y?" } else { "N" }
    }
    $t4 = if ($null -ne $T4 -and "$T4" -ne "") { "$T4" } else {
        if ($px -le 0) { "?" } elseif ($expandHits -eq 0) { "Y?" } else { "N" }
    }
    $t5 = if ($null -ne $T5 -and "$T5" -ne "") { "$T5" } else { "?" }
    $t6 = if ($null -ne $T6 -and "$T6" -ne "") { "$T6" } else { "?" }
    $t7 = if ($null -ne $T7 -and "$T7" -ne "") { "$T7" } else { "?" }
    $g1 = if ($null -ne $G1 -and "$G1" -ne "") { "$G1" } else {
        if ($gifCompleted -gt 0 -or $gifAny -gt 0) {
            if ($gifAborted -eq 0 -or $gifCompleted -ge $gifAborted) { "Y?" } else { "WARN" }
        } else { "N" }
    }
    $g2 = if ($null -ne $G2 -and "$G2" -ne "") { "$G2" } else { if ($imgBytes -gt 0) { "Y" } else { "N" } }
    # GX-041 G3: natural DISPFB → Y; residual FRAME/FBP0 composite only → Y?; none → N
    $g3 = if ($null -ne $G3 -and "$G3" -ne "") { "$G3" } else {
        if ($naturalDispfbPx -gt 0) { "Y" } elseif ($dispfbPx -gt 0) { "Y?" } else { "N" }
    }
    $g4 = if ($null -ne $G4 -and "$G4" -ne "") { "$G4" } else {
        if ($px -le 0) { "?" } elseif ($expandHits -eq 0) { "Y?" } else { "N" }
    }
    return [pscustomobject]@{
        T0 = $t0; T1 = $t1; T2 = $t2; T3 = $t3; T4 = $t4; T5 = $t5; T6 = $t6; T7 = $t7
        G1 = $g1; G2 = $g2; G3 = $g3; G4 = $g4
    }
}

function To-ULong($v) {
    if ($null -eq $v) { return [ulong]0 }
    $u = [ulong]0
    [void][ulong]::TryParse([string]$v, [ref]$u)
    return $u
}
function To-Long($v) {
    if ($null -eq $v) { return [long]0 }
    $n = [long]0
    [void][long]::TryParse([string]$v, [ref]$n)
    return $n
}

Write-Host "=== Scoreboard budget=$Budget titles=$($selected.Count) native=$NativeMetrics ==="
foreach ($t in $selected) {
    $media = Join-Path $repoRoot $t.media
    if (-not (Test-Path $media)) {
        Write-Warning "SKIP $($t.id) — missing media config $($t.media)"
        $results += [pscustomobject]@{
            id = $t.id; name = $t.name; serial = $t.serial
            status = "SKIP-NO-MEDIA"; menuHeuristic = "N/A"
            T0 = "N"; T1 = "N"; T2 = "?"; T3 = "N"; T4 = "?"; T5 = "?"; T6 = "?"; T7 = "?"
            G1 = "N"; G2 = "N"; G3 = "N"; G4 = "?"
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
                T0 = "N"; T1 = "N"; T2 = "?"; T3 = "N"; T4 = "?"; T5 = "?"; T6 = "?"; T7 = "?"
                G1 = "N"; G2 = "N"; G3 = "N"; G4 = "?"
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
        if ($DumpSoftGsDir) {
            New-Item -ItemType Directory -Force -Path $DumpSoftGsDir | Out-Null
            $ppm = Join-Path $DumpSoftGsDir "$($t.id)-$Budget.ppm"
            $argList += "--dump-softgs=$ppm"
        }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        & dotnet @argList 2>&1 | Out-Null
        $sw.Stop()
        if (Test-Path $metricsPath) {
            $m = Get-Content $metricsPath -Raw | ConvertFrom-Json
            # multi-title media → array
            if ($m -is [array]) { $m = $m[0] }
            $pxN = To-ULong $m.px
            $gifP1 = To-ULong $(if ($null -ne $m.gifPath1) { $m.gifPath1 } else { $m.gifP1 })
            $gifP2 = To-ULong $(if ($null -ne $m.gifPath2) { $m.gifPath2 } else { $m.gifP2 })
            $gifP3 = To-ULong $(if ($null -ne $m.gifPath3) { $m.gifPath3 } else { $m.gifP3 })
            $gifAny = $gifP1 + $gifP2 + $gifP3
            $prims = To-Long $m.prims
            $imgBytes = To-Long $m.imgBytes
            $dispfbPx = To-Long $m.dispfbPx
            $naturalDispfbPx = To-Long $m.naturalDispfbPx
            $residualDispfbPx = To-Long $m.residualDispfbPx
            $compositeSource = if ($null -ne $m.compositeSource) { [string]$m.compositeSource } else { "" }
            $expandHits = To-Long $m.expandHits
            $gifCompleted = To-ULong $m.gifCompleted
            $gifAborted = To-ULong $m.gifAborted
            $exitReq = [bool]$m.exitRequested
            $tiers = Get-TierSet -px ([long]$pxN) -prims $prims -imgBytes $imgBytes -dispfbPx $dispfbPx `
                -naturalDispfbPx $naturalDispfbPx `
                -expandHits $expandHits -gifP1 $gifP1 -gifP2 $gifP2 -gifP3 $gifP3 `
                -gifCompleted $gifCompleted -gifAborted $gifAborted -exitRequested $exitReq `
                -T0 $m.T0 -T1 $m.T1 -T2 $m.T2 -T3 $m.T3 -T4 $m.T4 -T5 $m.T5 -T6 $m.T6 -T7 $m.T7 `
                -G1 $m.G1 -G2 $m.G2 -G3 $m.G3 -G4 $m.G4
            # Prefer live metrics serial when fleet entry is empty (Haven historically blank).
            $serial = if ($t.serial) { $t.serial } elseif ($m.serial) { $m.serial } else { "" }
            $results += [pscustomobject]@{
                id = $t.id; name = $t.name; serial = $serial; menuKind = $t.menuKind
                status = "RAN"; menuHeuristic = (Get-MenuHeuristic $pxN $gifAny)
                pc = $m.pc; px = $m.px; prims = $prims
                gifPath1 = $gifP1; gifPath2 = $gifP2; gifPath3 = $gifP3
                gifP1 = $gifP1; gifP2 = $gifP2; gifP3 = $gifP3
                imgBytes = $imgBytes; dispfbPx = $dispfbPx
                naturalDispfbPx = $naturalDispfbPx; residualDispfbPx = $residualDispfbPx
                compositeSource = $compositeSource
                expandHits = $expandHits
                gifCompleted = $gifCompleted; gifAborted = $gifAborted
                dmac = $m.dmac
                cdvd = $m.cdvdSectors; syscalls = $m.syscalls
                binds = $m.binds; calls = $m.calls; exitReq = $m.exitRequested
                T0 = $tiers.T0; T1 = $tiers.T1; T2 = $tiers.T2; T3 = $tiers.T3
                T4 = $tiers.T4; T5 = $tiers.T5; T6 = $tiers.T6; T7 = $tiers.T7
                G1 = $tiers.G1; G2 = $tiers.G2; G3 = $tiers.G3; G4 = $tiers.G4
                dumpSoftGs = $m.dumpSoftGs
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
        $pxN = To-ULong $r.px
        $gifP1 = To-ULong $r.gifPath1
        $gifP2 = To-ULong $r.gifPath2
        $gifP3 = To-ULong $(if ($r.gifPath3) { $r.gifPath3 } else { $r.gifP3 })
        $prims = To-Long $r.prims
        $imgBytes = To-Long $r.imgBytes
        $dispfbPx = To-Long $r.dispfbPx
        $naturalDispfbPx = To-Long $r.naturalDispfbPx
        $residualDispfbPx = To-Long $r.residualDispfbPx
        $expandHits = To-Long $r.expandHits
        $gifCompleted = To-ULong $r.gifCompleted
        $gifAborted = To-ULong $r.gifAborted
        $exitReq = ("$($r.exitReq)" -match 'True')
        $tiers = Get-TierSet -px ([long]$pxN) -prims $prims -imgBytes $imgBytes -dispfbPx $dispfbPx `
            -naturalDispfbPx $naturalDispfbPx `
            -expandHits $expandHits -gifP1 $gifP1 -gifP2 $gifP2 -gifP3 $gifP3 `
            -gifCompleted $gifCompleted -gifAborted $gifAborted -exitRequested $exitReq
        $results += [pscustomobject]@{
            id             = $t.id
            name           = $t.name
            serial         = $t.serial
            menuKind       = $t.menuKind
            status         = "RAN"
            menuHeuristic  = $r.menuHeuristic
            pc             = $r.pc
            px             = $r.px
            prims          = $prims
            gifPath1       = $gifP1
            gifPath2       = $gifP2
            gifPath3       = $gifP3
            gifP1          = $gifP1
            gifP2          = $gifP2
            gifP3          = $gifP3
            imgBytes       = $imgBytes
            dispfbPx       = $dispfbPx
            naturalDispfbPx = $naturalDispfbPx
            residualDispfbPx = $residualDispfbPx
            compositeSource = ""
            expandHits     = $expandHits
            gifCompleted   = $gifCompleted
            gifAborted     = $gifAborted
            dmac           = $r.dmac
            cdvd           = $r.cdvd
            syscalls       = $r.syscalls
            binds          = $r.binds
            calls          = $r.calls
            exitReq        = $r.exitReq
            T0 = $tiers.T0; T1 = $tiers.T1; T2 = $tiers.T2; T3 = $tiers.T3
            T4 = $tiers.T4; T5 = $tiers.T5; T6 = $tiers.T6; T7 = $tiers.T7
            G1 = $tiers.G1; G2 = $tiers.G2; G3 = $tiers.G3; G4 = $tiers.G4
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
$md += "- **NativeMetrics:** $NativeMetrics"
$md += "- **Policy:** Soft-GS metrics only (no dGPU required). MENU YES is manual/claim, not this heuristic."
$md += "- **Schema:** ``tools/SCOREBOARD_SCHEMA.md`` (T0–T7 + G1–G4)"
$md += ""
$md += "| Title | Serial | Heur | T0 | T1 | T2 | T3 | T4 | T5 | T6 | T7 | G1 | G2 | G3 | G4 | PC | px | prims | gifP1 | gifP2 | gifP3 | img | dispfb | natDispfb | src | expand | dmac | cdvd | sec |"
$md += "|-------|--------|------|----|----|----|----|----|----|----|----|----|----|----|----|----|----|-------|-------|-------|-------|-----|--------|-----------|-----|--------|------|------|-----|"
foreach ($r in $results) {
    $nat = if ($null -ne $r.naturalDispfbPx) { $r.naturalDispfbPx } else { 0 }
    $src = if ($r.compositeSource) { $r.compositeSource } else { "-" }
    $md += "| $($r.name) | $($r.serial) | **$($r.menuHeuristic)** | $($r.T0) | $($r.T1) | $($r.T2) | $($r.T3) | $($r.T4) | $($r.T5) | $($r.T6) | $($r.T7) | $($r.G1) | $($r.G2) | $($r.G3) | $($r.G4) | $($r.pc) | $($r.px) | $($r.prims) | $($r.gifP1) | $($r.gifP2) | $($r.gifP3) | $($r.imgBytes) | $($r.dispfbPx) | $nat | $src | $($r.expandHits) | $($r.dmac) | $($r.cdvd) | $($r.elapsedSec) |"
}
$md += ""
$md += "## Tier legend (heuristic — not formal claims)"
$md += ""
$md += "| Code | Meaning |"
$md += "|------|---------|"
$md += "| T0 Boot | Spine live |"
$md += "| T1 Menu | Soft-GS + GIF activity |"
$md += "| T2 Interactive | ``--pad-script=`` / pad-inject (PL-002); ``?`` without script |"
$md += "| T3 Frontend | prims/img/dispfb/gifP3 bars |"
$md += "| T4 Natural | expandHits==0 |"
$md += "| T5–T7 | stubs until gameplay/IRX seasons |"
$md += "| G1 Path | gif completed / path counts |"
$md += "| G2 Tex | imgBytes>0 |"
$md += "| G3 Present | naturalDispfbPx>0 → Y; residual FRAME/FBP0 dispfbPx only → Y? (B3-class); else N |"
$md += "| G4 Expand off | expandHits==0 |"
$md += "| natDispfb / src | GX-041 natural DISPFB px + compositeSource (NaturalDispfb|Frame|SyntheticFbp0) |"
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
$md += "Agent SOP: ``docs/AGENT_SOP.md``  "
$md += "PPM dump: ``scoreboard-metrics --dump-softgs=path`` (when px>0)"
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
