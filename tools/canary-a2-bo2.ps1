<#
.SYNOPSIS
  A2 canary: Blood Omen 2 claim-budget (100M) host-present Soft-GS run.

.DESCRIPTION
  Runs BO2 via DetPS2.Core blocker-trace at claim budget with host present.
  SEMA_STALL_YIELD is forced OFF (SEMA_OFF claims policy).

  Optional DETPS2_DISABLE_JRGUARD64=1 (-DisableJrGuard64) is the A2 success-metric
  experiment path (reproduce pre-JRGUARD64 garbage-jump cascades / A-B vs guard on).
  Default leaves the 64-bit JR guard ON (production path).

  Captures full logs plus extracted claim/softgs/gif-path/MENU lines under out/canaries/.

  Does NOT edit Core .cs. Prefer an existing Release DetPS2.Core.dll; optional -Build.

.PARAMETER Media
  user-media JSON (default: user-media-bloodomen2.json)

.PARAMETER Cycles
  EE cycle budget (default: 100000000 = claim). Override for quick smoke only.

.PARAMETER HostPresent
  Pass --host-present (default: $true). Use -HostPresent:$false to disable.

.PARAMETER DisableJrGuard64
  Set DETPS2_DISABLE_JRGUARD64=1 for A2 success-metric experiment.

.PARAMETER Dll
  Path to DetPS2.Core.dll. Default: src/DetPS2.Core/bin/Release/net9.0/DetPS2.Core.dll
  then out/scoreboard-build/DetPS2.Core.dll if missing.

.PARAMETER BuildOut
  When -Build, dotnet publish/build output dir (default: out/scoreboard-build).

.PARAMETER Build
  Rebuild Release Core into -BuildOut before run.

.PARAMETER OutDir
  Claim/log capture directory (default: out/canaries).

.PARAMETER SkipRun
  Only resolve paths / print planned command (no emulator).

.EXAMPLE
  # Production guard ON (default) — claim 100M (long wall-clock)
  pwsh ./tools/canary-a2-bo2.ps1

  # A2 success metric: JRGUARD64 disabled
  pwsh ./tools/canary-a2-bo2.ps1 -DisableJrGuard64

  # Quick path check only
  pwsh ./tools/canary-a2-bo2.ps1 -Cycles 1000000 -SkipRun:$false
#>
[CmdletBinding()]
param(
    [string]$Media = "user-media-bloodomen2.json",
    [ulong]$Cycles = 100000000,
    [bool]$HostPresent = $true,
    [switch]$DisableJrGuard64,
    [string]$Dll = "",
    [string]$BuildOut = "out/scoreboard-build",
    [switch]$Build,
    [string]$OutDir = "out/canaries",
    [switch]$SkipRun
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

# --- SEMA_OFF (claims policy) ---
Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue

# --- JRGUARD64 A2 experiment ---
if ($DisableJrGuard64) {
    $env:DETPS2_DISABLE_JRGUARD64 = "1"
    Write-Host "A2 metric: DETPS2_DISABLE_JRGUARD64=1 (guard OFF)"
} else {
    Remove-Item Env:DETPS2_DISABLE_JRGUARD64 -ErrorAction SilentlyContinue
    Write-Host "A2 baseline: JRGUARD64 ON (DETPS2_DISABLE_JRGUARD64 unset)"
}

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
$guardTag = if ($DisableJrGuard64) { "jrguard64-off" } else { "jrguard64-on" }
$base = "canary-a2-bo2-$guardTag-$stamp"
$outLog    = Join-Path $OutDir "$base-out.txt"
$errLog    = Join-Path $OutDir "$base-err.txt"
$claimLog  = Join-Path $OutDir "$base-claims.txt"
$jsonPath  = Join-Path $OutDir "$base.json"

$argList = @("exec", $Dll, "blocker-trace", $mediaPath, "--cycles=$Cycles")
if ($HostPresent) { $argList += "--host-present" }

Write-Host "=== canary-a2-bo2 ==="
Write-Host "Media:        $mediaPath"
Write-Host "DLL:          $Dll"
Write-Host "Cycles:       $Cycles"
Write-Host "HostPresent:  $HostPresent"
Write-Host "JRGUARD64:    $(if ($DisableJrGuard64) { 'DISABLED (A2 metric)' } else { 'ON' })"
Write-Host "SEMA_STALL:   OFF"
Write-Host "RUN: dotnet $($argList -join ' ')"
Write-Host "logs: $outLog / $errLog"
Write-Host "claims: $claimLog"

if ($SkipRun) {
    Write-Host "SkipRun: not launching emulator."
    return [pscustomobject]@{
        media = $Media; cycles = $Cycles; dll = $Dll
        disableJrGuard64 = [bool]$DisableJrGuard64
        hostPresent = $HostPresent; skipped = $true
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

# Extract claim-relevant lines for quick scrape
$claimPatterns = @(
    '^\s*claim:\s*',
    '^\s*softgs:\s*',
    '^\s*gif-path:\s*',
    '^\s*gif-pkts:\s*',
    '^\s*gif-tags:\s*',
    'MENU\?',
    '\[BO2\]',
    '\[JRGUARD64\]',
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
    "# canary-a2-bo2 claims extract"
    "# stamp=$stamp guard=$guardTag cycles=$Cycles exit=$exit elapsedSec=$([math]::Round($sw.Elapsed.TotalSeconds,1))"
    "# media=$mediaPath"
    "# dll=$Dll"
    "# DETPS2_DISABLE_JRGUARD64=$(if ($DisableJrGuard64) { '1' } else { '(unset)' })"
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

$result = [ordered]@{
    canary           = "a2-bo2"
    media            = $Media
    cycles           = $Cycles
    hostPresent      = $HostPresent
    disableJrGuard64 = [bool]$DisableJrGuard64
    seMaStallYieldOff = $true
    exitCode         = $exit
    elapsedSec       = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    dll              = $Dll
    titleId          = (Get-Metric '\[([^\]]+)\] Booted' $joined)
    bootSerial       = (Get-Metric 'Booted ([^\s]+)' $joined)
    pc               = (Get-Metric 'PC=(0x[0-9A-Fa-f]+)' $joined)
    claim            = $claimLine
    gifPath          = $gifPathLine
    softgs           = $softgsLine
    px               = (Claim-Or 'px' 'px=([0-9]+)')
    prims            = (Claim-Or 'prims' 'prims=([0-9]+)')
    gifP1            = (Claim-Or 'gifP1' 'gifPath1=([0-9]+)')
    gifP2            = (Claim-Or 'gifP2' 'gifPath2=([0-9]+)')
    gifP3            = (Claim-Or 'gifP3' 'gifPath3=([0-9]+)')
    imgBytes         = (Claim-Or 'imgBytes' 'imgBytes=([0-9]+)')
    dispfbPx         = (Claim-Or 'dispfbPx' 'dispfbPx=([0-9]+)')
    expandHits       = (Claim-Or 'expandHits' 'expandHits=([0-9]+)')
    lit              = (Get-Metric 'lit=([0-9]+/[0-9]+)' $joined)
    dmac             = (Get-Metric 'dmac=([0-9]+)' $joined)
    cdvd             = (Get-Metric 'cdvdSectors=([0-9]+)' $joined)
    outLog           = $outLog
    errLog           = $errLog
    claimLog         = $claimLog
}

($result | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $jsonPath -Encoding utf8

Write-Host ""
Write-Host "=== A2 BO2 CANARY RESULT ==="
$result.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-18} {1}" -f $_.Key, $_.Value) }
Write-Host "json:   $jsonPath"
Write-Host "claims: $claimLog"

[pscustomobject]$result
