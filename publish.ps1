param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

# Publish a redistributable zip of the TestBrowserApp demo.
# Delegates the actual build (native + managed) to pack.ps1 so there is a
# single source of truth for the toolchain discovery logic.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition

Write-Host "=== Building (native + managed) ===" -ForegroundColor Cyan
& "$root\pack.ps1" -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# The library csproj copies the native host + CEF binaries next to the managed
# assembly on build, so the build output directory is already self-contained.
$tfm = "net8.0"
$outputDir = "$root\TestBrowserApp\bin\$Configuration\$tfm"
if (-not (Test-Path "$outputDir\TestBrowserApp.exe")) {
    throw "TestBrowserApp build output not found at $outputDir (run a build first)"
}

Write-Host "=== Packaging TestBrowserApp ($Configuration) ===" -ForegroundColor Cyan
$package = "$root\CefBrowser-Win-x64.zip"
if (Test-Path $package) { Remove-Item $package -Force }
Compress-Archive -Path "$outputDir\*" -DestinationPath $package -Force
Write-Host "Package created: $package" -ForegroundColor Green
Write-Host "Size: $([math]::Round((Get-Item $package).Length / 1MB, 1)) MB" -ForegroundColor Green
