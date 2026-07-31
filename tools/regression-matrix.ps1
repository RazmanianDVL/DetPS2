<#
.SYNOPSIS
  Fixed four-title DetPS2 regression matrix (SM, B3, BO2, GoW).

.DESCRIPTION
  Always runs:
    mk-shaolin-monks, burnout-3, blood-omen-2, god-of-war
  via tools/scoreboard.ps1 at diagnose or verify budget.

  Writes out/traces/regression-YYYYMMDD-HHMMSS.md (+ uses scoreboard json).

  Exit non-zero when:
    - any title is SKIP-NO-ISO / SKIP-NO-MEDIA and -FailOnSkip
    - -BaselineJson provided and compare-scoreboard reports regressions (-FailOnRegression implied)

.PARAMETER Budget
  diagnose | verify  (default diagnose)

.PARAMETER BaselineJson
  Optional prior scoreboard JSON for delta / regression gate.

.PARAMETER FailOnSkip
  Exit 1 if any matrix title was skipped (missing media/ISO).

.PARAMETER FailOnRegression
  Exit 2 if baseline compare finds regressions (default true when -BaselineJson set).

.PARAMETER SkipBuild
  Pass through to scoreboard.ps1.

.PARAMETER NoHostPresent
  Pass through to scoreboard.ps1.

.EXAMPLE
  pwsh ./tools/regression-matrix.ps1 -Budget diagnose
  pwsh ./tools/regression-matrix.ps1 -Budget verify -BaselineJson out/traces/scoreboard-prev.json -FailOnSkip
#>
[CmdletBinding()]
param(
    [ValidateSet("diagnose", "verify")]
    [string]$Budget = "diagnose",
    [string]$BaselineJson = "",
    [switch]$FailOnSkip,
    [switch]$FailOnRegression,
    [switch]$SkipBuild,
    [switch]$NoHostPresent,
    [switch]$NativeMetrics,
    [string]$BuildOut = "out/scoreboard-build",
    [string]$TraceDir = "out/traces"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$matrixIds = @(
    "mk-shaolin-monks",
    "burnout-3",
    "blood-omen-2",
    "god-of-war"
)

# When baseline is supplied, default to failing on regression unless explicitly off
if ($BaselineJson -and -not $PSBoundParameters.ContainsKey("FailOnRegression")) {
    $FailOnRegression = $true
}

$scoreboard = Join-Path $PSScriptRoot "scoreboard.ps1"
$compare    = Join-Path $PSScriptRoot "compare-scoreboard.ps1"
if (-not (Test-Path $scoreboard)) { throw "Missing $scoreboard" }

New-Item -ItemType Directory -Force -Path $TraceDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

Write-Host "=== regression-matrix ==="
Write-Host "Titles: $($matrixIds -join ', ')"
Write-Host "Budget: $Budget"
Write-Host ""

# Snapshot scoreboard json files before run so we can pick the new one
$before = @()
if (Test-Path $TraceDir) {
    $before = @(Get-ChildItem -LiteralPath $TraceDir -Filter "scoreboard-*.json" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
}

$sbArgs = @{
    Budget   = $Budget
    Titles   = $matrixIds
    BuildOut = $BuildOut
    TraceDir = $TraceDir
}
if ($SkipBuild) { $sbArgs.SkipBuild = $true }
if ($NoHostPresent) { $sbArgs.NoHostPresent = $true }
if ($NativeMetrics) { $sbArgs.NativeMetrics = $true }

& $scoreboard @sbArgs
$sbExit = $LASTEXITCODE
if ($sbExit -ne 0 -and $null -ne $sbExit) {
    Write-Warning "scoreboard.ps1 exited with code $sbExit"
}

# Find newest scoreboard json not in $before
$after = @(Get-ChildItem -LiteralPath $TraceDir -Filter "scoreboard-*.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending)
$currentJson = $null
foreach ($f in $after) {
    if ($before -notcontains $f.FullName) {
        $currentJson = $f.FullName
        break
    }
}
if (-not $currentJson -and $after.Count -gt 0) {
    $currentJson = $after[0].FullName
}
if (-not $currentJson) {
    throw "No scoreboard JSON produced under $TraceDir"
}

# Load rows
$raw = Get-Content -LiteralPath $currentJson -Raw | ConvertFrom-Json
$rows = @()
if ($raw -is [System.Array]) { $rows = @($raw) } else { $rows = @($raw) }

$byId = @{}
foreach ($r in $rows) {
    if ($r.id) { $byId[[string]$r.id] = $r }
}

$skipRows = @()
$ranRows = @()
foreach ($id in $matrixIds) {
    if (-not $byId.ContainsKey($id)) {
        $skipRows += [pscustomobject]@{ id = $id; status = "MISSING-FROM-RESULT" }
        continue
    }
    $r = $byId[$id]
    if ([string]$r.status -match 'SKIP') {
        $skipRows += $r
    } else {
        $ranRows += $r
    }
}

# Compare optional baseline
$compareResult = $null
$compareMdRel = $null
if ($BaselineJson) {
    $basePath = if ([IO.Path]::IsPathRooted($BaselineJson)) { $BaselineJson } else { Join-Path $repoRoot $BaselineJson }
    if (-not (Test-Path -LiteralPath $basePath)) {
        throw "BaselineJson not found: $basePath"
    }
    $compareMdRel = Join-Path $TraceDir "regression-compare-$stamp.md"
    $cArgs = @{
        Baseline = $basePath
        Current  = $currentJson
        Out      = $compareMdRel
    }
    if ($FailOnRegression) { $cArgs.FailOnRegression = $true }
    try {
        $compareResult = & $compare @cArgs
        $compareExit = 0
    } catch {
        $compareExit = 2
        Write-Warning "compare-scoreboard reported failure: $_"
    }
} else {
    $compareExit = 0
}

# Write regression markdown summary
$regMdPath = Join-Path $TraceDir "regression-$stamp.md"
$md = @()
$md += "# DetPS2 regression matrix — $stamp"
$md += ""
$md += "- **Budget:** $Budget"
$md += "- **Titles:** $($matrixIds -join ', ')"
$md += "- **Scoreboard JSON:** ``$currentJson``"
$md += "- **HostPresent:** $(-not $NoHostPresent)"
if ($BaselineJson) {
    $md += "- **Baseline:** ``$BaselineJson``"
    if ($compareMdRel) { $md += "- **Delta:** ``$compareMdRel``" }
}
$md += ""
$md += "## Matrix results"
$md += ""
$md += "| Title | Status | Heuristic | PC | px | gifP3 | dmac | cdvd | binds/calls | sec |"
$md += "|-------|--------|-----------|----|----|-------|------|------|-------------|-----|"

foreach ($id in $matrixIds) {
    if (-not $byId.ContainsKey($id)) {
        $md += "| $id | **MISSING** | — | — | — | — | — | — | — | — |"
        continue
    }
    $r = $byId[$id]
    $bc = if ($r.binds -or $r.calls) { "$($r.binds)/$($r.calls)" } else { "" }
    $gif = if ($null -ne $r.gifPath3) { $r.gifPath3 } else { $r.gifP3 }
    $md += ("| {0} | {1} | **{2}** | {3} | {4} | {5} | {6} | {7} | {8} | {9} |" -f `
        $(if ($r.name) { $r.name } else { $id }),
        $r.status,
        $(if ($r.menuHeuristic) { $r.menuHeuristic } else { "N/A" }),
        $r.pc, $r.px, $gif, $r.dmac, $r.cdvd, $bc, $r.elapsedSec)
}

$md += ""
$md += "## Gates"
$md += ""
$md += "- RAN: $($ranRows.Count) / $($matrixIds.Count)"
$md += "- SKIP/MISSING: $($skipRows.Count)"
if ($skipRows.Count -gt 0) {
    foreach ($s in $skipRows) {
        $md += "  - $($s.id): $($s.status)"
    }
}
if ($BaselineJson) {
    $regCount = 0
    if ($compareResult -and $null -ne $compareResult.regressionCount) {
        $regCount = [int]$compareResult.regressionCount
    }
    $md += "- Baseline regressions: $regCount"
}
$md += ""
$md += "FailOnSkip=$FailOnSkip  FailOnRegression=$FailOnRegression"
$md += ""
$md += "Play! oracle: ``pwsh ./tools/play-lookup.ps1``  "
$md += "PINE: ``pwsh ./tools/pine-helper.ps1 -CheckConfig``  "
$md += "Agent SOP: ``docs/AGENT_SOP.md``"
$md += ""

$md -join "`n" | Set-Content -LiteralPath $regMdPath -Encoding utf8

Write-Host ""
Write-Host "=== REGRESSION MATRIX WRITTEN ==="
Write-Host $regMdPath
Write-Host $currentJson
if ($compareMdRel) { Write-Host $compareMdRel }
Write-Host ""
Get-Content $regMdPath

# Exit codes
$exit = 0
if ($FailOnSkip -and $skipRows.Count -gt 0) {
    Write-Warning "FailOnSkip: $($skipRows.Count) title(s) skipped/missing"
    $exit = 1
}
if ($FailOnRegression -and $compareExit -ne 0) {
    Write-Warning "FailOnRegression: compare reported regressions"
    if ($exit -eq 0) { $exit = 2 } else { $exit = 3 }
}

exit $exit
