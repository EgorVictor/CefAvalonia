param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

# Single build flow: native CEF subprocess + managed library + NuGet pack.
# CMake/MSVC are resolved from the PATH or via vswhere, so the script is not
# tied to a specific Visual Studio edition or install location.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$outputDir = "$root\artifacts"

# ---- Locate MSVC environment + CMake ----------------------------------------
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vsWhere)) {
    throw "vswhere.exe not found (Visual Studio installer missing?)"
}

$vsDir = & $vsWhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -format value -property installationPath 2>$null
if (-not $vsDir) {
    throw "Visual Studio with the VC++ tools workload was not found"
}
$vcvars = Join-Path $vsDir "VC\Auxiliary\Build\vcvarsall.bat"
if (-not (Test-Path $vcvars)) { throw "vcvarsall.bat not found under $vsDir" }

# CMake: prefer PATH, otherwise fall back to the copy bundled with VS.
$cmake = "cmake"
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    $vsCMake = Join-Path $vsDir "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    if (-not (Test-Path $vsCMake)) { throw "cmake not found on PATH or in Visual Studio" }
    $cmake = $vsCMake
}

# CEF root: environment override, else the documented default layout.
$cefRoot = if ($env:CEF_ROOT) { $env:CEF_ROOT } `
    else { "C:/cef/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64" }

# ---- Step 1: Native CEF subprocess ------------------------------------------
Write-Host "=== [1/3] Building CefBrowser.Native ($Configuration) ===" -ForegroundColor Cyan
pushd "$root\CefBrowser.Native"
try {
    if (-not (Test-Path "build\CMakeCache.txt")) {
        cmd.exe /c """$vcvars"" x64 && ""$cmake"" -B build -G ""Ninja"" -DCMAKE_BUILD_TYPE=$Configuration -DCEF_ROOT=""$cefRoot"" -DCMAKE_MODULE_PATH=""$cefRoot/cmake"" 2>&1"
        if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }
    }
    cmd.exe /c """$vcvars"" x64 && ""$cmake"" --build build --config $Configuration 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "Native build failed" }
}
finally {
    popd
}

# ---- Step 2: Build + pack the managed library -------------------------------
Write-Host "=== [2/3] Building + packing CefSharp.Avalonia ($Configuration) ===" -ForegroundColor Cyan
dotnet build "$root\CefSharp.Avalonia\CefSharp.Avalonia.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw ".NET build failed" }

dotnet pack "$root\CefSharp.Avalonia\CefSharp.Avalonia.csproj" -c $Configuration -o $outputDir
if ($LASTEXITCODE -ne 0) { throw "NuGet pack failed" }

# ---- Step 3: Show result ----------------------------------------------------
Write-Host "=== [3/3] Done ===" -ForegroundColor Cyan
$nupkg = Get-ChildItem "$outputDir\*.nupkg" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($nupkg) {
    Write-Host "NuGet package: $($nupkg.FullName)" -ForegroundColor Green
    Write-Host "Size: $([math]::Round($nupkg.Length / 1KB, 1)) KB" -ForegroundColor Green
}
