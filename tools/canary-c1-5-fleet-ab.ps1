<#
.SYNOPSIS
  C1.5 fleet A/B harness: IOP multi-thread + real-RPC flags vs baseline.

.DESCRIPTION
  Infrastructure A/B only — NOT a MENU campaign and does not assert MENU YES.

  Builds DetPS2.Core once (or -SkipBuild), then runs a small diagnose-tier fleet
  twice under the same cycle budget:

    B (baseline)  — DETPS2_IOP_THREADS unset, DETPS2_IOP_REAL_RPC unset
                    (product defaults: single IOP context; LiveRpcDispatchEnabled
                     prefers live when a real server is registered — see caveats)
    A (flag-on)   — DETPS2_IOP_THREADS=1  AND  DETPS2_IOP_REAL_RPC=1

  Captures scoreboard-metrics JSON (preferred) or blocker-trace logs under:

    out/canaries/c1-5/<stamp>/baseline/
    out/canaries/c1-5/<stamp>/flag-on/
    out/canaries/c1-5/<stamp>/summary.md
    out/canaries/c1-5/<stamp>/summary.json

  Prints an honest comparison. Does NOT claim success if flag-on crashes,
  regresses exit/status, or fails to complete while baseline did.

  Exit criteria reference: docs/IOP_MULTITHREAD_AND_REAL_RPC.md §8
    - flag-off: no behavior change vs baseline
    - flag-on: multi-context smoke; at least one commercial path may show
      live registry growth / real handler dispatch (LiveRpcHits not yet in
      scoreboard-metrics JSON — compare binds/calls/px/dmac/cdvd + crash absence)
    - HLE fallback still boots when real path misses

.PARAMETER Budget
  diagnose (20M) | verify (50M) | claim (100M). Default: diagnose.

.PARAMETER Titles
  Optional fleet ids (comma-separated or array). Default: regression-matrix four
  (mk-shaolin-monks, burnout-3, blood-omen-2, god-of-war).

.PARAMETER FleetConfig
  Path to scoreboard-fleet.json (default: tools/scoreboard-fleet.json).

.PARAMETER BuildOut
  Release build output (default: out/scoreboard-build).

.PARAMETER SkipBuild
  Reuse existing DetPS2.Core.dll under -BuildOut (or Release bin).

.PARAMETER OutDir
  Root capture dir (default: out/canaries/c1-5).

.PARAMETER NoHostPresent
  Omit --host-present.

.PARAMETER NativeMetrics
  Prefer scoreboard-metrics CLI (default: $true). Use -NativeMetrics:$false for
  blocker-trace log scrape (includes RealSifRpc binds= line).

.PARAMETER TraceRealRpc
  Set DETPS2_TRACE_REALRPC=1 on both arms (noisy; useful for live-registry probes).

.PARAMETER SkipRun
  Resolve paths / print plan only.

.EXAMPLE
  # Default diagnose A/B on the four-title regression set
  pwsh ./tools/canary-c1-5-fleet-ab.ps1

  # Reuse existing scoreboard build, two titles only
  pwsh ./tools/canary-c1-5-fleet-ab.ps1 -SkipBuild -Titles burnout-3,blood-omen-2

  # Longer soak after a suspected C1 fix
  pwsh ./tools/canary-c1-5-fleet-ab.ps1 -Budget verify -SkipBuild
#>
[CmdletBinding()]
param(
    [ValidateSet("diagnose", "verify", "claim")]
    [string]$Budget = "diagnose",
    [string[]]$Titles = @(),
    [string]$FleetConfig = "",
    [string]$BuildOut = "out/scoreboard-build",
    [switch]$SkipBuild,
    [string]$OutDir = "out/canaries/c1-5",
    [switch]$NoHostPresent,
    [bool]$NativeMetrics = $true,
    [switch]$TraceRealRpc,
    [switch]$SkipRun
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

# --- Defaults / fleet selection ---
if (-not $FleetConfig) {
    $FleetConfig = Join-Path $PSScriptRoot "scoreboard-fleet.json"
}
if (-not (Test-Path -LiteralPath $FleetConfig)) {
    throw "Fleet config not found: $FleetConfig"
}
$fleet = Get-Content -LiteralPath $FleetConfig -Raw | ConvertFrom-Json

$defaultIds = @(
    "mk-shaolin-monks",
    "burnout-3",
    "blood-omen-2",
    "god-of-war"
)

$titleIds = @()
foreach ($t in $Titles) {
    if ([string]::IsNullOrWhiteSpace($t)) { continue }
    $titleIds += ($t -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}
if ($titleIds.Count -eq 0) { $titleIds = $defaultIds }

$selected = @($fleet.titles | Where-Object { $titleIds -contains $_.id })
if ($selected.Count -eq 0) {
    throw "No fleet titles matched: $($titleIds -join ', '). Known: $(($fleet.titles | ForEach-Object { $_.id }) -join ', ')"
}
$missing = @($titleIds | Where-Object { $selected.id -notcontains $_ })
if ($missing.Count -gt 0) {
    Write-Warning "Unknown fleet id(s) ignored: $($missing -join ', ')"
}

$budgetMap = @{
    diagnose = [ulong]20000000
    verify   = [ulong]50000000
    claim    = [ulong]100000000
}
$cycles = $budgetMap[$Budget]
$hostPresent = -not $NoHostPresent

# --- Build / DLL resolve ---
$releaseDll = Join-Path $repoRoot "src/DetPS2.Core/bin/Release/net9.0/DetPS2.Core.dll"
$scoreDll   = Join-Path $repoRoot (Join-Path $BuildOut "DetPS2.Core.dll")

if (-not $SkipBuild) {
    New-Item -ItemType Directory -Force -Path $BuildOut | Out-Null
    Write-Host "=== Build once → $BuildOut ==="
    dotnet build (Join-Path $repoRoot "src/DetPS2.Core/DetPS2.Core.csproj") -c Release -o $BuildOut --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

$dll = $null
if (Test-Path -LiteralPath $scoreDll) { $dll = $scoreDll }
elseif (Test-Path -LiteralPath $releaseDll) { $dll = $releaseDll }
else {
    throw "DetPS2.Core.dll not found. Tried:`n  $scoreDll`n  $releaseDll`nPass -Build (default) or -SkipBuild after a prior scoreboard build."
}

# --- Capture dirs ---
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $OutDir $stamp
$baseDir = Join-Path $runRoot "baseline"
$flagDir = Join-Path $runRoot "flag-on"
New-Item -ItemType Directory -Force -Path $baseDir | Out-Null
New-Item -ItemType Directory -Force -Path $flagDir | Out-Null

# --- Env snapshot (restore after both arms) ---
$envKeys = @(
    "DETPS2_IOP_THREADS",
    "DETPS2_IOP_REAL_RPC",
    "DETPS2_NO_REAL_RPC",
    "DETPS2_TRACE_REALRPC"
)
$envSaved = @{}
foreach ($k in $envKeys) {
    $envSaved[$k] = [Environment]::GetEnvironmentVariable($k, "Process")
}

function Restore-Detps2Env {
    foreach ($k in $envKeys) {
        $v = $envSaved[$k]
        if ($null -eq $v -or $v -eq "") {
            Remove-Item "Env:$k" -ErrorAction SilentlyContinue
        } else {
            Set-Item -Path "Env:$k" -Value $v
        }
    }
}

function Clear-C15Flags {
    Remove-Item Env:DETPS2_IOP_THREADS -ErrorAction SilentlyContinue
    Remove-Item Env:DETPS2_IOP_REAL_RPC -ErrorAction SilentlyContinue
    # Do not force DETPS2_NO_REAL_RPC=1 on baseline: product default already prefers live
    # when a real server exists (LiveRpcDispatchEnabled). Explicit hard-off is for bisect only.
    Remove-Item Env:DETPS2_NO_REAL_RPC -ErrorAction SilentlyContinue
    if ($TraceRealRpc) {
        $env:DETPS2_TRACE_REALRPC = "1"
    } else {
        Remove-Item Env:DETPS2_TRACE_REALRPC -ErrorAction SilentlyContinue
    }
}

function Set-C15FlagsOn {
    $env:DETPS2_IOP_THREADS = "1"
    $env:DETPS2_IOP_REAL_RPC = "1"
    # Prefer-live opt-in must not be shadowed by emergency hard-off
    Remove-Item Env:DETPS2_NO_REAL_RPC -ErrorAction SilentlyContinue
    if ($TraceRealRpc) {
        $env:DETPS2_TRACE_REALRPC = "1"
    } else {
        Remove-Item Env:DETPS2_TRACE_REALRPC -ErrorAction SilentlyContinue
    }
}

function To-ULong($v) {
    if ($null -eq $v) { return [ulong]0 }
    $u = [ulong]0
    [void][ulong]::TryParse([string]$v, [ref]$u)
    return $u
}

function Get-Metric([string]$pattern, [string]$src) {
    $m = [regex]::Match($src, $pattern, "IgnoreCase")
    if ($m.Success) { return $m.Groups[1].Value }
    return ""
}

function Classify-RunStatus {
    param(
        [int]$ExitCode,
        [bool]$HasMetrics,
        [string]$JoinedLog,
        [bool]$ExitRequested
    )
    # Hard process failure
    if ($ExitCode -ne 0) {
        if ($JoinedLog -match '(?i)Unhandled exception|Fatal error|AccessViolation|stack overflow|OutOfMemory') {
            return "CRASH"
        }
        return "EXIT-$ExitCode"
    }
    if (-not $HasMetrics) { return "NO-METRICS" }
    if ($ExitRequested) { return "EXIT-REQ" }
    if ($JoinedLog -match '(?i)Unhandled exception|AccessViolation') { return "CRASH" }
    return "RAN"
}

function Invoke-TitleArm {
    param(
        [object]$Title,
        [string]$ArmName,   # baseline | flag-on
        [string]$ArmDir
    )

    $media = Join-Path $repoRoot $Title.media
    $id = $Title.id
    $row = [ordered]@{
        id          = $id
        name        = $Title.name
        serial      = $Title.serial
        arm         = $ArmName
        media       = $Title.media
        status      = "SKIP"
        exitCode    = $null
        elapsedSec  = $null
        pc          = ""
        px          = $null
        prims       = $null
        gifP1       = $null
        gifP2       = $null
        gifP3       = $null
        dmac        = $null
        cdvd        = $null
        binds       = $null
        calls       = $null
        syscalls    = $null
        exitReq     = $null
        realrpcDbg  = 0
        outLog      = ""
        errLog      = ""
        metricsPath = ""
        note        = ""
    }

    if (-not (Test-Path -LiteralPath $media)) {
        $row.status = "SKIP-NO-MEDIA"
        $row.note = "missing media config"
        return [pscustomobject]$row
    }
    try {
        $cfg = Get-Content -LiteralPath $media -Raw | ConvertFrom-Json
        $iso = $cfg.titles[0].path
        if (-not (Test-Path -LiteralPath $iso)) {
            $row.status = "SKIP-NO-ISO"
            $row.note = "ISO missing: $iso"
            return [pscustomobject]$row
        }
    } catch {
        $row.status = "SKIP-BAD-MEDIA"
        $row.note = "$_"
        return [pscustomobject]$row
    }

    $outLog = Join-Path $ArmDir "$id-out.txt"
    $errLog = Join-Path $ArmDir "$id-err.txt"
    $metricsPath = Join-Path $ArmDir "$id-metrics.json"
    $row.outLog = $outLog
    $row.errLog = $errLog
    $row.metricsPath = $metricsPath

    if ($NativeMetrics) {
        $argList = @(
            "exec", $dll, "scoreboard-metrics", $media,
            "--cycles=$cycles", "--out=$metricsPath"
        )
        if ($hostPresent) { $argList += "--host-present" }
    } else {
        $argList = @("exec", $dll, "blocker-trace", $media, "--cycles=$cycles")
        if ($hostPresent) { $argList += "--host-present" }
    }

    Write-Host "  [$ArmName] $id — dotnet $($argList -join ' ')"

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath "dotnet" -ArgumentList $argList -WorkingDirectory $repoRoot `
        -NoNewWindow -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    Wait-Process -Id $p.Id
    $sw.Stop()
    $exit = $p.ExitCode
    if ($null -eq $exit) { $exit = -1 }
    $row.exitCode = $exit
    $row.elapsedSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)

    $text = @()
    if (Test-Path -LiteralPath $outLog) {
        $text += Get-Content -LiteralPath $outLog -Raw -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $errLog) {
        $text += Get-Content -LiteralPath $errLog -Raw -ErrorAction SilentlyContinue
    }
    $joined = ($text -join "`n")
    if (-not $joined) { $joined = "" }

    $row.realrpcDbg = ([regex]::Matches($joined, '\[REALRPC')).Count

    $hasMetrics = $false
    $exitReq = $false

    if ($NativeMetrics -and (Test-Path -LiteralPath $metricsPath)) {
        try {
            $m = Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json
            if ($m -is [array]) { $m = $m[0] }
            $hasMetrics = $true
            $row.pc = [string]$m.pc
            $row.px = To-ULong $m.px
            $row.prims = To-ULong $m.prims
            $row.gifP1 = To-ULong $(if ($null -ne $m.gifPath1) { $m.gifPath1 } else { $m.gifP1 })
            $row.gifP2 = To-ULong $(if ($null -ne $m.gifPath2) { $m.gifPath2 } else { $m.gifP2 })
            $row.gifP3 = To-ULong $(if ($null -ne $m.gifPath3) { $m.gifPath3 } else { $m.gifP3 })
            $row.dmac = To-ULong $m.dmac
            $row.cdvd = To-ULong $(if ($null -ne $m.cdvdSectors) { $m.cdvdSectors } else { $m.cdvd })
            $row.binds = To-ULong $m.binds
            $row.calls = To-ULong $m.calls
            $row.syscalls = To-ULong $m.syscalls
            $exitReq = [bool]$m.exitRequested
            $row.exitReq = $exitReq
            if (-not $row.serial -and $m.serial) { $row.serial = [string]$m.serial }
        } catch {
            $row.note = "metrics parse failed: $_"
        }
    } else {
        # blocker-trace / log scrape fallback
        $row.pc = Get-Metric 'PC=(0x[0-9A-Fa-f]+)' $joined
        $claimLine = Get-Metric '(?m)^\s*claim:\s*(.+)$' $joined
        $claimOr = {
            param([string]$key, [string]$fb, [string]$claim, [string]$src)
            if ($claim) {
                $cm = [regex]::Match($claim, "$key=([0-9]+)", "IgnoreCase")
                if ($cm.Success) { return $cm.Groups[1].Value }
            }
            return (Get-Metric $fb $src)
        }
        $pxS = & $claimOr 'px' 'px=([0-9]+)' $claimLine $joined
        $row.px = if ($pxS) { To-ULong $pxS } else { $null }
        $row.prims = To-ULong (& $claimOr 'prims' 'prims=([0-9]+)' $claimLine $joined)
        $row.gifP1 = To-ULong (& $claimOr 'gifP1' 'gifPath1=([0-9]+)' $claimLine $joined)
        $row.gifP2 = To-ULong (& $claimOr 'gifP2' 'gifPath2=([0-9]+)' $claimLine $joined)
        $row.gifP3 = To-ULong (& $claimOr 'gifP3' 'gifPath3=([0-9]+)' $claimLine $joined)
        $row.dmac = To-ULong (Get-Metric 'dmac=([0-9]+)' $joined)
        $row.cdvd = To-ULong (Get-Metric 'cdvdSectors=([0-9]+)' $joined)
        $row.binds = To-ULong (Get-Metric 'binds=([0-9]+)' $joined)
        $row.calls = To-ULong (Get-Metric 'calls=([0-9]+)' $joined)
        $row.syscalls = To-ULong (Get-Metric 'syscalls=([0-9]+)' $joined)
        $er = Get-Metric 'exitRequested=(True|False)' $joined
        if ($er) {
            $exitReq = ($er -match 'True')
            $row.exitReq = $exitReq
        }
        # Treat non-empty PC or claim as metrics present
        $hasMetrics = ($row.pc -or $null -ne $row.px -or $null -ne $row.binds)
    }

    $row.status = Classify-RunStatus -ExitCode ([int]$exit) -HasMetrics $hasMetrics `
        -JoinedLog $joined -ExitRequested $exitReq
    return [pscustomobject]$row
}

function Fmt-Cell($v) {
    if ($null -eq $v -or $v -eq "") { return "—" }
    return [string]$v
}

function Fmt-Delta($old, $new) {
    if ($null -eq $old -and $null -eq $new) { return "—" }
    if ($null -eq $old) { return "n/a→$new" }
    if ($null -eq $new) { return "$old→n/a" }
    $d = [long]$new - [long]$old
    if ($d -eq 0) { return "$new (=)" }
    $sign = if ($d -gt 0) { "+" } else { "" }
    return "$old→$new ($sign$d)"
}

# --- Banner ---
Write-Host ""
Write-Host "=== canary-c1-5-fleet-ab (infrastructure A/B — NOT MENU campaign) ==="
Write-Host "Budget:        $Budget ($cycles cycles)"
Write-Host "Titles:        $(($selected | ForEach-Object { $_.id }) -join ', ')"
Write-Host "DLL:           $dll"
Write-Host "HostPresent:   $hostPresent"
Write-Host "NativeMetrics: $NativeMetrics"
Write-Host "TraceRealRpc:  $TraceRealRpc"
Write-Host "Out:           $runRoot"
Write-Host ""
Write-Host "Arms:"
Write-Host "  baseline  DETPS2_IOP_THREADS=(unset) DETPS2_IOP_REAL_RPC=(unset)"
Write-Host "  flag-on   DETPS2_IOP_THREADS=1       DETPS2_IOP_REAL_RPC=1"
Write-Host ""
Write-Host "Doc: docs/IOP_MULTITHREAD_AND_REAL_RPC.md (exit criteria §8)"
Write-Host ""

if ($SkipRun) {
    Restore-Detps2Env
    Write-Host "SkipRun: not launching emulator."
    return [pscustomobject]@{
        canary   = "c1-5-fleet-ab"
        budget   = $Budget
        cycles   = $cycles
        titles   = @($selected | ForEach-Object { $_.id })
        dll      = $dll
        outDir   = $runRoot
        skipped  = $true
        envBaseline = "DETPS2_IOP_THREADS unset; DETPS2_IOP_REAL_RPC unset"
        envFlagOn   = "DETPS2_IOP_THREADS=1; DETPS2_IOP_REAL_RPC=1"
    }
}

$baselineRows = @()
$flagOnRows = @()

try {
    # ===== ARM B: baseline =====
    Write-Host "=== ARM baseline (flags off / product default) ==="
    Clear-C15Flags
    Write-Host "  env DETPS2_IOP_THREADS=$($env:DETPS2_IOP_THREADS) DETPS2_IOP_REAL_RPC=$($env:DETPS2_IOP_REAL_RPC) DETPS2_NO_REAL_RPC=$($env:DETPS2_NO_REAL_RPC)"
    foreach ($t in $selected) {
        Write-Host ""
        Write-Host ">>> $($t.name) ($($t.id))"
        $baselineRows += Invoke-TitleArm -Title $t -ArmName "baseline" -ArmDir $baseDir
    }

    # ===== ARM A: flag-on =====
    Write-Host ""
    Write-Host "=== ARM flag-on (DETPS2_IOP_THREADS=1 + DETPS2_IOP_REAL_RPC=1) ==="
    Set-C15FlagsOn
    Write-Host "  env DETPS2_IOP_THREADS=$($env:DETPS2_IOP_THREADS) DETPS2_IOP_REAL_RPC=$($env:DETPS2_IOP_REAL_RPC) DETPS2_NO_REAL_RPC=$($env:DETPS2_NO_REAL_RPC)"
    foreach ($t in $selected) {
        Write-Host ""
        Write-Host ">>> $($t.name) ($($t.id))"
        $flagOnRows += Invoke-TitleArm -Title $t -ArmName "flag-on" -ArmDir $flagDir
    }
}
finally {
    Restore-Detps2Env
}

# --- Compare ---
$baseMap = @{}
foreach ($r in $baselineRows) { $baseMap[$r.id] = $r }
$flagMap = @{}
foreach ($r in $flagOnRows) { $flagMap[$r.id] = $r }

$comparisons = @()
$flagOnCrashes = 0
$baselineCrashes = 0
$flagOnWorse = 0
$flagOnBetterBoot = 0
$bothRan = 0
$skipped = 0

foreach ($id in $titleIds) {
    if (-not $baseMap.ContainsKey($id) -and -not $flagMap.ContainsKey($id)) { continue }
    $b = if ($baseMap.ContainsKey($id)) { $baseMap[$id] } else { $null }
    $a = if ($flagMap.ContainsKey($id)) { $flagMap[$id] } else { $null }

    $bSt = if ($b) { $b.status } else { "—" }
    $aSt = if ($a) { $a.status } else { "—" }

    if ($bSt -match '^SKIP') { $skipped++ }
    if ($bSt -eq "CRASH" -or $bSt -match '^EXIT-') { $baselineCrashes++ }
    if ($aSt -eq "CRASH" -or $aSt -match '^EXIT-') { $flagOnCrashes++ }

    $flags = @()
    $honest = "neutral"

    # Crash / non-zero exit honesty
    $bOk = $bSt -eq "RAN" -or $bSt -eq "EXIT-REQ"
    $aOk = $aSt -eq "RAN" -or $aSt -eq "EXIT-REQ"

    if ($bSt -match '^SKIP' -or $aSt -match '^SKIP') {
        $honest = "skip"
        $flags += "skip"
    } elseif ($bOk -and -not $aOk) {
        $honest = "REGRESS"
        $flagOnWorse++
        $flags += "flag-on-fail"
    } elseif (-not $bOk -and $aOk) {
        $honest = "improve?"
        $flagOnBetterBoot++
        $flags += "flag-on-recovered"
    } elseif (-not $bOk -and -not $aOk) {
        $honest = "both-bad"
        $flags += "both-fail"
    } else {
        $bothRan++
        # Soft metric deltas (not success claims)
        $bBinds = if ($b) { $b.binds } else { $null }
        $aBinds = if ($a) { $a.binds } else { $null }
        $bCalls = if ($b) { $b.calls } else { $null }
        $aCalls = if ($a) { $a.calls } else { $null }
        $bPx = if ($b) { $b.px } else { $null }
        $aPx = if ($a) { $a.px } else { $null }
        $bCdvd = if ($b) { $b.cdvd } else { $null }
        $aCdvd = if ($a) { $a.cdvd } else { $null }

        if ($null -ne $bBinds -and $null -ne $aBinds -and [long]$aBinds -gt [long]$bBinds) {
            $flags += "binds+"
        }
        if ($null -ne $bCalls -and $null -ne $aCalls -and [long]$aCalls -gt [long]$bCalls) {
            $flags += "calls+"
        }
        if ($null -ne $bPx -and $null -ne $aPx) {
            if ([long]$bPx -gt 0 -and [long]$aPx -eq 0) {
                $flags += "px-drop-to-0"
                $honest = "REGRESS?"
                $flagOnWorse++
            } elseif ([long]$bPx -gt 0 -and [long]$aPx -lt ([long]$bPx * 0.5)) {
                $flags += "px-drop>50%"
            }
        }
        if ($null -ne $bCdvd -and $null -ne $aCdvd -and [long]$bCdvd -gt 0 -and [long]$aCdvd -lt ([long]$bCdvd * 0.5)) {
            $flags += "cdvd-drop>50%"
        }
        if ($flags.Count -eq 0) { $flags += "stable" }
    }

    $name = if ($a -and $a.name) { $a.name } elseif ($b -and $b.name) { $b.name } else { $id }
    $comparisons += [pscustomobject]@{
        id       = $id
        name     = $name
        baseStatus = $bSt
        flagStatus = $aSt
        pc       = "$(Fmt-Cell $(if ($b) { $b.pc })); $(Fmt-Cell $(if ($a) { $a.pc }))"
        px       = (Fmt-Delta $(if ($b) { $b.px }) $(if ($a) { $a.px }))
        binds    = (Fmt-Delta $(if ($b) { $b.binds }) $(if ($a) { $a.binds }))
        calls    = (Fmt-Delta $(if ($b) { $b.calls }) $(if ($a) { $a.calls }))
        dmac     = (Fmt-Delta $(if ($b) { $b.dmac }) $(if ($a) { $a.dmac }))
        cdvd     = (Fmt-Delta $(if ($b) { $b.cdvd }) $(if ($a) { $a.cdvd }))
        gifP3    = (Fmt-Delta $(if ($b) { $b.gifP3 }) $(if ($a) { $a.gifP3 }))
        baseSec  = if ($b) { $b.elapsedSec } else { $null }
        flagSec  = if ($a) { $a.elapsedSec } else { $null }
        realrpcDbg = "$(if ($b) { $b.realrpcDbg } else { 0 })→$(if ($a) { $a.realrpcDbg } else { 0 })"
        flags    = ($flags -join ",")
        honest   = $honest
    }
}

# Overall verdict — never claim green if flag-on introduced crashes
$verdict = "INCONCLUSIVE"
$verdictNote = ""
if ($flagOnCrashes -gt 0 -and $flagOnCrashes -gt $baselineCrashes) {
    $verdict = "FAIL"
    $verdictNote = "flag-on introduced crashes/non-zero exits vs baseline — do not claim C1.5 success"
} elseif ($flagOnWorse -gt 0) {
    $verdict = "REGRESS"
    $verdictNote = "flag-on worse on $flagOnWorse title(s) (crash or hard metric loss) — report honestly, no success claim"
} elseif ($bothRan -eq 0 -and $skipped -eq $titleIds.Count) {
    $verdict = "SKIP"
    $verdictNote = "all titles skipped (media/ISO) — no data"
} elseif ($bothRan -gt 0 -and $flagOnCrashes -eq 0 -and $flagOnWorse -eq 0) {
    $verdict = "STABLE"
    $verdictNote = "both arms completed without flag-on crash; metrics delta only — NOT a MENU / C1.5-done claim"
} else {
    $verdict = "MIXED"
    $verdictNote = "mixed outcomes; inspect per-title rows — no blanket success"
}

# --- Write summary ---
$mdPath = Join-Path $runRoot "summary.md"
$jsonPath = Join-Path $runRoot "summary.json"

$md = @()
$md += "# C1.5 fleet A/B — $stamp"
$md += ""
$md += "> Infrastructure harness only. **Not** a MENU campaign. Does not assert MENU YES or C1.5 merge-ready."
$md += ""
$md += "- **Budget:** $Budget ($cycles cycles)"
$md += "- **DLL:** ``$dll``"
$md += "- **HostPresent:** $hostPresent"
$md += "- **NativeMetrics:** $NativeMetrics"
$md += "- **TraceRealRpc:** $TraceRealRpc"
$md += "- **Baseline env:** ``DETPS2_IOP_THREADS`` unset, ``DETPS2_IOP_REAL_RPC`` unset (product default live-prefer when registry has servers)"
$md += "- **Flag-on env:** ``DETPS2_IOP_THREADS=1`` + ``DETPS2_IOP_REAL_RPC=1`` (clears ``DETPS2_NO_REAL_RPC``)"
$md += "- **Doc:** [docs/IOP_MULTITHREAD_AND_REAL_RPC.md](../../docs/IOP_MULTITHREAD_AND_REAL_RPC.md) §8"
$md += "- **Verdict:** **$verdict** — $verdictNote"
$md += ""
$md += "## Counts"
$md += ""
$md += "| Metric | Value |"
$md += "|--------|-------|"
$md += "| titles selected | $($selected.Count) |"
$md += "| both RAN | $bothRan |"
$md += "| skipped | $skipped |"
$md += "| baseline crash/exit | $baselineCrashes |"
$md += "| flag-on crash/exit | $flagOnCrashes |"
$md += "| flag-on worse | $flagOnWorse |"
$md += "| flag-on recovered | $flagOnBetterBoot |"
$md += ""
$md += "## Per-title (baseline → flag-on)"
$md += ""
$md += "| Title | base | flag-on | px | binds | calls | dmac | cdvd | gifP3 | flags | honest |"
$md += "|-------|------|---------|----|-------|-------|------|------|-------|-------|--------|"
foreach ($c in $comparisons) {
    $md += "| $($c.name) | $($c.baseStatus) | $($c.flagStatus) | $($c.px) | $($c.binds) | $($c.calls) | $($c.dmac) | $($c.cdvd) | $($c.gifP3) | $($c.flags) | **$($c.honest)** |"
}
$md += ""
$md += "## Caveats"
$md += ""
$md += "1. ``LiveRpcDispatchEnabled``: product default is **prefer-live** when ``DETPS2_IOP_REAL_RPC`` is unset; only ``DETPS2_IOP_REAL_RPC=0`` or ``DETPS2_NO_REAL_RPC=1`` hard-off. Flag-on sets explicit ``=1``; baseline leaves default (not HLE-only)."
$md += "2. ``DETPS2_IOP_THREADS`` is read as ``static readonly`` at IOP type init — must be set **before** process start (this harness does; do not rely on mid-process env changes)."
$md += "3. ``LiveRpcHits`` / ``LiveRpcFallbacks`` exist on ``RealSifRpc`` but are **not** yet emitted by ``scoreboard-metrics`` JSON — A/B uses binds/calls/px/dmac/cdvd + exit/crash honesty. Optional ``-TraceRealRpc`` scrapes ``[REALRPC`` debug line counts."
$md += "4. Multi-thread scaffolding: automatic Step-path RR is not full THREADMAN; yield hooks are explicit (see ``Iop.cs`` C1.2–C1.4)."
$md += "5. Pairing: real RPC without threads often still misses registration (doc §4); flag-on always enables both."
$md += "6. Prefer real only when A ≥ B and no new stalls/crashes (doc §4)."
$md += ""
$md += "## Artifacts"
$md += ""
$md += "- baseline logs/metrics: ``$baseDir``"
$md += "- flag-on logs/metrics: ``$flagDir``"
$md += ""

$md -join "`n" | Set-Content -LiteralPath $mdPath -Encoding utf8

$summaryObj = [ordered]@{
    canary          = "c1-5-fleet-ab"
    stamp           = $stamp
    budget          = $Budget
    cycles          = $cycles
    dll             = $dll
    hostPresent     = $hostPresent
    nativeMetrics   = $NativeMetrics
    traceRealRpc    = [bool]$TraceRealRpc
    envBaseline     = @{
        DETPS2_IOP_THREADS  = $null
        DETPS2_IOP_REAL_RPC = $null
        DETPS2_NO_REAL_RPC  = $null
    }
    envFlagOn       = @{
        DETPS2_IOP_THREADS  = "1"
        DETPS2_IOP_REAL_RPC = "1"
        DETPS2_NO_REAL_RPC  = $null
    }
    verdict         = $verdict
    verdictNote     = $verdictNote
    counts          = @{
        selected         = $selected.Count
        bothRan          = $bothRan
        skipped          = $skipped
        baselineCrashes  = $baselineCrashes
        flagOnCrashes    = $flagOnCrashes
        flagOnWorse      = $flagOnWorse
        flagOnRecovered  = $flagOnBetterBoot
    }
    baseline        = @($baselineRows)
    flagOn          = @($flagOnRows)
    comparisons     = @($comparisons)
    outDir          = $runRoot
    summaryMd       = $mdPath
    menuCampaign    = $false
    claimsMenuYes   = $false
}
($summaryObj | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $jsonPath -Encoding utf8

# --- Console summary ---
Write-Host ""
Write-Host "=== C1.5 FLEET A/B SUMMARY ==="
Write-Host "Verdict: $verdict"
Write-Host "  $verdictNote"
Write-Host ""
Write-Host ("{0,-22} {1,-12} {2,-12} {3,-22} {4,-18} {5}" -f "Title", "baseline", "flag-on", "binds", "calls", "honest")
Write-Host ("-" * 100)
foreach ($c in $comparisons) {
    Write-Host ("{0,-22} {1,-12} {2,-12} {3,-22} {4,-18} {5}" -f `
        $c.name, $c.baseStatus, $c.flagStatus, $c.binds, $c.calls, $c.honest)
}
Write-Host ""
Write-Host "summary.md:  $mdPath"
Write-Host "summary.json: $jsonPath"
Write-Host ""
if ($verdict -eq "FAIL" -or $verdict -eq "REGRESS") {
    Write-Host "HONEST: flag-on is NOT a success. Investigate crashes/regressions before claiming C1.5."
} elseif ($verdict -eq "STABLE") {
    Write-Host "HONEST: arms completed; deltas are informational only — not MENU and not merge-done."
}

# Exit codes: 0 = ran harness; 2 = flag-on regress/crash for CI optional gating
$exitCode = 0
if ($verdict -eq "FAIL" -or $verdict -eq "REGRESS") { $exitCode = 2 }
elseif ($verdict -eq "SKIP") { $exitCode = 1 }

[pscustomobject]@{
    canary     = "c1-5-fleet-ab"
    verdict    = $verdict
    verdictNote = $verdictNote
    stamp      = $stamp
    outDir     = $runRoot
    summaryMd  = $mdPath
    summaryJson = $jsonPath
    envBaseline = "DETPS2_IOP_THREADS unset; DETPS2_IOP_REAL_RPC unset"
    envFlagOn   = "DETPS2_IOP_THREADS=1; DETPS2_IOP_REAL_RPC=1"
    baselineCrashes = $baselineCrashes
    flagOnCrashes   = $flagOnCrashes
    flagOnWorse     = $flagOnWorse
    comparisons = $comparisons
    exitCode    = $exitCode
}

exit $exitCode
