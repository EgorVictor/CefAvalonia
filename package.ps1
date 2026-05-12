param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Step 1: Build native CEF subprocess
Write-Host "=== [1/3] Building CefBrowser.Native ($Configuration) ===" -ForegroundColor Cyan
$cmake = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$cefRoot = "C:/cef/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64"
$vcvars = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvarsall.bat"
$vcvarsVer = "14.44"

pushd "$root\CefBrowser.Native"
if (-not (Test-Path "build\CMakeCache.txt")) {
    cmd.exe /c """$vcvars"" x64 -vcvars_ver=$vcvarsVer && ""$cmake"" -B build -G ""Ninja"" -DCMAKE_BUILD_TYPE=$Configuration -DCEF_ROOT=""$cefRoot"" -DCMAKE_MODULE_PATH=""$cefRoot/cmake"" 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }
}
cmd.exe /c """$vcvars"" x64 -vcvars_ver=$vcvarsVer && ""$cmake"" --build build --config $Configuration 2>&1"
if ($LASTEXITCODE -ne 0) { throw "Native build failed" }
popd

# Step 2: Build .NET projects
Write-Host "=== [2/3] Building .NET ($Configuration) ===" -ForegroundColor Cyan
dotnet build "$root\TestBrowserApp\TestBrowserApp.csproj" -c $Configuration --no-self-contained
if ($LASTEXITCODE -ne 0) { throw ".NET build failed" }

# Step 3: Package
Write-Host "=== [3/3] Packaging ===" -ForegroundColor Cyan
$outputDir = "$root\TestBrowserApp\bin\$Configuration\net8.0-windows"
$package = "$root\CefBrowser-Win7-x64.zip"

Compress-Archive -Path "$outputDir\*" -DestinationPath $package -Force
Write-Host "Package created: $package" -ForegroundColor Green
Write-Host "Size: $([math]::Round((Get-Item $package).Length / 1MB, 1)) MB" -ForegroundColor Green
