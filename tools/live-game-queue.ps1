#Requires -Version 7
<#
.SYNOPSIS
  Live game test queue: at most 3 concurrent title runs (blocker-trace / diag).
  Other agents enqueue JSON jobs; this runner drains the queue.

.DESCRIPTION
  Job file schema (one JSON object per file in out/live-queue/inbox/*.json):
  {
    "id": "unique-id",
    "media": "user-media-deception.json",
    "cycles": 50000000,
    "hostPresent": true,
    "nativeBios": false,
    "priority": 0
  }

  Results written to out/live-queue/done/<id>.json and out/live-queue/done/<id>.txt

  Max concurrent: 3 (override -MaxConcurrent)
#>
param(
  [int]$MaxConcurrent = 3,
  [int]$PollMs = 1500,
  [switch]$Once,
  # Exit when inbox empty, no running jobs, and idle this many minutes (0 = never).
  [double]$IdleExitMinutes = 0,
  # Hard wall-clock cap in minutes (0 = never).
  [double]$MaxRuntimeMinutes = 0
)

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path (Join-Path $Root "src\DetPS2.Core"))) {
  $Root = $PSScriptRoot + "\.."
}
Set-Location $Root

$inbox = Join-Path $Root "out\live-queue\inbox"
$running = Join-Path $Root "out\live-queue\running"
$done = Join-Path $Root "out\live-queue\done"
$logDir = Join-Path $Root "out\live-queue\logs"
New-Item -ItemType Directory -Force -Path $inbox, $running, $done, $logDir | Out-Null

$coreDll = Join-Path $Root "src\DetPS2.Core\bin\Release\net9.0\DetPS2.Core.dll"
if (-not (Test-Path $coreDll)) {
  Write-Host "Building Core..."
  dotnet build (Join-Path $Root "src\DetPS2.Core\DetPS2.Core.csproj") -c Release --nologo | Out-Host
}

$jobs = [System.Collections.Concurrent.ConcurrentDictionary[string, System.Diagnostics.Process]]::new()

function Get-InboxJobs {
  Get-ChildItem $inbox -Filter *.json -ErrorAction SilentlyContinue |
    Sort-Object {
      try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).priority } catch { 0 }
    }, LastWriteTime
}

function Get-HostBlockerTraceCount {
  # Host-wide seat rule: count ALL game CLI processes (blocker-trace / pad-inject / scoreboard-metrics),
  # including ones not owned by this runner. NEVER start a 4th concurrent game on the machine.
  try {
    return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
      $_.Name -match '^(dotnet|DetPS2)' -and $_.CommandLine -and (
        $_.CommandLine -match 'blocker-trace|pad-inject|scoreboard-metrics'
      )
    }).Count
  } catch {
    return $jobs.Count
  }
}

function Start-JobFile([System.IO.FileInfo]$file) {
  # Re-check host-wide concurrency immediately before spawn (external agents / other seats).
  $hostN = Get-HostBlockerTraceCount
  if ($hostN -ge $MaxConcurrent) {
    Write-Host "[queue] SKIP start (host blocker-trace=$hostN >= $MaxConcurrent); leaving job in inbox"
    return $false
  }

  $raw = Get-Content $file.FullName -Raw
  $j = $raw | ConvertFrom-Json
  $id = if ($j.id) { [string]$j.id } else { [guid]::NewGuid().ToString("N") }
  $media = [string]$j.media
  $cycles = if ($j.cycles) { [ulong]$j.cycles } else { 50000000UL }
  $hostP = if ($null -eq $j.hostPresent) { $true } else { [bool]$j.hostPresent }
  $native = if ($null -eq $j.nativeBios) { $false } else { [bool]$j.nativeBios }
  # Optional: "blocker-trace" (default) or "pad-inject" (START/CROSS INTERACTIVE probe)
  $cmd = if ($j.command) { [string]$j.command } else { "blocker-trace" }
  $padScript = if ($j.padScript) { [string]$j.padScript } else { $null }
  $extraArgs = @()
  if ($j.extraArgs) {
    if ($j.extraArgs -is [System.Array]) { $extraArgs = @($j.extraArgs | ForEach-Object { [string]$_ }) }
    else { $extraArgs = @([string]$j.extraArgs) }
  }

  $mediaPath = if ([IO.Path]::IsPathRooted($media)) { $media } else { Join-Path $Root $media }
  if (-not (Test-Path $mediaPath)) {
    @{ id = $id; ok = $false; error = "media missing: $mediaPath" } |
      ConvertTo-Json | Set-Content (Join-Path $done "$id.json")
    Move-Item $file.FullName (Join-Path $done "$id.job.json") -Force
    return $true
  }

  $runPath = Join-Path $running "$id.json"
  try {
    Move-Item $file.FullName $runPath -Force -ErrorAction Stop
  } catch {
    # Another seat claimed it first
    Write-Host "[queue] claim race on $($file.Name); skip"
    return $false
  }

  # Avoid $args (automatic variable); build argv for `dotnet exec ... blocker-trace|pad-inject`
  $procArgs = [System.Collections.Generic.List[string]]::new()
  if ($cmd -eq "pad-inject") {
    $procArgs.AddRange([string[]]@("exec", $coreDll, "pad-inject", $mediaPath, "--cycles=$cycles"))
  } else {
    $procArgs.AddRange([string[]]@("exec", $coreDll, "blocker-trace", $mediaPath, "--cycles=$cycles"))
  }
  if ($hostP) { $procArgs.Add("--host-present") }
  if ($native) { $procArgs.Add("--native-bios") }
  if ($padScript) {
    $psPath = if ([IO.Path]::IsPathRooted($padScript)) { $padScript } else { Join-Path $Root $padScript }
    $procArgs.Add("--pad-script=$psPath")
  }
  foreach ($ea in $extraArgs) {
    if ($ea) { $procArgs.Add($ea) }
  }

  $outLog = Join-Path $logDir "$id-out.txt"
  $errLog = Join-Path $logDir "$id-err.txt"
  # Wipe prior logs so re-runs don't mix output
  if (Test-Path $outLog) { Remove-Item $outLog -Force }
  if (Test-Path $errLog) { Remove-Item $errLog -Force }

  # Final host-wide check after claim, before spawn — never start a 4th game.
  $hostN2 = Get-HostBlockerTraceCount
  if ($hostN2 -ge $MaxConcurrent) {
    Write-Host "[queue] ABORT spawn id=$id (host blocker-trace=$hostN2 >= $MaxConcurrent); requeue"
    Move-Item $runPath $file.FullName -Force -ErrorAction SilentlyContinue
    return $false
  }

  # Start-Process file redirects are reliable (handlers-after-Begin* was broken).
  # Pass ArgumentList as string[] so each token is one argv entry.
  $p = Start-Process -FilePath "dotnet" -ArgumentList $procArgs.ToArray() `
    -WorkingDirectory $Root `
    -RedirectStandardOutput $outLog `
    -RedirectStandardError $errLog `
    -PassThru -NoNewWindow

  if (-not $p) {
    @{ id = $id; ok = $false; error = "failed to start process" } |
      ConvertTo-Json | Set-Content (Join-Path $done "$id.json") -Encoding utf8
    if (Test-Path $runPath) {
      Move-Item $runPath (Join-Path $done "$id.job.json") -Force
    }
    return $true
  }

  $jobs[$id] = $p
  Write-Host "[queue] START id=$id cmd=$cmd media=$media cycles=$cycles pid=$($p.Id) owned=$($jobs.Count) host=$(Get-HostBlockerTraceCount)"
  return $true
}

function Get-LastMatchValue([string]$text, [string]$pattern) {
  # Prefer last match so early progress/debug lines don't shadow the final claim.
  $m = [regex]::Matches($text, $pattern)
  if ($m.Count -eq 0) { return $null }
  return $m[$m.Count - 1].Groups[1].Value
}

function Read-LogText([string]$path) {
  if (-not (Test-Path $path)) { return "" }
  # Retry: Start-Process redirects can lag a tick after HasExited.
  for ($i = 0; $i -lt 8; $i++) {
    try {
      $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
      try {
        $sr = New-Object System.IO.StreamReader($fs)
        try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
      } finally { $fs.Dispose() }
    } catch {
      Start-Sleep -Milliseconds 150
    }
  }
  try { return Get-Content $path -Raw -ErrorAction Stop } catch { return "" }
}

function Complete-Finished {
  foreach ($id in @($jobs.Keys)) {
    $p = $jobs[$id]
    if (-not $p.HasExited) { continue }
    # Ensure process handle fully released so redirect files are flushed.
    try { $p.WaitForExit(5000) | Out-Null } catch { }
    Start-Sleep -Milliseconds 200
    $null = $jobs.TryRemove($id, [ref]$p)
    $outLog = Join-Path $logDir "$id-out.txt"
    $errLog = Join-Path $logDir "$id-err.txt"
    $outTxt = Read-LogText $outLog
    $errTxt = Read-LogText $errLog
    # Scrape BOTH streams: pad-inject puts MKFAM/assist metrics on stderr; claim lines on stdout.
    $txt = $outTxt + "`n" + $errTxt
    $px = 0; $lit = 0; $pc = ""; $prims = 0
    # Prefer claim: line (authoritative Soft-GS summary), else last px=/lit=/prims=.
    if ($txt -match 'claim:\s*px=(\d+)\s+prims=(\d+).*?\blit=(\d+)') {
      $px = [long]$Matches[1]
      $prims = [int]$Matches[2]
      $lit = [long]$Matches[3]
    } else {
      $pxV = Get-LastMatchValue $txt 'px=(\d+)'
      $litV = Get-LastMatchValue $txt '(?:softgs-present:\s*)?lit=(\d+)'
      $primsV = Get-LastMatchValue $txt 'prims=(\d+)'
      if ($null -ne $pxV) { $px = [long]$pxV }
      if ($null -ne $litV) { $lit = [long]$litV }
      if ($null -ne $primsV) { $prims = [int]$primsV }
    }
    # pad-inject table: "  <cyc>  0xPC  <px>  <prims> ... (final)" — authoritative end sample
    if ($outTxt -match '\(final\)') {
      $fm = [regex]::Matches($outTxt, '^\s*\d+\s+0x([0-9A-Fa-f]+)\s+(\d+)\s+(\d+)\b.*\(final\)', 'Multiline')
      if ($fm.Count -gt 0) {
        $last = $fm[$fm.Count - 1]
        if ([string]::IsNullOrEmpty($pc)) { $pc = "0x$($last.Groups[1].Value)" }
        $tablePx = [long]$last.Groups[2].Value
        $tablePrims = [int]$last.Groups[3].Value
        if ($tablePx -gt $px) { $px = $tablePx }
        if ($tablePrims -gt $prims) { $prims = $tablePrims }
      }
    }
    # Final after-N-cyc PC, or last uppercase PC=0x…
    if ([string]::IsNullOrEmpty($pc)) {
      $pcV = Get-LastMatchValue $txt 'after\s+\d+\s+cyc:\s*PC=0x([0-9A-Fa-f]+)'
      if ($null -eq $pcV) { $pcV = Get-LastMatchValue $txt 'PC=0x([0-9A-Fa-f]+)' }
      if ($null -ne $pcV) { $pc = "0x$pcV" }
    }
    $result = [ordered]@{
      id = $id
      ok = ($p.ExitCode -eq 0)
      exitCode = $p.ExitCode
      px = $px
      lit = $lit
      prims = $prims
      pc = $pc
      outLog = $outLog
      errLog = $errLog
      finishedUtc = [DateTime]::UtcNow.ToString("o")
    }
    $result | ConvertTo-Json | Set-Content (Join-Path $done "$id.json") -Encoding utf8
    if (Test-Path (Join-Path $running "$id.json")) {
      Move-Item (Join-Path $running "$id.json") (Join-Path $done "$id.job.json") -Force
    }
    Write-Host "[queue] DONE  id=$id exit=$($p.ExitCode) px=$px lit=$lit prims=$prims"
  }
}

# Hard cap: never exceed MaxConcurrent concurrent game processes (seat rule).
if ($MaxConcurrent -gt 3) {
  Write-Host "[queue] WARNING: MaxConcurrent=$MaxConcurrent requested; clamping to 3 (seat rule)"
  $MaxConcurrent = 3
}

$startedUtc = [DateTime]::UtcNow
$lastActivityUtc = $startedUtc
$stopClaiming = $false
Write-Host "Live game queue runner MaxConcurrent=$MaxConcurrent IdleExitMinutes=$IdleExitMinutes MaxRuntimeMinutes=$MaxRuntimeMinutes root=$Root"
do {
  Complete-Finished
  if ($jobs.Count -gt 0) { $lastActivityUtc = [DateTime]::UtcNow }

  if ($MaxRuntimeMinutes -gt 0 -and -not $stopClaiming) {
    $elapsedMin = ([DateTime]::UtcNow - $startedUtc).TotalMinutes
    if ($elapsedMin -ge $MaxRuntimeMinutes) {
      Write-Host "[queue] MaxRuntimeMinutes=$MaxRuntimeMinutes reached; drain running only (no new claims)"
      $stopClaiming = $true
    }
  }

  if (-not $stopClaiming) {
    while ($true) {
      $hostN = Get-HostBlockerTraceCount
      # Seat rule: never exceed MaxConcurrent games host-wide (owned + external).
      if ($hostN -ge $MaxConcurrent) { break }
      if ($jobs.Count -ge $MaxConcurrent) { break }
      $next = Get-InboxJobs | Select-Object -First 1
      if (-not $next) { break }
      $ok = Start-JobFile $next
      if (-not $ok) { break }
      $lastActivityUtc = [DateTime]::UtcNow
    }
  }

  $idle = ($jobs.Count -eq 0) -and -not (Get-InboxJobs)
  if ($Once -and $idle) { break }
  if ($stopClaiming -and $jobs.Count -eq 0) { break }

  if ($IdleExitMinutes -gt 0 -and $idle) {
    $idleMin = ([DateTime]::UtcNow - $lastActivityUtc).TotalMinutes
    if ($idleMin -ge $IdleExitMinutes) {
      Write-Host "[queue] IdleExitMinutes=$IdleExitMinutes with empty inbox/running; exit"
      break
    }
  }

  Start-Sleep -Milliseconds $PollMs
} while ($true)

Complete-Finished
Write-Host "Queue runner exit."

