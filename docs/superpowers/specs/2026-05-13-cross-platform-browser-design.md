# CefBrowser Cross-Platform (Windows + Linux) Design

## Goal
Transform CefBrowser from Windows-only to Windows + Linux dual-platform, supporting Win7 and KylinOS V10 (X11).

## Architecture

```
┌──────────────────────────────────────────────────┐
│              Avalonia App (.NET 8)               │
│  ┌─────────────────────────────────────────────┐  │
│  │              BrowserView                     │  │
│  │  ┌─────────────────────────────────────────┐ │  │
│  │  │   ExternalBrowserProcessHost            │ │  │
│  │  │   (Platform-native window embedding)     │ │  │
│  │  │   Windows: NativeControlHost + SetParent │ │  │
│  │  │   Linux:   NativeControlHost + XReparent │ │  │
│  │  └─────────────────────────────────────────┘ │  │
│  │  ┌─────────────────────────────────────────┐ │  │
│  │  │   BrowserProcessManager                 │ │  │
│  │  │   (stdin/stdout IPC, platform-agnostic)  │ │  │
│  │  └─────────────────────────────────────────┘ │  │
│  └─────────────────────────────────────────────┘  │
│                         │                          │
│                   stdin/stdout                     │
│                   (cross-platform)                 │
└────────────────────┬─────────────────────────────┘
                     │
┌────────────────────┴─────────────────────────────┐
│          CefBrowser.Native (C++, CEF)             │
│  ┌─────────────────────────────────────────────┐  │
│  │              StdioServer                     │  │
│  │  (reads commands from stdin, writes events   │  │
│  │   to stdout, platform-agnostic)              │  │
│  └─────────────────────────────────────────────┘  │
│  ┌─────────────────────────────────────────────┐  │
│  │              BrowserHandler                  │  │
│  │  (CEF Client / LifeSpan / Load / Display)     │  │
│  └─────────────────────────────────────────────┘  │
│  ┌─────────────────────────────────────────────┐  │
│  │           CEF Browser Instance               │  │
│  │  (Chromium 109, platform-specific binary)    │  │
│  └─────────────────────────────────────────────┘  │
```

## Changes

### 1. IPC: Named Pipe → stdin/stdout

**C++ side:** Replace `PipeServer` (`pipe_server.h/.cpp`) with `StdioServer`:
- ReadLine from `stdin`, WriteLine to `stdout`
- Same `Cmd|arg\n` protocol, zero wire-format changes
- Remove all `CreateNamedPipe`/`ConnectNamedPipe`/`ReadFile`/`WriteFile` code
- Cross-platform: no special API, works on both OSes
- On Windows: `_setmode(_fileno(stdin), _O_BINARY)` / `_setmode(_fileno(stdout), _O_BINARY)` to prevent CR/LF translation

**C# side:** `BrowserProcessManager`:
- Replace `NamedPipeClientStream` with `Process.Start()` + `RedirectStandardInput`/`RedirectStandardOutput`
- Writer task → `process.StandardInput.WriteLineAsync()`
- Reader task → `process.StandardOutput.ReadLineAsync()`
- Remove pipe retry logic (process starts instantly, no connection delay)
- Remove `--cef-pipe`/`--cef-host-pid` args (no longer needed)

### 2. Window Embedding (Two-Step, both platforms)

**Windows (keep current):**
1. C++ creates browser with `SetAsChild(hiddenParent)` (hidden popup window)
2. C++ sends `Ready|HWND_HEX` via stdout
3. C# receives HWND, calls `ExternalBrowserProcessHost.EmbedWindow(hwnd)`:
   - `SetWindowLong(childHwnd, GWL_STYLE, WS_CHILD|WS_VISIBLE)`
   - `SetParent(childHwnd, panelHwnd)`
   - `MoveWindow(childHwnd, 0, 0, w, h)`
4. C# sends `EmbedDone` → C++ shows the window

**Linux (new):**
1. C++ creates browser with `CefWindowInfo.SetAsChild(0)` (no parent initially, or hidden dummy)
2. C++ sends `Ready|X11_WINDOW_HEX` via stdout
3. C# receives X11 Window ID (32-bit), calls `ExternalBrowserProcessHost.EmbedWindow(xid)`:
   - `XReparentWindow(display, xid, panelXid, 0, 0)`
   - `XMapWindow(display, xid)`
   - `XFlush(display)`
4. C# sends `EmbedDone`

### 3. TargetFramework

`CefSharp.Avalonia.csproj`: multi-target `net8.0-windows;net8.0`
- `net8.0-windows`: Windows build (HWND interop via P/Invoke)
- `net8.0`: Linux build (X11 interop via libX11 P/Invoke)
- `UseWindowsForms` only on Windows TFM: `Condition="$([MSBuild]::IsTargetFramework('net8.0-windows'))"`
- `ExternalBrowserProcessHost` uses `#if NET8_0_OR_GREATER` + runtime platform detection

The consuming project targets `net8.0` (Linux) or `net8.0-windows` (Windows). The correct TFM is selected based on the consuming project's TFM.

### 4. C++ Build

`CMakeLists.txt`:
- Windows: `OS_WINDOWS` block (keep current with `WIN32` flag)
- Linux: `OS_LINUX` block with CEF Linux libs
- Remove Windows-only libs (`d3d11.lib`, `imm32.lib`, `opengl32.lib`) for Linux build
- `add_executable` uses `WIN32` only on Windows

### 5. Protocol (unchanged)

```
C#→Native: Navigate|url, Reload, Stop, Close, EmbedDone, Resize|w|h
Native→C#: Ready|WINDOW_HEX, AddressChanged|url, LoadError|code|text|url,
           NavState|isLoading|canGoBack|canGoForward, TitleChanged|title
```

### 6. Test Project

- `TestBrowserApp` already works (Avalonia-based)
- On Linux: `dotnet run -c Release` with CefSharp.Avalonia's net8.0 TFM
- Add `TestBrowserApp.Linux/` project or just document `dotnet run` on Linux

## File Changes

| File | Change |
|------|--------|
| `CefBrowser.Native/src/main.cpp` | Replace PipeServer with StdioServer; platform-conditional includes and window creation |
| `CefBrowser.Native/src/pipe_server.h` | **Delete** |
| `CefBrowser.Native/src/pipe_server.cpp` | **Delete** |
| `CefBrowser.Native/src/stdio_server.h` | **New** — ReadLine from stdin, WriteLine to stdout |
| `CefBrowser.Native/src/stdio_server.cpp` | **New** |
| `CefBrowser.Native/CMakeLists.txt` | Add Linux build support, remove Windows-only deps |
| `CefSharp.Avalonia/BrowserProcessManager.cs` | Replace Named Pipe with Process stdio |
| `CefSharp.Avalonia/ExternalBrowserProcessHost.cs` | Add Linux X11 embedding path |
| `CefSharp.Avalonia/CefSharp.Avalonia.csproj` | Multi-target `net8.0-windows;net8.0` |

## NuGet Packaging

- `nativeBinaries/` → platform subfolders: `win-x64/` + `linux-x64/`
- Build targets choose correct binary per `$(RuntimeIdentifier)`
- Each platform TFM (`net8.0-windows` / `net8.0`) packages its own assembly
