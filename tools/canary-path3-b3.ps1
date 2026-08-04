<#
.SYNOPSIS
  PATH3/FQC regression canary: Burnout 3 diagnose-tier host-present Soft-GS run.

.DESCRIPTION
  Runs Burnout 3 (burnout-only.json by default) via DetPS2.Core blocker-trace at
  diagnose budget (20M) with --host-present. Default environment: JRGUARD64 stays
  ON (DETPS2_DISABLE_JRGUARD64 must not be set). SEMA_STALL_YIELD forced OFF.

  Purpose: quick PATH3 / M3P / FQC hold-drain regression after GIF/VIF/DMAC changes.
  Prefer diagnose (20M) first — do not open with 100M. Raise -Cycles / -Budget claim
  only when asserting a MENU / path-health claim.

  Captures claim + gif-path (+ softgs/gif-pkts/gif-tags) lines under out/canaries/.

  Does NOT edit Core .cs. Prefer an existing Release DetPS2.Core.dll; optional -Build.

.PARAMETER Media
  user-media / burnout JSON (default: burnout-only.json). Also accepts a
  user-media-*.json that lists Burnout 3.

.PARAMETER Budget
  diagnose (20M) | verify (50M) | claim (100M) | custom via -Cycles.
  Default: diagnose (PATH3/FQC quick regression).

.PARAMETER Cycles
  When -Budget custom, EE cycle count. Ignored for named budgets unless Budget=custom.

.PARAMETER HostPresent
  Pass --host-present (default: $true). Use -HostPresent:$false to disable.

.PARAMETER Dll
  Path to DetPS2.Core.dll. Default: src/DetPS2.Core/bin/Release/net9.0/DetPS2.Core.dll
  then out/scoreboard-build/DetPS2.Core.dll if missing.

.PARAMETER BuildOut
  When -Build, build output dir (default: out/scoreboard-build).

.PARAMETER Build
  Rebuild Release Core into -BuildOut before run.

.PARAMETER OutDir
  Claim/log capture directory (default: out/canaries).

.PARAMETER SkipRun
  Only resolve paths / print planned command (no emulator).

.EXAMPLE
  # Default PATH3/FQC diagnose canary (20M, JR guard ON, SEMA_OFF)
  pwsh ./tools/canary-path3-b3.ps1

  # Explicit media
  pwsh ./tools/canary-path3-b3.ps1 -Media burnout-only.json -Budget diagnose

  # After a suspected path fix, longer soak (still JR guard ON)
  pwsh ./tools/canary-path3-b3.ps1 -Budget verify
#>
[CmdletBinding()]
param(
    [string]$Media = "burnout-only.json",
    [ValidateSet("diagnose", "verify", "claim", "custom")]
    [string]$Budget = "diagnose",
    [ulong]$Cycles = 0,
    [bool]$HostPresent = $true,
    [string]$Dll = "",
    [string]$BuildOut = "out/scoreboard-build",
    [switch]$Build,
    [string]$OutDir = "out/canaries",
    [switch]$SkipRun
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$budgetMap = @{
    diagnose = [ulong]20000000
    verify   = [ulong]50000000
    claim    = [ulong]100000000
}
if ($Budget -ne "custom") {
    $Cycles = $budgetMap[$Budget]
}
if ($Cycles -eq 0) { throw "Cycles must be > 0 (use -Budget or -Budget custom -Cycles N)" }

# --- SEMA_OFF (claims / regression policy) ---
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue

# --- JR guard ON (PATH3 canary must not A/B with JRGUARD64 off) ---
if ($env:DETPS2_DISABLE_JRGUARD64 -eq "1") {
    Write-Warning "Clearing DETPS2_DISABLE_JRGUARD64=1 — path3-b3 canary requires JR guard ON"
}
Remove-Item Env:DETPS2_DISABLE_JRGUARD64 -ErrorAction SilentlyContinue

if (-not $env:DETPS2_TRACE_BIOS) { $env:DETPS2_TRACE_BIOS = "1" }

$mediaPath = if ([IO.Path]::IsPathRooted($Media)) { $Media } else { Join-Path $repoRoot $Media }
if (-not (Test-Path -LiteralPath $mediaPath)) { throw "Media config not found: $mediaPath" }

$releaseDll = Join-Path $repoRoot "src/DetPS2.Core/bin/Release/net9.0/DetPS2.Core.dll"
$scoreDll   = Join-Path $repoRoot (Join-Path $BuildOut "DetPS2.Core.dll")

if ($Build) {
    New-Item -ItemType Directory -Force -Path $BuildOut | Out-Null
    Write-Host "Building Release → $BuildOut ..."
    dotnet build (Join-Path $repoRoot "src/DetPS2.Core/DetPS2.Core.csproj") -c Release -o $BuildOut --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
    $Dll = $scoreDll
}

if (-not $Dll) {
    if (Test-Path -LiteralPath $releaseDll) { $Dll = $releaseDll }
    elseif (Test-Path -LiteralPath $scoreDll) { $Dll = $scoreDll }
    else { throw "DetPS2.Core.dll not found. Tried:`n  $releaseDll`n  $scoreDll`nPass -Dll or -Build." }
}
$Dll = if ([IO.Path]::IsPathRooted($Dll)) { $Dll } else { Join-Path $repoRoot $Dll }
if (-not (Test-Path -LiteralPath $Dll)) { throw "Missing DLL: $Dll" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$mediaStem = [IO.Path]::GetFileNameWithoutExtension($Media)
$base = "canary-path3-b3-$mediaStem-$Budget-$stamp"
$outLog   = Join-Path $OutDir "$base-out.txt"
$errLog   = Join-Path $OutDir "$base-err.txt"
$claimLog = Join-Path $OutDir "$base-claims.txt"
$jsonPath = Join-Path $OutDir "$base.json"

$argList = @("exec", $Dll, "blocker-trace", $mediaPath, "--cycles=$Cycles")
if ($HostPresent) { $argList += "--host-present" }

Write-Host "=== canary-path3-b3 ==="
Write-Host "Media:        $mediaPath"
Write-Host "DLL:          $Dll"
Write-Host "Budget:       $Budget ($Cycles cycles)"
Write-Host "HostPresent:  $HostPresent"
Write-Host "JRGUARD64:    ON (DETPS2_DISABLE_JRGUARD64 cleared)"
Write-Host "SEMA_STALL:   OFF"
Write-Host "RUN: dotnet $($argList -join ' ')"
Write-Host "logs: $outLog / $errLog"
Write-Host "claims: $claimLog"

if ($SkipRun) {
    Write-Host "SkipRun: not launching emulator."
    return [pscustomobject]@{
        media = $Media; budget = $Budget; cycles = $Cycles; dll = $Dll
        hostPresent = $HostPresent; jrGuardOn = $true; skipped = $true
        outDir = $OutDir
    }
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath "dotnet" -ArgumentList $argList -WorkingDirectory $repoRoot `
    -NoNewWindow -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog
Wait-Process -Id $p.Id
$sw.Stop()
$exit = $p.ExitCode

$text = @()
if (Test-Path -LiteralPath $outLog) { $text += Get-Content -LiteralPath $outLog -Raw -ErrorAction SilentlyContinue }
if (Test-Path -LiteralPath $errLog) { $text += Get-Content -LiteralPath $errLog -Raw -ErrorAction SilentlyContinue }
$joined = ($text -join "`n")

# PATH3/FQC scrape: claim + gif-path (m3p/heldP3*) + softgs
$claimPatterns = @(
    '^\s*claim:\s*',
    '^\s*softgs:\s*',
    '^\s*gif-path:\s*',
    '^\s*gif-pkts:\s*',
    '^\s*gif-tags:\s*',
    'MENU\?',
    '\[B3\]',
    '\[Burnout',
    'MSKPATH3|Path3|SetMskPath3|FQC',
    'Booted '
)
$claimLines = @()
foreach ($line in ($joined -split "`r?`n")) {
    foreach ($pat in $claimPatterns) {
        if ($line -match $pat) {
            $claimLines += $line
            break
        }
    }
}
$header = @(
    "# canary-path3-b3 claims extract (PATH3/FQC regression)"
    "# stamp=$stamp budget=$Budget cycles=$Cycles exit=$exit elapsedSec=$([math]::Round($sw.Elapsed.TotalSeconds,1))"
    "# media=$mediaPath"
    "# dll=$Dll"
    "# DETPS2_DISABLE_JRGUARD64=(unset) JR guard ON"
    "# DETPS2_SEMA_STALL_YIELD=(unset/OFF)"
    ""
)
($header + $claimLines) | Set-Content -LiteralPath $claimLog -Encoding utf8

function Get-Metric([string]$pattern, [string]$src) {
    $m = [regex]::Match($src, $pattern, "IgnoreCase")
    if ($m.Success) { return $m.Groups[1].Value }
    return ""
}

$claimLine = Get-Metric '(?m)^\s*claim:\s*(.+)$' $joined
$gifPathLine = Get-Metric '(?m)^\s*gif-path:\s*(.+)$' $joined
$softgsLine = Get-Metric '(?m)^\s*softgs:\s*(.+)$' $joined

function Claim-Or([string]$claimKey, [string]$fallbackPattern) {
    if ($claimLine) {
        $cm = [regex]::Match($claimLine, "$claimKey=([0-9]+)", "IgnoreCase")
        if ($cm.Success) { return $cm.Groups[1].Value }
    }
    return (Get-Metric $fallbackPattern $joined)
}

function GifPath-Field([string]$key) {
    if (-not $gifPathLine) { return "" }
    $m = [regex]::Match($gifPathLine, "$key=([^\s]+)", "IgnoreCase")
    if ($m.Success) { return $m.Groups[1].Value }
    return ""
}

$result = [ordered]@{
    canary      = "path3-b3"
    media       = $Media
    budget      = $Budget
    cycles      = $Cycles
    hostPresent = $HostPresent
    jrGuardOn   = $true
    seMaStallYieldOff = $true
    exitCode    = $exit
    elapsedSec  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    dll         = $Dll
    titleId     = (Get-Metric '\[([^\]]+)\] Booted' $joined)
    bootSerial  = (Get-Metric 'Booted ([^\s]+)' $joined)
    pc          = (Get-Metric 'PC=(0x[0-9A-Fa-f]+)' $joined)
    claim       = $claimLine
    gifPath     = $gifPathLine
    softgs      = $softgsLine
    px          = (Claim-Or 'px' 'px=([0-9]+)')
    prims       = (Claim-Or 'prims' 'prims=([0-9]+)')
    gifP1       = (Claim-Or 'gifP1' 'gifPath1=([0-9]+)')
    gifP2       = (Claim-Or 'gifP2' 'gifPath2=([0-9]+)')
    gifP3       = (Claim-Or 'gifP3' 'gifPath3=([0-9]+)')
    imgBytes    = (Claim-Or 'imgBytes' 'imgBytes=([0-9]+)')
    dispfbPx    = (Claim-Or 'dispfbPx' 'dispfbPx=([0-9]+)')
    expandHits  = (Claim-Or 'expandHits' 'expandHits=([0-9]+)')
    m3p         = (GifPath-Field 'm3p')
    heldP3n     = (GifPath-Field 'heldP3n')
    heldP3qwc   = (GifPath-Field 'heldP3qwc')
    heldSubmits = (GifPath-Field 'heldSubmits')
    mskPath3    = (GifPath-Field 'mskPath3')
    dmac        = (Get-Metric 'dmac=([0-9]+)' $joined)
    cdvd        = (Get-Metric 'cdvdSectors=([0-9]+)' $joined)
    outLog      = $outLog
    errLog      = $errLog
    claimLog    = $claimLog
}

($result | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $jsonPath -Encoding utf8

Write-Host ""
Write-Host "=== PATH3 B3 CANARY RESULT ==="
$result.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-18} {1}" -f $_.Key, $_.Value) }
Write-Host "json:   $jsonPath"
Write-Host "claims: $claimLog"
Write-Host ""
Write-Host "PATH3/FQC scrape tip: check gif-path m3p / heldP3n / heldP3qwc / p3 vs claim gifP3"

[pscustomobject]$result
