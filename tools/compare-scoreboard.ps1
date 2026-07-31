<#
.SYNOPSIS
  Compare two DetPS2 scoreboard JSON files and print a markdown delta table.

.DESCRIPTION
  Accepts JSON produced by tools/scoreboard.ps1 (array or single object rows).
  Also accepts per-title run-title JSON by synthesizing an id from media name.

  Flags regressions:
    - px significantly down
    - cdvd significantly down
    - exitRequested appeared (False→True) / new exit
    - status became SKIP-* when baseline was RAN

.PARAMETER Baseline
  Older / reference scoreboard JSON path.

.PARAMETER Current
  Newer scoreboard JSON path.

.PARAMETER Out
  Optional markdown output path (default: stdout only; if set, also write file).

.PARAMETER PxDropPct
  Percent drop in px that counts as regression (default 25).

.PARAMETER CdvdDropPct
  Percent drop in cdvd that counts as regression (default 20).

.PARAMETER FailOnRegression
  Exit code 2 if any regression flag is set.

.EXAMPLE
  pwsh ./tools/compare-scoreboard.ps1 -Baseline out/traces/scoreboard-old.json -Current out/traces/scoreboard-new.json
  pwsh ./tools/compare-scoreboard.ps1 -Baseline a.json -Current b.json -Out out/traces/delta.md -FailOnRegression
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Baseline,
    [Parameter(Mandatory = $true)]
    [string]$Current,
    [string]$Out = "",
    [double]$PxDropPct = 25,
    [double]$CdvdDropPct = 20,
    [switch]$FailOnRegression
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

function Resolve-RepoPath([string]$p) {
    if ([IO.Path]::IsPathRooted($p)) { return $p }
    return (Join-Path $repoRoot $p)
}

function Read-ScoreboardRows([string]$path) {
    $full = Resolve-RepoPath $path
    if (-not (Test-Path -LiteralPath $full)) { throw "JSON not found: $full" }
    $raw = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json
    $rows = @()
    if ($null -eq $raw) { return $rows }
    # Single object vs array
    if ($raw -is [System.Array]) {
        $rows = @($raw)
    } elseif ($raw.PSObject.Properties.Name -contains "value" -and $raw.value) {
        # PowerShell ConvertFrom-Json single-element array quirk (rare)
        $rows = @($raw.value)
    } else {
        $rows = @($raw)
    }
    return $rows
}

function Get-RowKey($row) {
    if ($row.id) { return [string]$row.id }
    if ($row.serial) { return [string]$row.serial }
    if ($row.name) { return [string]$row.name }
    if ($row.media) { return [IO.Path]::GetFileNameWithoutExtension([string]$row.media) }
    return "unknown"
}

function To-Num($v) {
    if ($null -eq $v -or $v -eq "") { return $null }
    $s = [string]$v
    $n = 0.0
    if ([double]::TryParse($s, [ref]$n)) { return $n }
    return $null
}

function Fmt-Delta($old, $new) {
    if ($null -eq $old -and $null -eq $new) { return "—" }
    if ($null -eq $old) { return "n/a → $new" }
    if ($null -eq $new) { return "$old → n/a" }
    $d = $new - $old
    $sign = if ($d -gt 0) { "+" } elseif ($d -lt 0) { "" } else { "" }
    if ($d -eq 0) { return "$new (=)" }
    return ("{0} → {1} ({2}{3})" -f $old, $new, $sign, $d)
}

function Drop-Pct($old, $new) {
    if ($null -eq $old -or $old -eq 0) { return $null }
    if ($null -eq $new) { return 100.0 }
    return (($old - $new) / [math]::Abs($old)) * 100.0
}

$baseRows = Read-ScoreboardRows $Baseline
$curRows  = Read-ScoreboardRows $Current

$baseMap = @{}
foreach ($r in $baseRows) { $baseMap[(Get-RowKey $r)] = $r }
$curMap = @{}
foreach ($r in $curRows) { $curMap[(Get-RowKey $r)] = $r }

$keys = @($baseMap.Keys + $curMap.Keys) | Select-Object -Unique | Sort-Object

$md = @()
$md += "# Scoreboard delta"
$md += ""
$md += "- **Baseline:** ``$Baseline``"
$md += "- **Current:**  ``$Current``"
$md += "- **Generated:** $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$md += "- **Regression thresholds:** px drop ≥ ${PxDropPct}%; cdvd drop ≥ ${CdvdDropPct}%; exit appeared"
$md += ""
$md += "| Title | status | PC changed? | px | gifP3 | dmac | cdvd | binds/calls | flags |"
$md += "|-------|--------|-------------|----|-------|------|------|-------------|-------|"

$regressions = @()
$improvements = @()

foreach ($k in $keys) {
    $b = $baseMap[$k]
    $c = $curMap[$k]
    $name = if ($c -and $c.name) { $c.name } elseif ($b -and $b.name) { $b.name } else { $k }

    $bStatus = if ($b) { $b.status } else { "—" }
    $cStatus = if ($c) { $c.status } else { "—" }
    $statusCell = if ($bStatus -eq $cStatus) { "$cStatus" } else { "$bStatus → $cStatus" }

    $bPc = if ($b) { [string]$b.pc } else { "" }
    $cPc = if ($c) { [string]$c.pc } else { "" }
    $pcChanged = if ($bPc -and $cPc -and ($bPc -ne $cPc)) { "YES ($bPc → $cPc)" } elseif (-not $bPc -or -not $cPc) { "n/a" } else { "no" }

    $bPx = To-Num $(if ($b) { $b.px } else { $null })
    $cPx = To-Num $(if ($c) { $c.px } else { $null })
    # gifPath3 or gifP3
    $bGif = To-Num $(if ($b) { if ($null -ne $b.gifPath3) { $b.gifPath3 } else { $b.gifP3 } } else { $null })
    $cGif = To-Num $(if ($c) { if ($null -ne $c.gifPath3) { $c.gifPath3 } else { $c.gifP3 } } else { $null })
    $bDmac = To-Num $(if ($b) { $b.dmac } else { $null })
    $cDmac = To-Num $(if ($c) { $c.dmac } else { $null })
    $bCdvd = To-Num $(if ($b) { $b.cdvd } else { $null })
    $cCdvd = To-Num $(if ($c) { $c.cdvd } else { $null })
    $bBinds = if ($b) { $b.binds } else { "" }
    $cBinds = if ($c) { $c.binds } else { "" }
    $bCalls = if ($b) { $b.calls } else { "" }
    $cCalls = if ($c) { $c.calls } else { "" }
    $bcCell = if ("$bBinds$bCalls" -or "$cBinds$cCalls") {
        $left = if ("$bBinds" -or "$bCalls") { "$bBinds/$bCalls" } else { "—" }
        $right = if ("$cBinds" -or "$cCalls") { "$cBinds/$cCalls" } else { "—" }
        if ($left -eq $right) { $right } else { "$left → $right" }
    } else { "—" }

    $flags = @()

    # exit appeared
    $bExit = if ($b) { [string]$b.exitReq } else { "" }
    $cExit = if ($c) { [string]$c.exitReq } else { "" }
    if ($cExit -match 'True' -and $bExit -notmatch 'True') {
        $flags += "REGRESS:exit-appeared"
        $regressions += "$k exit appeared"
    }

    # status skip regression
    if ($cStatus -match 'SKIP' -and $bStatus -eq 'RAN') {
        $flags += "REGRESS:became-skip"
        $regressions += "$k became $cStatus"
    }

    $pxDrop = Drop-Pct $bPx $cPx
    if ($null -ne $pxDrop -and $pxDrop -ge $PxDropPct -and $bPx -gt 0) {
        $flags += ("REGRESS:px-down-{0:N0}%" -f $pxDrop)
        $regressions += ("{0} px {1} → {2}" -f $k, $bPx, $cPx)
    } elseif ($null -ne $bPx -and $null -ne $cPx -and $cPx -gt $bPx -and $bPx -ge 0) {
        $gain = if ($bPx -eq 0) { 100.0 } else { (($cPx - $bPx) / [math]::Max($bPx, 1)) * 100.0 }
        if ($gain -ge 10 -or ($cPx - $bPx) -gt 1000) {
            $flags += "IMPROVE:px"
            $improvements += "$k px up"
        }
    }

    $cdvdDrop = Drop-Pct $bCdvd $cCdvd
    if ($null -ne $cdvdDrop -and $cdvdDrop -ge $CdvdDropPct -and $bCdvd -gt 100) {
        $flags += ("REGRESS:cdvd-down-{0:N0}%" -f $cdvdDrop)
        $regressions += ("{0} cdvd {1} → {2}" -f $k, $bCdvd, $cCdvd)
    }

    # gif crash to zero when baseline had spine
    if ($null -ne $bGif -and $bGif -ge 5 -and $null -ne $cGif -and $cGif -lt 2) {
        $flags += "REGRESS:gifP3-collapse"
        $regressions += "$k gifP3 collapse $bGif → $cGif"
    }

    $flagCell = if ($flags.Count) { ($flags -join ", ") } else { "ok" }

    $md += ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |" -f `
        $name, $statusCell, $pcChanged, `
        (Fmt-Delta $bPx $cPx), `
        (Fmt-Delta $bGif $cGif), `
        (Fmt-Delta $bDmac $cDmac), `
        (Fmt-Delta $bCdvd $cCdvd), `
        $bcCell, $flagCell)
}

$md += ""
$md += "## Summary"
$md += ""
if ($regressions.Count -eq 0) {
    $md += "- **Regressions:** none"
} else {
    $md += "- **Regressions ($($regressions.Count)):**"
    foreach ($r in $regressions) { $md += "  - $r" }
}
if ($improvements.Count -gt 0) {
    $md += "- **Improvements:**"
    foreach ($i in $improvements) { $md += "  - $i" }
}
$md += ""
$md += "Policy: scoreboard heuristic is not MENU YES. See docs/AGENT_SOP.md."
$md += ""

$text = $md -join "`n"
Write-Host $text

if ($Out) {
    $outFull = Resolve-RepoPath $Out
    $dir = Split-Path $outFull -Parent
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Set-Content -LiteralPath $outFull -Value $text -Encoding utf8
    Write-Host "Wrote: $outFull"
}

# Return object for callers
$result = [pscustomobject]@{
    regressionCount = $regressions.Count
    regressions     = $regressions
    improvementCount = $improvements.Count
}
if ($FailOnRegression -and $regressions.Count -gt 0) {
    Write-Error "Regressions detected ($($regressions.Count))."
    exit 2
}
$result
