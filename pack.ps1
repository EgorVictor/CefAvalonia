param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$outputDir = "$root\artifacts"

# Step 1: Build native CEF subprocess
Write-Host "=== [1/3] Building CefBrowser.Native ($Configuration) ===" -ForegroundColor Cyan
& "$root\package.ps1" -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Native/.NET build failed" }

# Step 2: Pack the NuGet package
Write-Host "=== [2/3] Packing NuGet ===" -ForegroundColor Cyan
dotnet pack "$root\CefSharp.Avalonia\CefSharp.Avalonia.csproj" -c $Configuration -o $outputDir --no-build
if ($LASTEXITCODE -ne 0) { throw "NuGet pack failed" }

# Step 3: Show result
Write-Host "=== [3/3] Done ===" -ForegroundColor Cyan
$nupkg = Get-ChildItem "$outputDir\*.nupkg" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($nupkg) {
    Write-Host "NuGet package: $($nupkg.FullName)" -ForegroundColor Green
    Write-Host "Size: $([math]::Round($nupkg.Length / 1KB, 1)) KB" -ForegroundColor Green
}
