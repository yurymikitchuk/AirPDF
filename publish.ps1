# Builds both AirPDF publish variants into .\publish\
#   full\   — self-contained (no .NET install needed)
#   dotnet\ — framework-dependent (needs .NET Desktop Runtime win-x64)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Stop-Process -Name AirPDF -Force -ErrorAction SilentlyContinue

Write-Host '=== Cleaning bin/obj ===' -ForegroundColor Cyan
Remove-Item -Recurse -Force .\bin, .\obj -ErrorAction SilentlyContinue

Write-Host '=== Publish FULL (self-contained) ===' -ForegroundColor Cyan
dotnet publish .\AirPDF.csproj -c Release -p:Platform=x64 `
  -p:PublishProfile=Full `
  --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host '=== Publish DOTNET (framework-dependent) ===' -ForegroundColor Cyan
dotnet publish .\AirPDF.csproj -c Release -p:Platform=x64 `
  -p:PublishProfile=Dotnet `
  --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# App is win-x64 only — drop unused Tesseract x86 natives and debugger junk.
foreach ($dir in @('.\publish\full', '.\publish\dotnet')) {
    Remove-Item -Recurse -Force (Join-Path $dir 'x86') -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $dir 'createdump.exe') -ErrorAction SilentlyContinue
}

# Intermediate build trees are not needed after publish output is ready.
Remove-Item -Recurse -Force .\bin, .\obj -ErrorAction SilentlyContinue

function Show-Size([string]$label, [string]$path) {
    if (-not (Test-Path $path)) {
        Write-Host ("{0,-10} MISSING: {1}" -f $label, $path) -ForegroundColor Red
        return
    }
    $bytes = (Get-ChildItem $path -Recurse -File | Measure-Object Length -Sum).Sum
    $files = (Get-ChildItem $path -Recurse -File).Count
    Write-Host ("{0,-10} {1,8:N1} MB  ({2} files)  {3}" -f $label, ($bytes / 1MB), $files, $path)
}

Write-Host ''
Write-Host '=== Done ===' -ForegroundColor Green
Show-Size 'full' '.\publish\full'
Show-Size 'dotnet' '.\publish\dotnet'
