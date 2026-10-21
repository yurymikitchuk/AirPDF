# Publishes builds, zips release assets, and copies a clean source tree for GitHub.
# Run from repo root. Calls publish.ps1 if publish\full or publish\dotnet is missing.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csprojText = Get-Content .\AirPDF.csproj -Raw
if ($csprojText -match '<Version>([^<]+)</Version>') {
    $version = $Matches[1].Trim()
}
else {
    $version = '0.9.6'
}

Write-Host "=== AirPDF $version - GitHub pack ===" -ForegroundColor Cyan

Stop-Process -Name AirPDF -Force -ErrorAction SilentlyContinue

if (-not (Test-Path '.\publish\full\AirPDF.exe') -or -not (Test-Path '.\publish\dotnet\AirPDF.exe')) {
    Write-Host 'Publish output missing - running publish.ps1...' -ForegroundColor Yellow
    & .\publish.ps1
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$assets = Join-Path $PSScriptRoot 'release-assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
Get-ChildItem $assets -Filter 'AirPDF-*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force

$fullZip = Join-Path $assets "AirPDF-$version-win-x64-full.zip"
$dotnetZip = Join-Path $assets "AirPDF-$version-win-x64-dotnet.zip"

Write-Host '=== Zipping release assets ===' -ForegroundColor Cyan
if (Test-Path $fullZip) { Remove-Item $fullZip -Force }
if (Test-Path $dotnetZip) { Remove-Item $dotnetZip -Force }
Compress-Archive -Path (Join-Path $PSScriptRoot 'publish\full\*') -DestinationPath $fullZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $PSScriptRoot 'publish\dotnet\*') -DestinationPath $dotnetZip -CompressionLevel Optimal

$githubRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'AirPDF-github'
Write-Host "=== Copying sources -> $githubRoot ===" -ForegroundColor Cyan
if (Test-Path $githubRoot) {
    Remove-Item -Recurse -Force $githubRoot
}
New-Item -ItemType Directory -Force -Path $githubRoot | Out-Null

$excludeDirs = @('bin', 'obj', 'publish', 'release-assets', '.vs', '.git', '_tess_smoke', 'TestResults')
$excludeFiles = @('*.user', '*.suo', '*.tmp', '*.log')

Get-ChildItem -Force $PSScriptRoot | ForEach-Object {
    $name = $_.Name
    if ($excludeDirs -contains $name) { return }
    if ($_.PSIsContainer) {
        Copy-Item -Recurse -Force $_.FullName (Join-Path $githubRoot $name)
    }
    else {
        $skip = $false
        foreach ($pat in $excludeFiles) {
            if ($name -like $pat) { $skip = $true; break }
        }
        if (-not $skip) {
            Copy-Item -Force $_.FullName (Join-Path $githubRoot $name)
        }
    }
}

if (-not (Test-Path (Join-Path $githubRoot 'README.md'))) {
    throw 'GitHub pack is missing README.md'
}

Write-Host ''
Write-Host '=== Done ===' -ForegroundColor Green
function Show-Size([string]$label, [string]$path) {
    if (-not (Test-Path $path)) {
        Write-Host ("{0,-12} MISSING: {1}" -f $label, $path) -ForegroundColor Red
        return
    }
    if (Test-Path $path -PathType Leaf) {
        Write-Host ("{0,-12} {1,8:N1} MB  {2}" -f $label, ((Get-Item $path).Length / 1MB), $path)
    }
    else {
        $bytes = (Get-ChildItem $path -Recurse -File | Measure-Object Length -Sum).Sum
        $files = @(Get-ChildItem $path -Recurse -File).Count
        Write-Host ("{0,-12} {1,8:N1} MB  ({2} files)  {3}" -f $label, ($bytes / 1MB), $files, $path)
    }
}

Show-Size 'full.zip' $fullZip
Show-Size 'dotnet.zip' $dotnetZip
Show-Size 'sources' $githubRoot
Write-Host ''
Write-Host 'Next: see GITHUB-UPLOAD.md' -ForegroundColor Cyan
