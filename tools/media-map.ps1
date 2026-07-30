<#
.SYNOPSIS
  Inventory user-media*.json and burnout-only.json at the repo root.

.DESCRIPTION
  Scans media configs and prints id, title, path, whether the ISO/ELF exists,
  and whether biosPath exists. Optional markdown report under out/traces/.

  Exit 0 always unless -Strict (then non-zero if any ISO or BIOS is missing).

.PARAMETER WriteReport
  Write markdown to this path (default when switch-only: out/traces/media-map.md).
  Pass a path string, or use -WriteReport without a value for the default.

.PARAMETER Strict
  Exit 1 if any title path or any biosPath is missing.

.EXAMPLE
  pwsh ./tools/media-map.ps1
  pwsh ./tools/media-map.ps1 -WriteReport
  pwsh ./tools/media-map.ps1 -WriteReport out/traces/media-map.md -Strict
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [AllowEmptyString()]
    [string]$WriteReport = $null,
    [switch]$Strict
)

$ErrorActionPreference = "Continue"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

# -WriteReport with no arg: PowerShell binds $true as string for [string] sometimes;
# treat empty / "True" as default path when switch-style use is intended.
$reportPath = $null
if ($PSBoundParameters.ContainsKey("WriteReport")) {
    if ([string]::IsNullOrWhiteSpace($WriteReport) -or $WriteReport -eq "True" -or $WriteReport -eq "true") {
        $reportPath = Join-Path $repoRoot "out/traces/media-map.md"
    } else {
        $reportPath = if ([IO.Path]::IsPathRooted($WriteReport)) {
            $WriteReport
        } else {
            Join-Path $repoRoot $WriteReport
        }
    }
}

$configs = @()
$configs += Get-ChildItem -Path $repoRoot -File -Filter "user-media*.json" -ErrorAction SilentlyContinue
$burnout = Join-Path $repoRoot "burnout-only.json"
if (Test-Path -LiteralPath $burnout) {
    $configs += Get-Item -LiteralPath $burnout
}
$configs = $configs | Sort-Object FullName -Unique

$rows = @()
$missing = 0

foreach ($cfgFile in $configs) {
    $rel = $cfgFile.Name
    try {
        $cfg = Get-Content -LiteralPath $cfgFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    } catch {
        Write-Warning "Bad JSON: $rel — $($_.Exception.Message)"
        $rows += [pscustomobject]@{
            config   = $rel
            id       = "?"
            title    = "?"
            path     = ""
            isoOk    = $false
            biosPath = ""
            biosOk   = $false
            status   = "BAD-JSON"
        }
        $missing++
        continue
    }

    $biosPath = [string]$cfg.biosPath
    $biosOk = -not [string]::IsNullOrWhiteSpace($biosPath) -and (Test-Path -LiteralPath $biosPath)
    if (-not $biosOk) { $missing++ }

    $titles = @($cfg.titles)
    if ($titles.Count -eq 0) {
        $rows += [pscustomobject]@{
            config   = $rel
            id       = ""
            title    = "(no titles)"
            path     = ""
            isoOk    = $false
            biosPath = $biosPath
            biosOk   = $biosOk
            status   = "EMPTY"
        }
        continue
    }

    foreach ($t in $titles) {
        $id = [string]$t.id
        $title = [string]$t.title
        $path = [string]$t.path
        $isoOk = -not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path)
        if (-not $isoOk) { $missing++ }
        $status = if ($isoOk -and $biosOk) { "OK" } elseif (-not $isoOk -and -not $biosOk) { "NO-ISO+BIOS" } elseif (-not $isoOk) { "NO-ISO" } else { "NO-BIOS" }
        $rows += [pscustomobject]@{
            config   = $rel
            id       = $id
            title    = $title
            path     = $path
            isoOk    = $isoOk
            biosPath = $biosPath
            biosOk   = $biosOk
            status   = $status
        }
    }
}

Write-Host "=== DetPS2 media map ($($configs.Count) config file(s), $($rows.Count) title row(s)) ==="
Write-Host ("{0,-28} {1,-22} {2,-28} {3,-6} {4,-6} {5}" -f "config", "id", "title", "iso?", "bios?", "status")
Write-Host ("-" * 110)
foreach ($r in $rows) {
    $isoMark = if ($r.isoOk) { "YES" } else { "NO" }
    $biosMark = if ($r.biosOk) { "YES" } else { "NO" }
    $titleShort = if ($r.title.Length -gt 26) { $r.title.Substring(0, 23) + "..." } else { $r.title }
    Write-Host ("{0,-28} {1,-22} {2,-28} {3,-6} {4,-6} {5}" -f $r.config, $r.id, $titleShort, $isoMark, $biosMark, $r.status)
}

Write-Host ""
Write-Host "Paths (ISO/ELF):"
foreach ($r in $rows) {
    if ($r.path) {
        $mark = if ($r.isoOk) { "OK  " } else { "MISS" }
        Write-Host ("  [{0}] {1}  ({2} / {3})" -f $mark, $r.path, $r.id, $r.config)
    }
}
Write-Host "BIOS paths:"
$rows | Group-Object biosPath | ForEach-Object {
    $bp = $_.Name
    if ([string]::IsNullOrWhiteSpace($bp)) {
        Write-Host "  [MISS] (empty biosPath) — configs: $($_.Group.config -join ', ')"
    } else {
        $ok = Test-Path -LiteralPath $bp
        $mark = if ($ok) { "OK  " } else { "MISS" }
        Write-Host ("  [{0}] {1}" -f $mark, $bp)
    }
}

if ($reportPath) {
    $dir = Split-Path -Parent $reportPath
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $md = @()
    $md += "# DetPS2 media map"
    $md += ""
    $md += "- **Generated:** $stamp"
    $md += "- **Repo:** ``$repoRoot``"
    $md += "- **Configs:** $($configs.Count) / **title rows:** $($rows.Count)"
    $md += ""
    $md += "| Config | Id | Title | ISO exists | BIOS exists | Status | Path |"
    $md += "|--------|----|-------|------------|-------------|--------|------|"
    foreach ($r in $rows) {
        $isoMark = if ($r.isoOk) { "YES" } else { "NO" }
        $biosMark = if ($r.biosOk) { "YES" } else { "NO" }
        $p = ($r.path -replace '\|', '\|')
        $md += "| $($r.config) | $($r.id) | $($r.title) | $isoMark | $biosMark | $($r.status) | ``$p`` |"
    }
    $md += ""
    $md += "Agent SOP: ``docs/AGENT_SOP.md`` · tooling: ``docs/TOOLING.md``"
    $md += ""
    ($md -join "`n") | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host ""
    Write-Host "Report written: $reportPath"
}

if ($Strict -and $missing -gt 0) {
    Write-Host ""
    Write-Host "STRICT: $missing missing ISO/BIOS reference(s)."
    exit 1
}

exit 0
