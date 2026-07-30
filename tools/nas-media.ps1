<#
.SYNOPSIS
  Probe the PS2 dump library on the operator NAS and scaffold user-media JSON.

.DESCRIPTION
  Probes known UNC roots (never prints secrets beyond path existence / filenames).
  Prefer UNC media paths in gitignored user-media-*.json — do not copy ISOs to C:.

.PARAMETER Probe
  Test reachability of known library roots.

.PARAMETER List
  List first 30 ISO/CUE/CHD files under the first reachable root.

.PARAMETER Search
  Filename pattern (wildcard, e.g. *Burnout* or *SLUS*).

.PARAMETER WriteUserMedia
  Write a user-media JSON template.

.PARAMETER Serial
  Optional disc serial for the template title entry (e.g. SLUS_210.87).

.PARAMETER Id
  Title id for the JSON (default: derived from serial or "title").

.PARAMETER Title
  Human title string for the JSON entry.

.PARAMETER IsoPath
  Explicit ISO path to put in the template (default: first Search hit or placeholder).

.PARAMETER Out
  Output path for -WriteUserMedia (default: user-media-<id>.json at repo root).

.PARAMETER Root
  Override library root UNC.

.EXAMPLE
  pwsh ./tools/nas-media.ps1 -Probe
  pwsh ./tools/nas-media.ps1 -List
  pwsh ./tools/nas-media.ps1 -Search "*Shaolin*"
  pwsh ./tools/nas-media.ps1 -WriteUserMedia -Serial SLUS_210.87 -Search "*Shaolin*" -Out user-media-mk.json
#>
[CmdletBinding()]
param(
    [switch]$Probe,
    [switch]$List,
    [string]$Search = "",
    [switch]$WriteUserMedia,
    [string]$Serial = "",
    [string]$Id = "",
    [string]$Title = "",
    [string]$IsoPath = "",
    [string]$Out = "",
    [string]$Root = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

# Known library roots (operator LAN). Existence only — never embed credentials.
$defaultRoots = @(
    "\\Home_NAS\ND\Emulation\Playstation 2",
    "\\Home_NAS\ND\Emulation\PlayStation 2",
    "\\192.168.0.17\ND\Emulation\Playstation 2",
    "\\192.168.0.17\ND\Emulation\PlayStation 2",
    "\\HOME_NAS\ND\Emulation\Playstation 2"
)

function Test-LibraryRoot {
    param([string]$Path)
    if (-not $Path) { return $false }
    try {
        return [bool](Test-Path -LiteralPath $Path)
    } catch {
        return $false
    }
}

function Get-ReachableRoots {
    param([string]$Override)
    $list = @()
    if ($Override) { $list += $Override }
    $list += $defaultRoots
    $list = $list | Select-Object -Unique
    $hit = @()
    foreach ($r in $list) {
        $ok = Test-LibraryRoot $r
        $hit += [pscustomobject]@{ Path = $r; Reachable = $ok }
    }
    return $hit
}

function Find-BiosPath {
    $docsBios = Join-Path $env:USERPROFILE "Documents\PCSX2\bios"
    $candidates = @()
    if (Test-Path -LiteralPath $docsBios) {
        $candidates += Get-ChildItem -LiteralPath $docsBios -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'scph.?70008|SCPH.?70008|SCPH-70008|70008' }
        # Prefer exact SCPH70008 naming; else any .bin in bios dir as last resort listed only
        if (-not $candidates) {
            $candidates += Get-ChildItem -LiteralPath $docsBios -Filter "*.bin" -File -ErrorAction SilentlyContinue |
                Select-Object -First 3
        }
    }
    if ($candidates) {
        # Prefer name containing 70008
        $pref = $candidates | Where-Object { $_.Name -match '70008' } | Select-Object -First 1
        if (-not $pref) { $pref = $candidates | Select-Object -First 1 }
        return $pref.FullName.Replace('\', '/')
    }
    return "C:/path/to/SCPH70008.bin"
}

function Get-IsoFiles {
    param([string]$RootPath, [string]$Pattern, [int]$Max = 30)
    if (-not (Test-LibraryRoot $RootPath)) { return @() }
    $filter = if ($Pattern) { $Pattern } else { "*" }
    $exts = @("*.iso", "*.ISO", "*.cue", "*.CUE", "*.chd", "*.CHD")
    $files = @()
    foreach ($ext in $exts) {
        if ($files.Count -ge $Max) { break }
        try {
            $batch = Get-ChildItem -LiteralPath $RootPath -Filter $ext -File -ErrorAction SilentlyContinue
            if ($Pattern) {
                $batch = $batch | Where-Object { $_.Name -like $Pattern }
            }
            foreach ($f in $batch) {
                $files += $f
                if ($files.Count -ge $Max) { break }
            }
        } catch {
            # ignore enumeration errors (permissions / partial share)
        }
    }
    # Recursive shallow (one level) if root has subdirs and still empty
    if ($files.Count -eq 0) {
        try {
            $subs = Get-ChildItem -LiteralPath $RootPath -Directory -ErrorAction SilentlyContinue | Select-Object -First 20
            foreach ($sub in $subs) {
                if ($files.Count -ge $Max) { break }
                foreach ($ext in $exts) {
                    if ($files.Count -ge $Max) { break }
                    $batch = Get-ChildItem -LiteralPath $sub.FullName -Filter $ext -File -Recurse -Depth 2 -ErrorAction SilentlyContinue
                    if ($Pattern) { $batch = $batch | Where-Object { $_.Name -like $Pattern } }
                    foreach ($f in $batch) {
                        $files += $f
                        if ($files.Count -ge $Max) { break }
                    }
                }
            }
        } catch { }
    }
    return $files | Select-Object -First $Max
}

# Default action
if (-not $Probe -and -not $List -and -not $Search -and -not $WriteUserMedia) {
    $Probe = $true
}

Write-Host "=== nas-media ==="
Write-Host "(Reports path existence and public filenames only — no credentials.)"
Write-Host ""

$roots = Get-ReachableRoots -Override $Root
$firstOk = $roots | Where-Object { $_.Reachable } | Select-Object -First 1

if ($Probe) {
    Write-Host "=== Probe library roots ==="
    foreach ($r in $roots) {
        $mark = if ($r.Reachable) { "OK  " } else { "MISS" }
        Write-Host ("  [{0}] {1}" -f $mark, $r.Path)
    }
    if (-not $firstOk) {
        Write-Warning "No library root reachable. Check LAN/VPN/SMB and -Root."
    } else {
        Write-Host "Primary: $($firstOk.Path)"
    }
    Write-Host ""
}

$workRoot = if ($Root -and (Test-LibraryRoot $Root)) { $Root } elseif ($firstOk) { $firstOk.Path } else { $null }

if ($List -or $Search) {
    if (-not $workRoot) {
        Write-Warning "Cannot list/search — no reachable root."
    } else {
        $pat = if ($Search) { $Search } else { "*" }
        $max = if ($List -and -not $Search) { 30 } else { 30 }
        Write-Host "=== List/Search under primary (max $max) ==="
        Write-Host "Root: $workRoot"
        if ($Search) { Write-Host "Pattern: $Search" }
        $files = Get-IsoFiles -RootPath $workRoot -Pattern $(if ($Search) { $Search } else { $null }) -Max $max
        if (-not $files -or $files.Count -eq 0) {
            Write-Host "  (no matching ISO/CUE/CHD)"
        } else {
            $n = 0
            foreach ($f in $files) {
                $n++
                $rel = $f.FullName
                if ($rel.StartsWith($workRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    $rel = $rel.Substring($workRoot.Length).TrimStart('\')
                }
                $sizeMb = [math]::Round($f.Length / 1MB, 0)
                Write-Host ("  {0,2}. {1}  ({2} MB)" -f $n, $rel, $sizeMb)
            }
            Write-Host "  ($($files.Count) shown)"
        }
        Write-Host ""
    }
}

if ($WriteUserMedia) {
    $bios = Find-BiosPath
    if (-not $Id) {
        if ($Serial) {
            $Id = ($Serial -replace '[^\w]+', '-').ToLowerInvariant()
        } else {
            $Id = "title"
        }
    }
    if (-not $Title) {
        $Title = if ($Serial) { "Title $Serial" } else { "Untitled" }
    }

    $pathForJson = $IsoPath
    if (-not $pathForJson) {
        if ($workRoot -and $Search) {
            $hits = Get-IsoFiles -RootPath $workRoot -Pattern $Search -Max 1
            if ($hits) { $pathForJson = $hits[0].FullName }
        }
    }
    if (-not $pathForJson) {
        $base = if ($workRoot) { $workRoot } else { "\\Home_NAS\ND\Emulation\Playstation 2" }
        $pathForJson = Join-Path $base "REPLACE_WITH_ISO.iso"
    }

    # Normalize to forward slashes for JSON portability on Windows Core
    $pathJson = $pathForJson.Replace('\', '/')
    $biosJson = $bios.Replace('\', '/')

    $obj = [ordered]@{
        biosPath    = $biosJson
        titles      = @(
            [ordered]@{
                id     = $Id
                title  = $Title
                path   = $pathJson
                kind   = "iso"
                serial = $Serial
            }
        )
        bootCycles  = 5000000
        sampleEvery = 100000
    }

    if (-not $Out) {
        $Out = Join-Path $repoRoot "user-media-$Id.json"
    } elseif (-not [IO.Path]::IsPathRooted($Out)) {
        $Out = Join-Path $repoRoot $Out
    }

    $json = $obj | ConvertTo-Json -Depth 6
    Set-Content -LiteralPath $Out -Value $json -Encoding utf8
    Write-Host "=== WriteUserMedia ==="
    Write-Host "  Out : $Out"
    Write-Host "  Id  : $Id"
    if ($Serial) { Write-Host "  Ser : $Serial" }
    Write-Host "  ISO : $(if (Test-Path -LiteralPath $pathForJson) { 'exists' } else { 'path not yet present' })"
    Write-Host "  BIOS: $(if ($biosJson -match 'path/to') { 'placeholder — place SCPH70008 under Documents\PCSX2\bios' } else { 'found under Documents\PCSX2\bios' })"
    Write-Host "  Keep gitignored when it embeds private absolute paths. See docs/LIBRARY_SAMPLING.md"
    Write-Host ""
}

if (-not $firstOk -and -not $WriteUserMedia) {
    exit 1
}

Write-Host "Done."
