<#
.SYNOPSIS
  Move root-level investigation trace noise into out/traces/archive-YYYYMMDD/.

.DESCRIPTION
  Targets repo-root files matching:
    b3-*.txt  bo2-*.txt  gow-*.txt  sm-*.txt  *-err.txt  *-out.txt

  Default action is MOVE (never delete unless -Delete).
  Only processes files that are gitignored noise (or match the patterns when git is unavailable);
  skips tracked files and files already under out/.

.PARAMETER Delete
  Permanently delete matching root files instead of moving them.

.PARAMETER DryRun
  Print actions without moving or deleting.

.PARAMETER DateStamp
  Archive folder suffix (default: today's yyyyMMdd).

.EXAMPLE
  pwsh ./tools/clean-traces.ps1
  pwsh ./tools/clean-traces.ps1 -DryRun
  pwsh ./tools/clean-traces.ps1 -Delete
#>
[CmdletBinding()]
param(
    [switch]$Delete,
    [switch]$DryRun,
    [string]$DateStamp = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

if (-not $DateStamp) {
    $DateStamp = Get-Date -Format "yyyyMMdd"
}
$archiveDir = Join-Path $repoRoot "out/traces/archive-$DateStamp"

$patterns = @(
    "b3-*.txt",
    "bo2-*.txt",
    "gow-*.txt",
    "sm-*.txt",
    "*-err.txt",
    "*-out.txt"
)

function Test-IsGitIgnored {
    param([string]$Path)
    # Prefer git check-ignore: only clean true ignore-noise; never touch tracked files.
    Push-Location $repoRoot
    try {
        $null = & git rev-parse --is-inside-work-tree 2>$null
        if ($LASTEXITCODE -ne 0) { return $true } # no git → treat pattern match as eligible
        & git check-ignore -q -- $Path 2>$null
        return ($LASTEXITCODE -eq 0)
    } finally {
        Pop-Location
    }
}

function Test-IsTracked {
    param([string]$Path)
    Push-Location $repoRoot
    try {
        $null = & git rev-parse --is-inside-work-tree 2>$null
        if ($LASTEXITCODE -ne 0) { return $false }
        & git ls-files --error-unmatch -- $Path 2>$null | Out-Null
        return ($LASTEXITCODE -eq 0)
    } finally {
        Pop-Location
    }
}

$candidates = @()
foreach ($pat in $patterns) {
    $candidates += Get-ChildItem -Path $repoRoot -File -Filter $pat -ErrorAction SilentlyContinue
}
$candidates = $candidates | Sort-Object FullName -Unique

$moved = 0
$deleted = 0
$skipped = 0

Write-Host "=== clean-traces (root -> archive-$DateStamp) Delete=$Delete DryRun=$DryRun ==="

if (-not $Delete -and -not $DryRun) {
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
        Write-Host "SKIP tracked: $name"
        $skipped++
        continue
    }

    # Prefer gitignored noise; also allow untracked pattern matches (e.g. brand-new patterns).
    if (-not (Test-IsGitIgnored -Path $name)) {
        Push-Location $repoRoot
        try {
            $status = & git status --porcelain -- $name 2>$null
            $isUntracked = $status -and ($status -match '^\?\?')
            if (-not $isUntracked) {
                Write-Host "SKIP not-ignore-noise: $name"
                $skipped++
                continue
            }
        } finally {
            Pop-Location
        }
    }

    if ($Delete) {
        if ($DryRun) {
            Write-Host "WOULD DELETE $name"
        } else {
            Remove-Item -LiteralPath $full -Force
            Write-Host "DELETED $name"
            $deleted++
        }
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
        Write-Host "WOULD MOVE $name → $dest"
    } else {
        Move-Item -LiteralPath $full -Destination $dest -Force
        Write-Host "MOVED $name → out/traces/archive-$DateStamp/"
        $moved++
    }
}

Write-Host ""
Write-Host "Done. moved=$moved deleted=$deleted skipped=$skipped"
if (-not $Delete) {
    Write-Host "Archive: $archiveDir"
}
Write-Host "Policy: never delete without -Delete; prefer out/traces/ for new runs (run-title/scoreboard)."
exit 0
