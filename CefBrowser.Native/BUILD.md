# Building CefBrowser.Native

## Prerequisites

1. **Visual Studio 2022/2026** with C++ desktop development workload
2. **CMake 3.20+** (included in VS C++ workload)
3. **CEF Binary Distribution 109.1.11** (matching CefSharp 109.1.110)

## Step 1: Download CEF Binary Distribution

1. Go to https://cef-builds.spotifycdn.com/index.html#windows64_builds
2. Find version **109.1.11** (scroll down, it's an older version from Jan 2023)
3. Download the **Windows 64-bit** `.tar.bz2` file
   - Filename: `cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64.tar.bz2`
   - Size: ~200MB compressed

Direct URL:
```
https://cef-builds.spotifycdn.com/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64.tar.bz2
```

## Step 2: Extract CEF

Extract to a path **without spaces**, e.g. `C:\cef\`:

```
C:\cef\cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64
```

Use 7-Zip or tar:
```cmd
7z x cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64.tar.bz2
7z x cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64.tar
```

## Step 3: Build

From the solution root (F:\Test):

```cmd
cd CefBrowser.Native
cmake -B build -G "Visual Studio 17 2022" -A x64 ^
  -DCEF_ROOT=C:/cef/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64 ^
  -DCMAKE_MODULE_PATH=C:/cef/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64/cmake
cmake --build build --config Release
```

Output: `CefBrowser.Native\build\Release\CefBrowser.Native.exe`

## Step 4: Deploy

Copy `build\Release\*` to Avalonia output directory alongside `CefSharp.Avalonia.exe`.

Required files:
- `CefBrowser.Native.exe`
- `libcef.dll`, `chrome_elf.dll`
- `icudtl.dat`, `snapshot_blob.bin`, `v8_context_snapshot.bin`
- `*.pak` files, `locales\`, `d3dcompiler_47.dll`, `libEGL.dll`, `libGLESv2.dll`

## Architecture

```
Avalonia (C#, .NET 8)
  │
  ├── NamedPipe ── CefBrowser.Native (C++, CEF C++ wrapper)
  │                     │
  │               ┌─────┴──────┐
  │               │   CEF UI   │
  │               │   Thread   │
  │               └─────┬──────┘
  │                     │
  ├── SetParent(browser_hwnd, panel_hwnd) ── CEF Browser HWND
  │
  └── Commands: Navigate, Reload, Stop, Resize, Close, EmbedDone
      Events:   Ready, AddressChanged, LoadError
```

## IPC Protocol

Avalonia → C++: `Navigate|url`, `Reload`, `Stop`, `Close`, `EmbedDone`, `Resize|w|h`
C++ → Avalonia: `Ready|HWND_HEX`, `AddressChanged|url`, `LoadError|code|text|url`
