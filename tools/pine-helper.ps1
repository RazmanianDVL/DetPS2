<#
.SYNOPSIS
  Locate PCSX2, document/enable PINE, optionally boot an ISO in -batch.

.DESCRIPTION
  Operator helper for the PCSX2 + PINE ground-truth path.
  Does not invent behavior — prints SOP and verifies config keys.

  Required PINE settings (PCSX2.ini [UI] or top-level keys depending on version):
    EnablePINE=true
    PINESlot=28011

.PARAMETER Iso
  Path to ISO to boot (optional; required with -Batch).

.PARAMETER Batch
  Start pcsx2-qt (or found binary) with: -batch -- <Iso>
  One instance only — script refuses if a pcsx2 process is already running.

.PARAMETER CheckConfig
  Locate PCSX2.ini and report EnablePINE / PINESlot status.

.PARAMETER WriteConfigSample
  Write a sample PINE snippet to out/traces/pcsx2-pine-sample.ini
  (never touches user profile unless -ForceUserConfig).

.PARAMETER ForceUserConfig
  With -WriteConfigSample: also merge/append EnablePINE keys into the live
  Documents\PCSX2\inis\PCSX2.ini (operator-only; use carefully).

.PARAMETER Pcsx2Path
  Override binary or install root. Else: $env:PCSX2_PATH, C:\pcsx2, common paths.

.EXAMPLE
  pwsh ./tools/pine-helper.ps1 -CheckConfig
  pwsh ./tools/pine-helper.ps1 -WriteConfigSample
  pwsh ./tools/pine-helper.ps1 -Batch -Iso "\\Home_NAS\ND\Emulation\Playstation 2\game.iso"
#>
[CmdletBinding()]
param(
    [string]$Iso = "",
    [switch]$Batch,
    [switch]$CheckConfig,
    [switch]$WriteConfigSample,
    [switch]$ForceUserConfig,
    [string]$Pcsx2Path = "",
    [int]$PineSlot = 28011
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

function Find-Pcsx2Root {
    param([string]$Override)
    $candidates = @()
    if ($Override) { $candidates += $Override }
    if ($env:PCSX2_PATH) { $candidates += $env:PCSX2_PATH }

    $candidates += @(
        "C:\pcsx2",
        "C:\PCSX2",
        "C:\Program Files\PCSX2",
        "C:\Program Files (x86)\PCSX2",
        (Join-Path $env:LOCALAPPDATA "PCSX2"),
        (Join-Path $env:ProgramFiles "PCSX2"),
        "E:\pcsx2",
        "E:\dev\pcsx2",
        "D:\pcsx2"
    )

    foreach ($c in $candidates) {
        if (-not $c) { continue }
        if (Test-Path -LiteralPath $c -PathType Leaf) {
            # Direct path to exe
            if ($c -match 'pcsx2') { return @{ Root = (Split-Path $c -Parent); Exe = $c } }
        }
        if (Test-Path -LiteralPath $c -PathType Container) {
            $names = @(
                "pcsx2-qt.exe", "pcsx2-qtx64.exe", "pcsx2-qtx64-avx2.exe",
                "pcsx2.exe", "PCSX2.exe"
            )
            foreach ($n in $names) {
                $p = Join-Path $c $n
                if (Test-Path -LiteralPath $p) {
                    return @{ Root = $c; Exe = $p }
                }
            }
            # Nested bin/
            $bin = Join-Path $c "bin"
            if (Test-Path $bin) {
                foreach ($n in $names) {
                    $p = Join-Path $bin $n
                    if (Test-Path -LiteralPath $p) {
                        return @{ Root = $c; Exe = $p }
                    }
                }
            }
        }
    }
    return $null
}

function Find-Pcsx2Ini {
    $paths = @(
        (Join-Path $env:USERPROFILE "Documents\PCSX2\inis\PCSX2.ini"),
        (Join-Path $env:USERPROFILE "Documents\PCSX2\PCSX2.ini"),
        (Join-Path $env:USERPROFILE "Documents\PCSX2\inis\PCSX2_ui.ini")
    )
    if ($env:PCSX2_PATH) {
        $paths = @(
            (Join-Path $env:PCSX2_PATH "inis\PCSX2.ini"),
            (Join-Path $env:PCSX2_PATH "PCSX2.ini")
        ) + $paths
    }
    foreach ($p in $paths) {
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

function Show-Sop {
    Write-Host ""
    Write-Host "=== PCSX2 + PINE SOP (DetPS2) ==="
    Write-Host "1. ONE PCSX2 instance only (PINE binds a single TCP slot)."
    Write-Host "2. Config keys:"
    Write-Host "     EnablePINE=true"
    Write-Host "     PINESlot=$PineSlot"
    Write-Host "3. Boot: pcsx2-qt -batch -- `"<ISO>`"  (avoid -nogui if it hangs on your host)."
    Write-Host "4. Compare DetPS2 PC / mem / flags to PINE reads at the same wall."
    Write-Host "5. Soft-GS is DetPS2 ground truth; PCSX2 is the live LLE oracle — do not guess."
    Write-Host "6. This operator machine has NO iGPU: pin pcsx2-qt to the dGPU if present fails."
    Write-Host "     pwsh ./tools/pin-gpu.ps1 -ListAdapters"
    Write-Host "     pwsh ./tools/pin-gpu.ps1 -ExePath <pcsx2-qt.exe>"
    Write-Host "7. Never publish personal install paths or private UNC ISOs to the wiki."
    Write-Host "Full policy: docs/AGENT_SOP.md § PCSX2 + PINE"
    Write-Host ""
}

# Default: always show location + SOP when no action flags
$anyAction = $Batch -or $CheckConfig -or $WriteConfigSample
if (-not $anyAction) {
    $CheckConfig = $true
}

Write-Host "=== pine-helper ==="
$found = Find-Pcsx2Root -Override $Pcsx2Path
if ($found) {
    Write-Host "PCSX2 exe : $($found.Exe)"
    Write-Host "PCSX2 root: $($found.Root)"
} else {
    Write-Warning "PCSX2 binary not found. Set PCSX2_PATH or pass -Pcsx2Path."
    Write-Host "Searched: C:\pcsx2, env PCSX2_PATH, Program Files, common roots."
}

$ini = Find-Pcsx2Ini
if ($ini) {
    Write-Host "PCSX2.ini : $ini"
} else {
    Write-Host "PCSX2.ini : (not found under Documents\PCSX2)"
}

Show-Sop

if ($CheckConfig) {
    Write-Host "=== CheckConfig ==="
    if (-not $ini) {
        Write-Warning "No PCSX2.ini found. Use -WriteConfigSample, then copy keys into your install's ini."
    } else {
        $raw = Get-Content -LiteralPath $ini -Raw
        $en = [regex]::Match($raw, '(?im)^\s*EnablePINE\s*=\s*(.+)$')
        $sl = [regex]::Match($raw, '(?im)^\s*PINESlot\s*=\s*(.+)$')
        $enVal = if ($en.Success) { $en.Groups[1].Value.Trim() } else { "(missing)" }
        $slVal = if ($sl.Success) { $sl.Groups[1].Value.Trim() } else { "(missing)" }
        Write-Host "  EnablePINE = $enVal"
        Write-Host "  PINESlot   = $slVal"
        $okEn = $enVal -match '^(true|1|yes)$'
        $okSl = $slVal -eq [string]$PineSlot
        if ($okEn -and $okSl) {
            Write-Host "  STATUS: PINE config OK"
        } else {
            Write-Warning "  STATUS: PINE not fully enabled — set EnablePINE=true and PINESlot=$PineSlot"
            Write-Host "  Tip: pwsh ./tools/pine-helper.ps1 -WriteConfigSample"
            Write-Host "       or -WriteConfigSample -ForceUserConfig (edits live ini)"
        }
    }
}

if ($WriteConfigSample) {
    $traceDir = Join-Path $repoRoot "out\traces"
    New-Item -ItemType Directory -Force -Path $traceDir | Out-Null
    $samplePath = Join-Path $traceDir "pcsx2-pine-sample.ini"
    $sample = @"
; DetPS2 PINE sample — merge into your PCSX2.ini (Documents\PCSX2\inis\)
; Generated by tools/pine-helper.ps1
; Do not commit operator-local paths.

EnablePINE=true
PINESlot=$PineSlot

; Boot (one instance):
;   pcsx2-qt -batch -- "C:\path\to\game.iso"
;
; PINE TCP: 127.0.0.1:$PineSlot
; Stock opcodes: MsgRead8/16/32/64, MsgWrite*, MsgVersion, MsgStatus, ...
; See docs/DEVELOPER_GUIDE.md § PINE and docs/AGENT_SOP.md
"@
    Set-Content -LiteralPath $samplePath -Value $sample -Encoding utf8
    Write-Host "=== WriteConfigSample ==="
    Write-Host "  Wrote: $samplePath"

    if ($ForceUserConfig) {
        if (-not $ini) {
            $defaultIniDir = Join-Path $env:USERPROFILE "Documents\PCSX2\inis"
            New-Item -ItemType Directory -Force -Path $defaultIniDir | Out-Null
            $ini = Join-Path $defaultIniDir "PCSX2.ini"
            if (-not (Test-Path -LiteralPath $ini)) {
                Set-Content -LiteralPath $ini -Value "; PCSX2.ini (created by pine-helper)`nEnablePINE=true`nPINESlot=$PineSlot`n" -Encoding utf8
                Write-Host "  Created live ini: $ini"
            }
        }
        $raw = Get-Content -LiteralPath $ini -Raw
        if ($raw -match '(?im)^\s*EnablePINE\s*=') {
            $raw = [regex]::Replace($raw, '(?im)^\s*EnablePINE\s*=.*$', 'EnablePINE=true')
        } else {
            $raw = $raw.TrimEnd() + "`r`nEnablePINE=true`r`n"
        }
        if ($raw -match '(?im)^\s*PINESlot\s*=') {
            $raw = [regex]::Replace($raw, '(?im)^\s*PINESlot\s*=.*$', "PINESlot=$PineSlot")
        } else {
            $raw = $raw.TrimEnd() + "`r`nPINESlot=$PineSlot`r`n"
        }
        Set-Content -LiteralPath $ini -Value $raw -Encoding utf8
        Write-Host "  Updated live user config: $ini"
    } else {
        Write-Host "  (Live user profile not modified. Pass -ForceUserConfig to edit Documents\PCSX2.)"
    }
}

if ($Batch) {
    if (-not $Iso) { throw "-Batch requires -Iso <path>" }
    if (-not (Test-Path -LiteralPath $Iso)) { throw "ISO not found: $Iso" }
    if (-not $found) { throw "PCSX2 binary not found; set PCSX2_PATH or -Pcsx2Path" }

    $running = Get-Process -Name "pcsx2*","pcsx2-qt*","pcsx2-qtx64*" -ErrorAction SilentlyContinue
    if ($running) {
        throw "PCSX2 already running (PIDs: $($running.Id -join ',')). One instance only — close it first."
    }

    Write-Host "=== Batch launch ==="
    Write-Host "  exe: $($found.Exe)"
    Write-Host "  iso: $Iso"
    Write-Host "  cmd: `"$($found.Exe)`" -batch -- `"$Iso`""
    Write-Host "  PINE: 127.0.0.1:$PineSlot (after emu boots)"
    Write-Host "  Tip: pin dGPU if no iGPU → pwsh ./tools/pin-gpu.ps1 -ExePath `"$($found.Exe)`""

    Start-Process -FilePath $found.Exe -ArgumentList @("-batch", "--", $Iso) -WorkingDirectory $found.Root
    Write-Host "  Started. Compare DetPS2 at the same wall; do not guess."
}

Write-Host ""
Write-Host "Done."
