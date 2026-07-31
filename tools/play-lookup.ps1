<#
.SYNOPSIS
  Play! HLE oracle lookup for a DetPS2 title (GameConfig + module map).

.DESCRIPTION
  Mandatory first step before inventing HLE. Does not guess — prints GameConfig hits
  and the wall→Play! source map from docs/PLAY_HLE_ORACLE.md.

.PARAMETER Serial
  Disc serial, e.g. SLUS_210.87

.PARAMETER Title
  Free-text title / search string for GameConfig.xml

.PARAMETER Wall
  Optional wall class: FILEIO, SIF, PAD, CDVD, MC, THREAD, LOADFILE, TITLE

.PARAMETER PlayRoot
  Play! tree root (default C:\Windows\Play)

.EXAMPLE
  pwsh ./tools/play-lookup.ps1 -Serial SLUS_200.24 -Wall FILEIO
#>
[CmdletBinding()]
param(
    [string]$Serial = "",
    [string]$Title = "",
    [ValidateSet("", "FILEIO", "SIF", "PAD", "CDVD", "MC", "THREAD", "LOADFILE", "TITLE")]
    [string]$Wall = "",
    [string]$PlayRoot = "C:\Windows\Play"
)

$ErrorActionPreference = "Stop"
$gameConfig = Join-Path $PlayRoot "GameConfig.xml"
$iop = Join-Path $PlayRoot "Source\iop"

Write-Host "=== Play! oracle lookup ==="
Write-Host "PlayRoot: $PlayRoot"
if (-not (Test-Path $PlayRoot)) {
    Write-Error "Play! tree not found at $PlayRoot. Clone https://github.com/jpd002/Play- or set -PlayRoot."
}

if (-not (Test-Path $gameConfig)) {
    Write-Warning "GameConfig.xml missing under $PlayRoot"
} else {
    $pattern = @()
    if ($Serial) { $pattern += [regex]::Escape($Serial) }
    if ($Title) { $pattern += [regex]::Escape($Title) }
    if ($pattern.Count -eq 0) { $pattern = @("SLUS_|SCUS_|SLES_|SCES_") }
    $rx = ($pattern -join "|")
    Write-Host ""
    Write-Host "=== GameConfig.xml matches ($rx) ==="
    $hits = Select-String -Path $gameConfig -Pattern $rx -Context 0, 12
    if (-not $hits) {
        Write-Host "  (no entry — use generic IOP HLE only; still open Play! modules below)"
    } else {
        $hits | ForEach-Object {
            Write-Host "---"
            $_.Context.PreContext | ForEach-Object { Write-Host "  $_" }
            Write-Host "> $($_.Line)"
            $_.Context.PostContext | ForEach-Object { Write-Host "  $_" }
        }
        Write-Host ""
        Write-Host "Policy: GameConfig patches are TITLE_LOCAL candidates only after structural confirmation."
    }
}

$map = @{
    FILEIO   = @("Iop_FileIo.cpp", "Iop_FileIoHandler1000.cpp", "Iop_FileIoHandler2100.cpp", "Iop_FileIoHandler2200.cpp")
    SIF      = @("Iop_SifCmd.cpp", "Iop_SifMan.cpp", "Iop_SifManNull.cpp", "Iop_Thsema.cpp")
    PAD      = @("Iop_PadMan.cpp")
    CDVD     = @("Iop_Cdvdfsv.cpp", "Iop_Cdvdman.cpp")
    MC       = @("Iop_McServ.cpp")
    THREAD   = @("Iop_Thbase.cpp", "Iop_Thsema.cpp", "Iop_Thmsgbx.cpp", "Iop_Thvpool.cpp")
    LOADFILE = @("Iop_Loadcore.cpp", "Iop_Modload.cpp")
    TITLE    = @("..\..\GameConfig.xml")
}

Write-Host ""
Write-Host "=== Wall → Play! module map ==="
$keys = if ($Wall) { @($Wall) } else { $map.Keys | Sort-Object }
foreach ($k in $keys) {
    Write-Host "[$k]"
    foreach ($f in $map[$k]) {
        $full = if ($f.StartsWith("..")) {
            Join-Path $PlayRoot ($f -replace '^\.\.\\', '' -replace '\\', [IO.Path]::DirectorySeparatorChar)
        } else {
            Join-Path $iop $f
        }
        $ok = Test-Path $full
        Write-Host ("  {0}  {1}" -f ($(if ($ok) { "OK" } else { "MISS" })), $full)
    }
}

Write-Host ""
Write-Host "=== Next steps (required) ==="
Write-Host "1. Read the Play! handlers for your wall — port ABI/side-effects into DetPS2 C#."
Write-Host "2. Prefer SHARED HLE (RealSifRpc / kernel / CDVD) over GameQuirks."
Write-Host "3. If still unsure on live state: PCSX2 + PINE (same ISO). Do not guess."
Write-Host "4. Score progress with: pwsh ./tools/scoreboard.ps1 -Budget diagnose"
Write-Host "Full policy: docs/PLAY_HLE_ORACLE.md + docs/AGENT_SOP.md"
