# Launch DetPS2 Desktop (media library + BIOS/ISO workflow)
# Usage: pwsh ./launch.ps1
$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
Set-Location $Root

$proj = Join-Path $Root "src\DetPS2.Desktop\DetPS2.Desktop.csproj"
$exe  = Join-Path $Root "src\DetPS2.Desktop\bin\Release\net9.0\DetPS2.Desktop.exe"

Write-Host "Building DetPS2.Desktop (Release)..."
dotnet build $proj -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Config will be saved under: $env:LOCALAPPDATA\DetPS2\config.json"
Write-Host "In the app: left panel → Choose media folder… → Choose BIOS file… → Boot"
Write-Host "See PLAY.md for details."
Write-Host ""

if (Test-Path $exe) {
  Write-Host "Starting: $exe"
  # Start GUI process (does not block forever on console attach issues)
  $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
  Write-Host "Launched PID=$($p.Id). If no window appears, check for an error dialog."
} else {
  Write-Host "EXE missing; falling back to dotnet run..."
  dotnet run --project $proj -c Release --no-build
}
