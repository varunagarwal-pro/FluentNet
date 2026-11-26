param(
    [ValidateSet("major", "minor", "patch")]
    [string]$Part = "patch"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$versionFile = Join-Path $repoRoot "version.txt"

if (-not (Test-Path $versionFile)) {
    throw "version.txt not found at $versionFile"
}

$raw = (Get-Content -Raw $versionFile).Trim()
if ($raw -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "Invalid SemVer in version.txt: '$raw' (expected MAJOR.MINOR.PATCH)"
}

$major = [int]$matches[1]
$minor = [int]$matches[2]
$patch = [int]$matches[3]

switch ($Part) {
    "major" { $major++; $minor = 0; $patch = 0 }
    "minor" { $minor++; $patch = 0 }
    "patch" { $patch++ }
}

$next = "$major.$minor.$patch"
[System.IO.File]::WriteAllText($versionFile, "$next`n", (New-Object System.Text.UTF8Encoding $false))
Write-Host "Version bumped: $raw -> $next" -ForegroundColor Green
