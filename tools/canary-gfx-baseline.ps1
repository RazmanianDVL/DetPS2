<#
.SYNOPSIS
  GFX-PLAN CP1 — Soft-GS present baseline canary (read-only ops; no Core changes).

.DESCRIPTION
  Runs scoreboard-metrics + --dump-softgs for a small fleet (default B3, Dec, Whip)
  at verify (50M) or other budget. Parses present PPM for lit / gray / color counts.
  Writes metrics JSON, PPM, and summary under out/canaries/gfx-baseline/<stamp>/.

  Does NOT claim MENU YES or Tier A. Visual checkpoint: open the PPM files.

.PARAMETER Budget
  diagnose (20M) | verify (50M) | claim (100M). Default: verify.

.PARAMETER Titles
  Fleet ids: burnout-3, mk-deception, whiplash (default all three).

.PARAMETER SkipBuild
  Reuse out/scoreboard-build DetPS2.Core.dll.

.EXAMPLE
  pwsh ./tools/canary-gfx-baseline.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [ValidateSet("diagnose", "verify", "claim")]
    [string]$Budget = "verify",
    [string[]]$Titles = @("burnout-3", "mk-deception", "whiplash"),
    [string]$BuildOut = "out/scoreboard-build",
    [switch]$SkipBuild,
    [string]$OutRoot = "out/canaries/gfx-baseline"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $repoRoot "src/DetPS2.Core/DetPS2.Core.csproj"))) {
    $repoRoot = $PSScriptRoot
    if (-not (Test-Path (Join-Path $repoRoot "src/DetPS2.Core/DetPS2.Core.csproj"))) {
        throw "Run from detps2 repo (tools/ sibling of src/)."
    }
}
Set-Location $repoRoot

$cycles = switch ($Budget) {
    "diagnose" { 20000000 }
    "verify"   { 50000000 }
    "claim"    { 100000000 }
}

$mediaMap = @{
    "burnout-3"    = "burnout-only.json"
    "mk-deception" = "user-media-deception.json"
    "whiplash"     = "user-media-whiplash.json"
    "blood-omen-2" = "user-media-bloodomen2.json"
    "god-of-war"   = "user-media-god-of-war.json"
}

$dll = Join-Path $BuildOut "DetPS2.Core.dll"
if (-not $SkipBuild -or -not (Test-Path $dll)) {
    Write-Host "Building Release Core → $BuildOut"
    dotnet build src/DetPS2.Core/DetPS2.Core.csproj -c Release -o $BuildOut --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
if (-not (Test-Path $dll)) { throw "missing $dll" }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $OutRoot $stamp
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

function Analyze-Ppm([string]$path) {
    $result = [ordered]@{
        path = $path
        exists = $false
        pixels = 0
        lit = 0
        gray = 0
        color = 0
        black = 0
        litPct = 0.0
        colorPct = 0.0
        sampleNonBlack = @()
    }
    if (-not (Test-Path $path)) { return $result }
    $result.exists = $true
    $sr = [System.IO.StreamReader]::new((Resolve-Path $path))
    try {
        $magic = $sr.ReadLine()
        $dim = $sr.ReadLine()
        $null = $sr.ReadLine() # maxval
        if ($magic -ne "P3") { throw "expected P3 PPM, got $magic" }
        $parts = $dim.Trim() -split '\s+'
        $w = [int]$parts[0]; $h = [int]$parts[1]
        $n = $w * $h
        $lit = 0; $gray = 0; $color = 0; $black = 0
        $samples = [System.Collections.Generic.List[string]]::new()
        for ($i = 0; $i -lt $n; $i++) {
            $line = $sr.ReadLine()
            while ($null -ne $line -and $line.Trim() -eq "") { $line = $sr.ReadLine() }
            if ($null -eq $line) { break }
            $rgb = $line.Trim() -split '\s+'
            $r = [int]$rgb[0]; $g = [int]$rgb[1]; $b = [int]$rgb[2]
            if ($r -eq 0 -and $g -eq 0 -and $b -eq 0) { $black++ }
            else {
                $lit++
                if ($r -eq $g -and $g -eq $b) { $gray++ }
                else {
                    $color++
                    if ($samples.Count -lt 8) { $samples.Add("$r,$g,$b@$i") }
                }
            }
        }
        $result.pixels = $n
        $result.lit = $lit
        $result.gray = $gray
        $result.color = $color
        $result.black = $black
        $result.litPct = if ($n -gt 0) { [math]::Round(100.0 * $lit / $n, 3) } else { 0 }
        $result.colorPct = if ($n -gt 0) { [math]::Round(100.0 * $color / $n, 3) } else { 0 }
        $result.sampleNonBlack = @($samples)
    }
    finally { $sr.Close() }
    return $result
}

$tip = (git rev-parse --short HEAD 2>$null)
if (-not $tip) { $tip = "unknown" }

$rows = @()
foreach ($id in $Titles) {
    if (-not $mediaMap.ContainsKey($id)) {
        Write-Warning "unknown title id $id — skip"
        continue
    }
    $media = $mediaMap[$id]
    if (-not (Test-Path $media)) {
        Write-Warning "missing media $media — skip $id"
        continue
    }
    $metricsPath = Join-Path $outDir "$id-metrics.json"
    $ppmPath = Join-Path $outDir "$id-present.ppm"
    $errPath = Join-Path $outDir "$id-err.txt"
    $outPath = Join-Path $outDir "$id-out.txt"
    Write-Host "=== $id cycles=$cycles media=$media ==="
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $args = @(
        "exec", $dll, "scoreboard-metrics", $media,
        "--cycles=$cycles", "--host-present",
        "--out=$metricsPath", "--dump-softgs=$ppmPath"
    )
    & dotnet @args 2>$errPath | Tee-Object -FilePath $outPath | Out-Null
    $sw.Stop()
    $exit = $LASTEXITCODE
    $m = $null
    if (Test-Path $metricsPath) {
        $m = Get-Content $metricsPath -Raw | ConvertFrom-Json
        if ($m -is [array]) { $m = $m[0] }
    }
    $ppm = Analyze-Ppm $ppmPath
    $row = [ordered]@{
        id = $id
        exitCode = $exit
        wallSec = [math]::Round($sw.Elapsed.TotalSeconds, 2)
        tip = $tip
        cycles = $cycles
        pc = if ($m) { $m.pc } else { $null }
        px = if ($m) { $m.px } else { $null }
        prims = if ($m) { $m.prims } else { $null }
        imgBytes = if ($m) { $m.imgBytes } else { $null }
        gifP2 = if ($m) { $m.gifP2 } else { $null }
        gifP3 = if ($m) { $m.gifP3 } else { $null }
        residualDispfbPx = if ($m) { $m.residualDispfbPx } else { $null }
        naturalDispfbPx = if ($m) { $m.naturalDispfbPx } else { $null }
        compositeSource = if ($m) { $m.compositeSource } else { $null }
        expandHits = if ($m) { $m.expandHits } else { $null }
        frame1 = if ($m) { $m.frame1 } else { $null }
        dispfb2 = if ($m) { $m.dispfb2 } else { $null }
        presentLit = $ppm.lit
        presentGray = $ppm.gray
        presentColor = $ppm.color
        presentBlack = $ppm.black
        presentLitPct = $ppm.litPct
        presentColorPct = $ppm.colorPct
        ppm = $ppmPath
        tierA_color5pct = ($ppm.colorPct -ge 5.0)
        metrics = $metricsPath
    }
    $rows += [pscustomobject]$row
    Write-Host ("  px={0} img={1} src={2} present lit%={3} color%={4} tierA?={5}" -f `
        $row.px, $row.imgBytes, $row.compositeSource, $row.presentLitPct, $row.presentColorPct, $row.tierA_color5pct)
}

$summary = [ordered]@{
    protocol = "gfx-baseline-v1"
    plan = "GFX-PLAN-v0"
    checkpoint = "CP1"
    tip = $tip
    budget = $Budget
    cycles = $cycles
    stamp = $stamp
    outDir = $outDir
    note = "Present lit/color from PPM dump after scoreboard-metrics composite. Visual inspect PPM before claiming Tier A."
    titles = $rows
}
$summaryPath = Join-Path $outDir "summary.json"
($summary | ConvertTo-Json -Depth 6) | Set-Content -Path $summaryPath -Encoding utf8

$md = @()
$md += "# GFX baseline canary — $stamp"
$md += ""
$md += "- Tip: ``$tip``"
$md += "- Budget: $Budget ($cycles cycles)"
$md += "- Plan: GFX-PLAN-v0 CP1 (read-only)"
$md += "- Artifacts: ``$outDir``"
$md += ""
$md += "| Title | px | imgBytes | composite | lit% | color% | TierA color>5% | PPM |"
$md += "|-------|----:|---------:|-----------|-----:|-------:|:--------------:|-----|"
foreach ($r in $rows) {
    $md += ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | ``{7}`` |" -f `
        $r.id, $r.px, $r.imgBytes, $r.compositeSource, $r.presentLitPct, $r.presentColorPct, $r.tierA_color5pct, (Split-Path $r.ppm -Leaf))
}
$md += ""
$md += "Open each ``*-present.ppm`` for visual checkpoint (Claude/Grok dual visual)."
$mdPath = Join-Path $outDir "summary.md"
$md -join "`n" | Set-Content -Path $mdPath -Encoding utf8

Write-Host ""
Write-Host "Wrote $summaryPath"
Write-Host "Wrote $mdPath"
Write-Host "Open PPMs under $outDir for visual check."
