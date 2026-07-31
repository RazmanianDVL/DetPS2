<#
.SYNOPSIS
  Move root-level investigation trace noise into out/traces/archive-YYYYMMDD/.

.DESCRIPTION
  Targets repo-root files matching investigation dump patterns (b3/bo2/gow/sm/dec/mk
  hyphen families, bare short dumps like b3t.txt / bo240.txt, *-err/*-out, etc.).

  Default action is MOVE (never delete unless -Delete).
  Only processes files that are gitignored noise (or match the patterns when git is unavailable);
  skips tracked files and files already under out/.

.PARAMETER Delete
  Permanently delete matching root files instead of moving them.

.PARAMETER DryRun
  Print actions without moving or deleting.

.PARAMETER DateStamp
  Archive folder suffix (default: today's yyyyMMdd).

.PARAMETER ReportSize
  After cleanup (or alone), print worktree size breakdown (root txt / out/ / total).

.EXAMPLE
  pwsh ./tools/clean-traces.ps1
  pwsh ./tools/clean-traces.ps1 -DryRun
  pwsh ./tools/clean-traces.ps1 -Delete
  pwsh ./tools/clean-traces.ps1 -ReportSize
#>
[CmdletBinding()]
param(
    [switch]$Delete,
    [switch]$DryRun,
    [switch]$ReportSize,
    [string]$DateStamp = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

if (-not $DateStamp) {
    $DateStamp = Get-Date -Format "yyyyMMdd"
}
$archiveDir = Join-Path $repoRoot "out/traces/archive-$DateStamp"

# Hyphen families + bare short dumps agents drop at repo root.
# Keep media JSON / source / docs out of this list.
# NOTE: Get-ChildItem -Filter uses Win32 globs (no [0-9] classes). Character-class
# patterns must use -like / regex below (see $likePatterns / $regexPatterns).
$filterPatterns = @(
    "b3-*.txt",
    "b3t.txt",
    "b3d*.txt",
    "bo2-*.txt",
    "bo2t.txt",
    "gow-*.txt",
    "gowt.txt",
    "sm-*.txt",
    "dec-*.txt",
    "mk-*.txt",
    "mkt*.txt",
    "v2-*.txt",
    "snap-*.txt",
    "smoke-*.txt",
    "build-*.txt",
    "*-err.txt",
    "*-out.txt",
    "*-final*.txt",
    "*-score*.txt",
    "*-menu*.txt",
    "*-rpc*.txt"
)
# PowerShell -like (supports [0-9])
$likePatterns = @(
    "bo2[0-9]*.txt",
    "gow[0-9]*.txt",
    "mk[0-9]*.txt",
    "d[0-9]*.txt"
)

function Test-HasGit {
    Push-Location $repoRoot
    try {
        $null = & git rev-parse --is-inside-work-tree 2>$null
        return ($LASTEXITCODE -eq 0)
    } finally {
        Pop-Location
    }
}

# Batch: set of ignored / tracked basenames (avoids per-file git spawn on large roots).
$script:HasGit = Test-HasGit
$script:IgnoredSet = @{}
$script:TrackedSet = @{}

function Initialize-GitSets {
    param([string[]]$Names)
    if (-not $script:HasGit -or $Names.Count -eq 0) { return }
    Push-Location $repoRoot
    try {
        # git check-ignore -z --stdin: NUL-delimited names in, ignored names out
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "git"
        $psi.Arguments = "check-ignore --stdin"
        $psi.WorkingDirectory = $repoRoot.Path
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $p = [Diagnostics.Process]::Start($psi)
        foreach ($n in $Names) { $p.StandardInput.WriteLine($n) }
        $p.StandardInput.Close()
        $out = $p.StandardOutput.ReadToEnd()
        $null = $p.StandardError.ReadToEnd()
        $p.WaitForExit()
        foreach ($line in ($out -split "`r?`n")) {
            $t = $line.Trim()
            if ($t) { $script:IgnoredSet[$t] = $true }
        }

        $tracked = & git ls-files -- "*.txt" 2>$null
        foreach ($t in $tracked) {
            $base = [IO.Path]::GetFileName($t)
            if ($base) { $script:TrackedSet[$base] = $true }
            $script:TrackedSet[$t] = $true
        }
    } finally {
        Pop-Location
    }
}

function Test-IsGitIgnored {
    param([string]$Path)
    if (-not $script:HasGit) { return $true }
    return $script:IgnoredSet.ContainsKey($Path)
}

function Test-IsTracked {
    param([string]$Path)
    if (-not $script:HasGit) { return $false }
    return $script:TrackedSet.ContainsKey($Path)
}

function Get-DirSizeBytes {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [long]0 }
    $sum = [long]0
    Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
        ForEach-Object { $sum += $_.Length }
    return $sum
}

function Format-Size {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return ("{0:N2} GB" -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ("{0:N2} MB" -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ("{0:N2} KB" -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function Write-SizeReport {
    Write-Host ""
    Write-Host "=== worktree size report ($($repoRoot.Path)) ==="
    $rootTxt = Get-ChildItem -Path $repoRoot -File -Filter "*.txt" -ErrorAction SilentlyContinue
    $rootTxtBytes = ($rootTxt | Measure-Object -Property Length -Sum).Sum
    if (-not $rootTxtBytes) { $rootTxtBytes = [long]0 }
    $rootTxtCount = @($rootTxt).Count

    $outBytes = Get-DirSizeBytes (Join-Path $repoRoot "out")
    $srcBytes = Get-DirSizeBytes (Join-Path $repoRoot "src")
    $docsBytes = Get-DirSizeBytes (Join-Path $repoRoot "docs")
    $toolsBytes = Get-DirSizeBytes (Join-Path $repoRoot "tools")
    $totalBytes = Get-DirSizeBytes $repoRoot.Path

    Write-Host ("  root *.txt     : {0} files, {1}" -f $rootTxtCount, (Format-Size $rootTxtBytes))
    Write-Host ("  out/           : {0}" -f (Format-Size $outBytes))
    Write-Host ("  src/           : {0}" -f (Format-Size $srcBytes))
    Write-Host ("  docs/          : {0}" -f (Format-Size $docsBytes))
    Write-Host ("  tools/         : {0}" -f (Format-Size $toolsBytes))
    Write-Host ("  worktree total : {0}" -f (Format-Size $totalBytes))
    if ($totalBytes -gt 0 -and $rootTxtBytes + $outBytes -gt ($totalBytes * 0.5)) {
        Write-Host "  WARN: traces/out dominate size — prefer clean-traces + delete archive when done."
    } else {
        Write-Host "  OK: sources/docs should dominate after archive; rebuild is not blocked by root dumps."
    }
}

$candidates = @()
foreach ($pat in $filterPatterns) {
    $candidates += Get-ChildItem -Path $repoRoot -File -Filter $pat -ErrorAction SilentlyContinue
}
$rootTxtAll = @(Get-ChildItem -Path $repoRoot -File -Filter "*.txt" -ErrorAction SilentlyContinue)
foreach ($pat in $likePatterns) {
    $candidates += $rootTxtAll | Where-Object { $_.Name -like $pat }
}
$candidates = @($candidates | Sort-Object FullName -Unique)
$candidateNames = @($candidates | ForEach-Object { $_.Name })
Initialize-GitSets -Names $candidateNames

$moved = 0
$deleted = 0
$skipped = 0
$verbose = $env:DETPS2_CLEAN_TRACES_VERBOSE -eq "1"

Write-Host "=== clean-traces (root -> archive-$DateStamp) Delete=$Delete DryRun=$DryRun candidates=$($candidates.Count) ==="

if (-not $Delete -and -not $DryRun -and $candidates.Count -gt 0) {
    New-Item -ItemType Directory -Force -Path $archiveDir | Out-Null
}

foreach ($f in $candidates) {
    $name = $f.Name
    $full = $f.FullName

    # Never operate outside repo root top-level
    if ($f.DirectoryName -ne $repoRoot.Path) {
        $skipped++
        continue
    }

    if (Test-IsTracked -Path $name) {
        if ($verbose) { Write-Host "SKIP tracked: $name" }
        $skipped++
        continue
    }

    # Eligible if gitignored, or (no git / not tracked and pattern matched).
    # Pattern match at root + not tracked is enough for agent dump hygiene.
    $ignored = Test-IsGitIgnored -Path $name
    if (-not $ignored -and $script:HasGit) {
        # Not in ignore set: still allow if not tracked (new pattern not yet in .gitignore).
        # Refuse only when tracked (already handled) — untracked non-ignored dumps are cleaned.
        if ($verbose) { Write-Host "NOTE untracked non-ignore pattern: $name" }
    }

    if ($Delete) {
        if ($DryRun) {
            if ($verbose) { Write-Host "WOULD DELETE $name" }
        } else {
            Remove-Item -LiteralPath $full -Force
            if ($verbose) { Write-Host "DELETED $name" }
            $deleted++
        }
        if ($DryRun) { $deleted++ }
        continue
    }

    $dest = Join-Path $archiveDir $name
    if (Test-Path -LiteralPath $dest) {
        $stem = [IO.Path]::GetFileNameWithoutExtension($name)
        $ext = [IO.Path]::GetExtension($name)
        $n = 2
        do {
            $dest = Join-Path $archiveDir ("{0}-{1}{2}" -f $stem, $n, $ext)
            $n++
        } while (Test-Path -LiteralPath $dest)
    }

    if ($DryRun) {
        if ($verbose) { Write-Host "WOULD MOVE $name → $dest" }
        $moved++
    } else {
        Move-Item -LiteralPath $full -Destination $dest -Force
        if ($verbose) { Write-Host "MOVED $name → out/traces/archive-$DateStamp/" }
        $moved++
    }
}

Write-Host ""
Write-Host "Done. moved=$moved deleted=$deleted skipped=$skipped"
if (-not $Delete) {
    Write-Host "Archive: $archiveDir"
}
Write-Host "Policy: never delete without -Delete; prefer out/traces/ for new runs (run-title/scoreboard)."
Write-Host "Never touch source, ISOs, media JSON, or tracked files."
Write-Host "Set DETPS2_CLEAN_TRACES_VERBOSE=1 for per-file lines."

if ($ReportSize) {
    Write-SizeReport
}

exit 0

