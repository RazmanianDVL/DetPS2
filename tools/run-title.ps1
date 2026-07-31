<#
.SYNOPSIS
  Run one commercial title under DetPS2 with fixed cycle budgets.

.PARAMETER Media
  Path to user-media-*.json (relative to repo root OK)

.PARAMETER Budget
  diagnose (20M) | verify (50M) | claim (100M) | custom via -Cycles

.PARAMETER HostPresent
  Drive OnHostPresent (required for Midway assists / many menus)

.PARAMETER PadInject
  Optional pad-inject with default MK-style presses instead of blocker-trace

.EXAMPLE
  pwsh ./tools/run-title.ps1 -Media user-media-mk.json -Budget diagnose
  pwsh ./tools/run-title.ps1 -Media user-media-mk.json -Budget claim -PadInject
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Media,
    [ValidateSet("diagnose", "verify", "claim", "custom")]
    [string]$Budget = "diagnose",
    [ulong]$Cycles = 0,
    [switch]$HostPresent = $true,
    [switch]$PadInject,
    [string]$BuildOut = "out/scoreboard-build",
    [string]$TraceDir = "out/traces",
    [string[]]$FindString = @(),
    [switch]$SkipBuild
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
if ($Cycles -eq 0) { throw "Cycles must be > 0" }

Remove-Item Env:DETPS2_SEMA_STALL_YIELD -ErrorAction SilentlyContinue
$env:DETPS2_TRACE_BIOS = if ($env:DETPS2_TRACE_BIOS) { $env:DETPS2_TRACE_BIOS } else { "1" }

New-Item -ItemType Directory -Force -Path $TraceDir | Out-Null
New-Item -ItemType Directory -Force -Path $BuildOut | Out-Null

$mediaPath = if ([IO.Path]::IsPathRooted($Media)) { $Media } else { Join-Path $repoRoot $Media }
if (-not (Test-Path $mediaPath)) { throw "Media config not found: $mediaPath" }

if (-not $SkipBuild) {
    Write-Host "Building Release → $BuildOut ..."
    dotnet build (Join-Path $repoRoot "src/DetPS2.Core/DetPS2.Core.csproj") -c Release -o $BuildOut --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

$dll = Join-Path $BuildOut "DetPS2.Core.dll"
if (-not (Test-Path $dll)) { throw "Missing $dll" }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$base = [IO.Path]::GetFileNameWithoutExtension($Media)
$outLog = Join-Path $TraceDir "$base-$Budget-$stamp-out.txt"
$errLog = Join-Path $TraceDir "$base-$Budget-$stamp-err.txt"

$args = @("exec", $dll)
if ($PadInject) {
    $args += @(
        "pad-inject", $mediaPath,
        "--cycles=$Cycles",
        "--press=START:15000000:1500000",
        "--press=CROSS:25000000:2000000",
        "--press=DOWN:35000000:800000",
        "--press=CROSS:45000000:2000000",
        "--press=UP:55000000:600000",
        "--press=CROSS:65000000:2000000"
    )
} else {
    $args += @("blocker-trace", $mediaPath, "--cycles=$Cycles")
}
if ($HostPresent) { $args += "--host-present" }
foreach ($s in $FindString) { $args += "--find-string=$s" }

Write-Host "RUN: dotnet $($args -join ' ')"
Write-Host "logs: $outLog / $errLog"

$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath "dotnet" -ArgumentList $args -WorkingDirectory $repoRoot `
    -NoNewWindow -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog
Wait-Process -Id $p.Id
$sw.Stop()
$exit = $p.ExitCode

# Parse metrics from combined logs
$text = @()
if (Test-Path $outLog) { $text += Get-Content $outLog -Raw }
if (Test-Path $errLog) { $text += Get-Content $errLog -Raw }
$joined = $text -join "`n"

function Get-Metric([string]$pattern, [string]$src) {
    $m = [regex]::Match($src, $pattern, "IgnoreCase")
    if ($m.Success) { return $m.Groups[1].Value }
    return ""
}

$result = [ordered]@{
    media       = $Media
    budget      = $Budget
    cycles      = $Cycles
    exitCode    = $exit
    elapsedSec  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    titleId     = (Get-Metric '\[([^\]]+)\] Booted' $joined)
    bootSerial  = (Get-Metric 'Booted ([^\s]+)' $joined)
    pc          = (Get-Metric 'PC=(0x[0-9A-Fa-f]+)' $joined)
    px          = (Get-Metric 'px=([0-9]+)' $joined)
    gifPath3    = (Get-Metric 'gifPath3=([0-9]+)' $joined)
    dmac        = (Get-Metric 'dmac=([0-9]+)' $joined)
    cdvd        = (Get-Metric 'cdvdSectors=([0-9]+)' $joined)
    syscalls    = (Get-Metric 'syscalls=([0-9]+)' $joined)
    binds       = (Get-Metric 'binds=([0-9]+)' $joined)
    calls       = (Get-Metric 'calls=([0-9]+)' $joined)
    exitReq     = (Get-Metric 'exitRequested=(True|False)' $joined)
    outLog      = $outLog
    errLog      = $errLog
}

# Heuristic menu bar (not a formal MENU YES claim)
$pxN = 0; [void][ulong]::TryParse([string]$result.px, [ref]$pxN)
$gifN = 0; [void][int]::TryParse([string]$result.gifPath3, [ref]$gifN)
$menu = "No"
if ($pxN -gt 10000 -and $gifN -ge 10) { $menu = "NEAR?" }
if ($pxN -gt 100000 -and $gifN -ge 12) { $menu = "LIKELY-NEAR" }
# GoW-style: any real GS
if ($pxN -gt 0 -and $gifN -gt 0) { if ($menu -eq "No") { $menu = "GS?" } }
$result.menuHeuristic = $menu

$jsonPath = Join-Path $TraceDir "$base-$Budget-$stamp.json"
($result | ConvertTo-Json -Depth 4) | Set-Content $jsonPath -Encoding utf8

Write-Host ""
Write-Host "=== RESULT ==="
$result.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-14} {1}" -f $_.Key, $_.Value) }
Write-Host "json: $jsonPath"

# Return object for scoreboard
[pscustomobject]$result
