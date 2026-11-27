param(
    [string]$TargetDir = "D:\Program\FluentNet"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

Write-Host "[1/4] Running FluentNet.Tests..." -ForegroundColor Cyan
dotnet test "$repoRoot\src\FluentNet.Tests\FluentNet.Tests.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

Write-Host "[2/4] Publishing FluentNet.App (WinUI 3 NativeAOT, x64) -> $TargetDir" -ForegroundColor Cyan
dotnet publish "$repoRoot\src\FluentNet.App\FluentNet.App.csproj" -c Release -p:Platform=x64 -o $TargetDir
if ($LASTEXITCODE -ne 0) { throw "FluentNet.App publish failed." }

Write-Host "[3/4] Publishing FluentNet.Cli (x64) -> $TargetDir" -ForegroundColor Cyan
dotnet publish "$repoRoot\src\FluentNet.Cli\FluentNet.Cli.csproj" -c Release -p:Platform=x64 -p:PublishAot=false -p:PublishSingleFile=true --self-contained true -o $TargetDir
if ($LASTEXITCODE -ne 0) { throw "FluentNet.Cli publish failed." }

Write-Host "[4/4] Building FluentNet.Classic (.NET 4.8 WPF, x64) -> $TargetDir" -ForegroundColor Cyan
dotnet build "$repoRoot\src\FluentNet.Classic\FluentNet.Classic.csproj" -c Release -p:Platform=x64 -o $TargetDir
if ($LASTEXITCODE -ne 0) { throw "FluentNet.Classic build failed." }

Write-Host "Published all FluentNet executables to $TargetDir" -ForegroundColor Green
Get-ChildItem $TargetDir -Filter "*.exe" | Select-Object Name, Length, LastWriteTime
