# DetPS2Sharp v3.1 portable publish (Phase 56 Completeness)
# Usage: pwsh ./publish.ps1
$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Out = Join-Path $Root "dist\DetPS2-3.1.0-win-x64"

Write-Host "Building Release..."
dotnet build (Join-Path $Root "DetPS2.slnx") -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Running smoke tests..."
dotnet run --project (Join-Path $Root "Tests\DetPS2.Tests.csproj") -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing Desktop..."
dotnet publish (Join-Path $Root "src\DetPS2.Desktop\DetPS2.Desktop.csproj") `
  -c Release -r win-x64 --self-contained false -o $Out --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Copy docs
$docs = @(
  "README.md", "RELEASE_NOTES.md", "COMPATIBILITY.md", "PARITY_PLAN.md", "COMMERCIAL_PLAN.md", "COMPLETENESS.md",
  "LICENSE", "CONTRIBUTING.md", "docs\NETPLAY_CERTIFIED.md", "docs\DX_LIST.md", "docs\ROLLBACK.md"
)
foreach ($d in $docs) {
  $src = Join-Path $Root $d
  if (Test-Path $src) {
    $dest = Join-Path $Out (Split-Path $d -Leaf)
    if ($d.StartsWith("docs\")) {
      $docDir = Join-Path $Out "docs"
      New-Item -ItemType Directory -Force -Path $docDir | Out-Null
      $dest = Join-Path $docDir (Split-Path $d -Leaf)
    }
    Copy-Item $src $dest -Force
  }
}

Write-Host "Portable build ready: $Out"
Write-Host "Run: $Out\DetPS2.Desktop.exe"
Write-Host "Legal: provide your own BIOS/ISOs."
