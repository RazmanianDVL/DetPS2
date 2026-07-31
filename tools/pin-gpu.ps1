<#
.SYNOPSIS
  List GPUs and optionally pin an executable to the high-performance (dGPU) adapter.

.DESCRIPTION
  This operator machine has no iGPU. PCSX2 / DetPS2 Desktop present may need an
  explicit Windows "High performance" GPU preference for the exe.

  Practical approach (Windows 10 1803+ / Windows 11):
    HKCU\Software\Microsoft\DirectX\UserGpuPreferences
      <full-exe-path> = "GpuPreference=2;"   # 1=Power saving, 2=High performance

  Falls back to printed Settings UI instructions if registry write fails.

.PARAMETER ExePath
  Full path to the .exe to pin (e.g. pcsx2-qt.exe). When set without -Remove,
  writes GpuPreference=2 (high performance).

.PARAMETER ListAdapters
  List Win32_VideoController adapters (name, PNPDeviceID, DriverVersion, Status).

.PARAMETER Preference
  high | power | default  (default: high when -ExePath set)

.PARAMETER Remove
  Remove the UserGpuPreferences entry for -ExePath.

.PARAMETER ShowGuidance
  Always print manual Settings steps (default on when no other action).

.EXAMPLE
  pwsh ./tools/pin-gpu.ps1 -ListAdapters
  pwsh ./tools/pin-gpu.ps1 -ExePath "C:\pcsx2\pcsx2-qt.exe"
  pwsh ./tools/pin-gpu.ps1 -ExePath "C:\pcsx2\pcsx2-qt.exe" -Preference high
#>
[CmdletBinding()]
param(
    [string]$ExePath = "",
    [switch]$ListAdapters,
    [ValidateSet("high", "power", "default")]
    [string]$Preference = "high",
    [switch]$Remove,
    [switch]$ShowGuidance
)

$ErrorActionPreference = "Stop"

$regPath = "HKCU:\Software\Microsoft\DirectX\UserGpuPreferences"
# GpuPreference: 0/absent=default auto, 1=power saving, 2=high performance
$prefMap = @{
    high    = "GpuPreference=2;"
    power   = "GpuPreference=1;"
    default = "GpuPreference=0;"
}

function Show-ManualGuidance {
    Write-Host ""
    Write-Host "=== Manual GPU pin (Windows Settings) ==="
    Write-Host "1. Settings → System → Display → Graphics settings"
    Write-Host "   (or: Settings → System → Display → Related → Graphics)"
    Write-Host "2. Browse → select the .exe (pcsx2-qt.exe / DetPS2.Desktop.exe)"
    Write-Host "3. Options → High performance → Save"
    Write-Host ""
    Write-Host "Registry equivalent (this script):"
    Write-Host "  $regPath"
    Write-Host "  <ExePath> = GpuPreference=2;   # high performance"
    Write-Host ""
    Write-Host "Notes:"
    Write-Host "  - Soft-GS DetPS2 metrics do NOT need a GPU window."
    Write-Host "  - Pin dGPU only for PCSX2 UI / Desktop present when no iGPU."
    Write-Host "  - DXGI_ADAPTER_PREFERENCE / similar env vars are not portable; prefer UserGpuPreferences."
    Write-Host "  - After changing preference, fully quit and relaunch the app."
    Write-Host ""
}

function Get-Adapters {
    Write-Host "=== Video adapters (Win32_VideoController) ==="
    try {
        $ctrls = Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop
    } catch {
        Write-Warning "Get-CimInstance failed: $_"
        Write-Host "Fallback: wmic path win32_videocontroller get Name,PNPDeviceID,DriverVersion,Status"
        & wmic path win32_videocontroller get Name,PNPDeviceID,DriverVersion,Status 2>$null
        return
    }
    if (-not $ctrls) {
        Write-Warning "No video controllers reported."
        return
    }
    $i = 0
    foreach ($c in $ctrls) {
        $i++
        $vram = if ($c.AdapterRAM -and $c.AdapterRAM -gt 0) {
            "{0:N0} MB" -f ($c.AdapterRAM / 1MB)
        } else { "n/a" }
        Write-Host ("[{0}] {1}" -f $i, $c.Name)
        Write-Host ("     Status={0}  Driver={1}  RAM≈{2}" -f $c.Status, $c.DriverVersion, $vram)
        Write-Host ("     PNP={0}" -f $c.PNPDeviceID)
        # Heuristic: Microsoft Basic / Remote = not a real dGPU
        if ($c.Name -match 'Microsoft Basic|Remote Desktop|Virtual') {
            Write-Host "     (likely software/remote — not a discrete GPU pin target)"
        } elseif ($c.Name -match 'NVIDIA|AMD|Radeon|GeForce|Intel Arc') {
            Write-Host "     (candidate high-performance adapter)"
        }
    }
    Write-Host ""
    $real = @($ctrls | Where-Object { $_.Name -notmatch 'Microsoft Basic|Remote Desktop' })
    if ($real.Count -eq 0) {
        Write-Warning "No discrete/physical GPU detected. Soft-GS headless remains the success path."
    } elseif ($real.Count -eq 1) {
        Write-Host "Single physical adapter: Windows may auto-use it; pin still helps some DX/OpenGL hosts."
    } else {
        Write-Host "Multiple adapters: pin pcsx2-qt / Desktop to the dGPU via -ExePath."
    }
}

function Set-ExeGpuPreference {
    param([string]$Path, [string]$PrefKey, [switch]$DoRemove)

    if (-not $Path) { throw "-ExePath is required" }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $DoRemove -and -not (Test-Path -LiteralPath $full)) {
        Write-Warning "Exe not found at $full — registry value will still be written (for future install)."
    }

    if (-not (Test-Path $regPath)) {
        New-Item -Path $regPath -Force | Out-Null
    }

    if ($DoRemove) {
        Remove-ItemProperty -Path $regPath -Name $full -ErrorAction SilentlyContinue
        # Also try original path form if different
        if ($Path -ne $full) {
            Remove-ItemProperty -Path $regPath -Name $Path -ErrorAction SilentlyContinue
        }
        Write-Host "Removed UserGpuPreferences entry for: $full"
        return
    }

    $value = $prefMap[$PrefKey]
    if (-not $value) { throw "Unknown preference: $PrefKey" }

    New-ItemProperty -Path $regPath -Name $full -Value $value -PropertyType String -Force | Out-Null
    Write-Host "=== GPU preference set ==="
    Write-Host "  Exe : $full"
    Write-Host "  Pref: $PrefKey → $value"
    Write-Host "  Key : $regPath"
    Write-Host "  Relaunch the application for it to take effect."
}

# Defaults
if (-not $ListAdapters -and -not $ExePath) {
    $ListAdapters = $true
    $ShowGuidance = $true
}

Write-Host "=== pin-gpu ==="

if ($ListAdapters) {
    Get-Adapters
}

if ($ExePath) {
    try {
        Set-ExeGpuPreference -Path $ExePath -PrefKey $Preference -DoRemove:$Remove
    } catch {
        Write-Warning "Registry set failed: $_"
        Write-Host "Use manual Settings path below."
        $ShowGuidance = $true
    }
}

if ($ShowGuidance -or (-not $ExePath -and -not $ListAdapters)) {
    Show-ManualGuidance
}

# Show current preferences if any
if (Test-Path $regPath) {
    $props = Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue
    if ($props) {
        $names = $props.PSObject.Properties |
            Where-Object { $_.Name -notmatch '^PS' -and $_.Value -is [string] }
        if ($names) {
            Write-Host "=== Current UserGpuPreferences ==="
            foreach ($n in $names) {
                Write-Host ("  {0} = {1}" -f $n.Name, $n.Value)
            }
        }
    }
}

Write-Host "Done."
