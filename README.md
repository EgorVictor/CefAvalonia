# CefBrowser

基于 **CEF (Chromium Embedded Framework)** 的 .NET Avalonia 浏览器控件，通过 C++ 原生子进程 + **stdio（标准输入/输出）IPC** 实现嵌入式浏览器 HWND 托管。

支持 **Windows**（x64，原生窗口嵌入）与 **Linux**（x64，离屏渲染 OSR）。目标框架为 **.NET 8**。

## 简介

CefBrowser 将 CEF 浏览器引擎封装为一个 Avalonia `WebView` 控件，提供完整的浏览功能（导航、加载状态、标题同步、下载、JS 对话框等），并暴露 C# 事件与异步 API。

**设计目标：**
- CEF 运行在独立的原生进程，与 .NET 进程解耦（一个原生宿主进程可服务多个 `WebView`）。
- 通过 HWND 嵌入（`SetParent`）实现浏览器与 Avalonia 控件无缝融合。
- 进程间通信走 stdin/stdout，按行分帧（文本字段经转义），带 `Hello|{version}` 握手。

---

## 架构

```
┌──────────────────────────────────────────────────┐
│              Avalonia App (.NET 8)               │
│  ┌─────────────────────────────────────────────┐  │
│  │              WebView                        │  │
│  │  ┌─────────────────────────────────────────┐ │  │
│  │  │   ExternalBrowserProcessHost            │ │  │
│  │  │   (NativeControlHost, HWND 嵌入)        │ │  │
│  │  └─────────────────────────────────────────┘ │  │
│  │  ┌─────────────────────────────────────────┐ │  │
│  │  │   BrowserProcessManager                 │ │  │
│  │  │   (浏览器生命周期 + stdio IPC 客户端)    │ │  │
│  │  └─────────────────────────────────────────┘ │  │
│  └─────────────────────────────────────────────┘  │
│                         │                          │
│                      stdio (stdin/stdout)           │
└────────────────────┬─────────────────────────────┘
                     │
┌────────────────────┴─────────────────────────────┐
│          CefBrowser.Native (C++, CEF)             │
│  ┌─────────────────────────────────────────────┐  │
│  │              StdioServer                    │  │
│  │  (stdio 服务端, 读/写双线程, 事件队列)       │  │
│  └─────────────────────────────────────────────┘  │
│  ┌─────────────────────────────────────────────┐  │
│  │              BrowserHandler                 │  │
│  │  (CEF Client / LifeSpan / Load / Display)   │  │
│  └─────────────────────────────────────────────┘  │
│  ┌─────────────────────────────────────────────┐  │
│  │           CEF Browser Instance              │  │
│  │  (Chromium 109, 独立渲染进程)               │  │
│  └─────────────────────────────────────────────┘  │
│                        │                           │
│                  SetParent(browser_hwnd)           │
└──────────────────────────────────────────────────┘
```

### 通信协议（v3，行协议）

文本字段（URL、标题、路径、对话框文本）一律用 `CefProtocol.Escape` 转义，避免 `|`、CR、LF 破坏分帧。所有命令/事件携带浏览器 `id`。

| 方向 | 命令/事件 | 格式 |
|------|-----------|------|
| C# → C++ | `Create` | `Create\|{id}\|{cachePath}\|{url}` |
| C# → C++ | `Navigate` | `Navigate\|{id}\|{url}` |
| C# → C++ | `Reload` / `Stop` / `Close` / `EmbedDone` | `...\|{id}` |
| C# → C++ | `Resize` | `Resize\|{id}\|{w}\|{h}` |
| C# → C++ | `Zoom` / `Find` / `ExecJS` | `...\|{id}\|{payload}` |
| C# → C++ | `JsDialogResult` | `JsDialogResult\|{id}\|{dialogId}\|{accept}\|{text}` |
| C# → C++ | `Quit` | `Quit` |
| C++ → C# | `Hello` | `Hello\|{version}` |
| C++ → C# | `Ready` | `Ready\|{id}\|{HWND_HEX}` |
| C++ → C# | `AddressChanged` | `AddressChanged\|{id}\|{url}` |
| C++ → C# | `LoadError` | `LoadError\|{id}\|{code}\|{text}\|{url}` |
| C++ → C# | `NavState` | `NavState\|{id}\|{isLoading}\|{canGoBack}\|{canGoForward}` |
| C++ → C# | `TitleChanged` | `TitleChanged\|{id}\|{title}` |
| C++ → C# | `OpenPopup` | `OpenPopup\|{id}\|{url}` |
| C++ → C# | `JsDialog` | `JsDialog\|{id}\|{dialogId}\|{type}\|{message}\|{default}` |
| C++ → C# | `Download*` | `DownloadStart\|{id}\|{path}` 等 |

---

## 项目结构

```
CefBrowser/
├── CefBrowser.Native/              # C++ CEF 原生宿主 (CMake 项目)
│   ├── CMakeLists.txt              # CMake 构建配置
│   ├── prebuilt/                   # 不再使用；请从源码构建（见 .gitignore）
│   └── src/
│       ├── main.cpp                # 入口、CEF 初始化、消息泵、命令调度
│       ├── browser_handler.h/.cpp  # CEF Client 回调处理
│       ├── stdio_server.h/.cpp     # stdio 服务端（双线程读写）
│       └── protocol.h              # 行协议转义/切分辅助
│
├── CefSharp.Avalonia/              # C# Avalonia 浏览器控件库 (.NET 8)
│   ├── CefSharp.Avalonia.csproj
│   ├── WebView.cs                  # 可复用的 Avalonia UserControl
│   ├── BrowserProcessManager.cs    # 单浏览器外观 + stdio IPC 客户端
│   ├── CefHostProcess.cs           # 共享原生宿主进程（崩溃恢复、引用计数）
│   ├── ExternalBrowserProcessHost.cs # HWND 嵌入 (NativeControlHost)
│   ├── CefSettings.cs              # CefSettings 镜像类
│   ├── CefProtocol.cs              # 行协议转义/切分
│   ├── CefDiagnostics.cs           # 可配置诊断日志
│   ├── build/                      # NuGet 打包 MSBuild targets
│   └── buildTransitive/
│
├── TestBrowserApp/                 # 测试演示应用 (.NET 8)
│   ├── TestBrowserApp.csproj
│   ├── Program.cs                  # 应用入口
│   ├── App.axaml / App.axaml.cs    # Avalonia Application
│   ├── MainWindow.axaml / .cs      # 主窗口 (标签 + 地址栏 + 浏览器)
│   └── app.manifest                # Windows 兼容性清单
│
├── .github/workflows/publish.yml   # GitHub Actions（构建 + 打包 + 发布 NuGet）
├── pack.ps1                        # 一键构建并打包 NuGet（原生 + 托管）
├── publish.ps1                     # 打包 TestBrowserApp 可再分发 zip
└── README.md                       # 本文件
```

---

## 构建

所有工具（CMake / MSVC）都通过 `vswhere` 或 PATH 解析，不依赖特定 VS 版本或安装路径。

### 环境要求

| 组件 | 版本 |
|------|------|
| Visual Studio | 2022（含 C++ 桌面开发 / v143） |
| CMake | 3.20+ |
| .NET SDK | 8.0+ |
| CEF Binary | 109.1.11（Chromium 109.0.5414.87） |

CEF 根目录可通过环境变量 `CEF_ROOT` 覆盖，默认 `C:/cef/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64`。

### 一键构建 + 打包 NuGet

```powershell
.\pack.ps1            # Release
.\pack.ps1 -Debug     # Debug
```

此命令依次：
1. 用 CMake + Ninja 编译 `CefBrowser.Native`（v143 工具链）；
2. 构建 `CefSharp.Avalonia` 库；
3. `dotnet pack` 生成 `artifacts\CefSharp.Avalonia.<ver>.nupkg`。

### 打包演示应用

```powershell
.\publish.ps1         # 生成 CefBrowser-Win-x64.zip（含原生宿主 + CEF 运行时 + 演示程序）
```

### 分步编译

#### 1. 下载并解压 CEF 到无空格路径

```
https://cef-builds.spotifycdn.com/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64.tar.bz2
```

#### 2. 编译原生宿主

```cmd
cd CefBrowser.Native
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release ^
  -DCEF_ROOT=C:/cef/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64 ^
  -DCMAKE_MODULE_PATH=C:/cef/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64/cmake
cmake --build build --config Release
```

#### 3. 编译 .NET

```cmd
dotnet build TestBrowserApp -c Release
dotnet run --project TestBrowserApp -c Release
```

> **本地文件加载 / 开发模式**：以 `--no-cef` 启动演示程序（`dotnet run --project TestBrowserApp -c Release -- --no-cef`）可跳过浏览器进程，仅验证 UI 布局。

---

## 配置 (CefSettings)

通过 `WebView.CefSettings` 配置 CEF 初始化参数。**必须在控件挂接到可视树之前赋值。**

```csharp
var browser = new WebView();
// 沙箱默认开启；仅在确实需要时显式关闭。
browser.CefSettings.NoSandbox = true;                 // 逃生开关，默认 false（沙箱开启）
browser.CefSettings.CommandLineSwitches.Add("--allow-file-access-from-files");
browser.CefSettings.Locale = "zh-CN";
```

所有 CEF 设置项通过 `--cef-*` 命令行参数序列化到原生进程（值含空格也安全，因为使用 `ProcessStartInfo.ArgumentList`）。

### 重要的共享宿主约束

- **进程级设置只在第一个 `WebView` 生效**：因为所有 `WebView` 共享同一个原生宿主进程，CEF 的进程级设置（UA、语言、代理、缓存根等）由首个创建宿主的实例决定。后续实例若设置不同的进程级参数，库会输出 `CefDiagnostics` 告警并忽略其差异。
- **每浏览器隔离**：用 `WebView.IsolatedCachePath` 给单个浏览器独立的 request context（独立 cookie/localStorage/cache），这是多标签并存的关键。
- **默认缓存目录**：`%LOCALAPPDATA%\CefAvalonia\cef_cache`（Linux：`~/.cache/cefavalonia/cef_cache`），不会写到程序目录（Program Files 不可写）。

---

## NuGet 包

`CefSharp.Avalonia` 已发布到 nuget.org：<https://www.nuget.org/packages/CefSharp.Avalonia>

### 安装

**Windows**（含 Windows 原生）：

```xml
<PackageReference Include="CefSharp.Avalonia" Version="1.2.0" />
```

**Linux**（额外引用 Linux 原生包）：

```xml
<PackageReference Include="CefSharp.Avalonia" Version="1.2.0" />
<PackageReference Include="CefSharp.Avalonia.Linux" Version="1.2.0" />
```

> 一个 CEF 发行包约 150–180 MB，Windows + Linux 两套塞进同一个 nupkg 会超过 NuGet.org 的 **250 MB 上限**，因此拆成两个包。跨平台发布（例如在 Windows 上执行 `dotnet publish -r linux-x64`）时，把 `CefSharp.Avalonia.Linux` 也引用上即可，SDK 会自动按 RID 选用对应原生文件。

### 目标框架要求（消费方）

```xml
<TargetFramework>net8.0</TargetFramework>
<PlatformTarget>x64</PlatformTarget>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

> 库本身只依赖 `Avalonia` 核心包（不再强制 `Avalonia.Desktop`），由消费方自行引用所需的平台后端。

---

## 部署须知（发布到目标机器）

**发布本身是零配置的**：`dotnet publish -r win-x64` 或 `dotnet publish -r linux-x64`，NuGet 包会通过 `build/CefSharp.Avalonia.targets` 自动把对应平台的 CEF 运行时 + `CefBrowser.Native` 复制到输出目录，并在 Linux 上自动 `chmod +x`。**无需任何 `-p:` 参数或额外构建步骤。**

但以下是目标机器上的**运行前提**（注意：这是部署约定，不是发布时设置）：

> ## ⚠️ 三个必须注意的坑
>
> **坑 1 —— Linux 必须预装 CEF 系统库，否则启动即崩。**
> NuGet 包和 `--self-contained` 发布都**不会**带上这些系统 `.so`（任何 Chromium 内核都一样）。缺了会在启动时直接报 `libnss3.so` / `libgtk-3.so.0` not found，或进程闪退。解法见下方 Linux 的 `apt install` 命令。
>
> **坑 2 —— `chrome-sandbox` 不设权限 = 自动回退无沙箱。**
> 不 `chmod 4755` 时 CEF 会静默回退到无沙箱模式（只打一条警告），测试可用，但生产环境安全性降低。要真沙箱必须执行 `sudo chown root:root chrome-sandbox && sudo chmod 4755 chrome-sandbox`。
>
> **坑 3 —— 只支持 x64。**
> 包内只带了 `win-x64` / `linux-x64` 的原生二进制。用 `win-x86`、`linux-arm` 等其它 RID 发布会找不到 `CefBrowser.Native`，直接启动失败。

### Windows（x64）

- .NET 8 Runtime（或用 `--self-contained` 免安装）。
- 无需额外系统依赖，CEF 的 DLL 已随包部署。

### Linux（x64）

Linux 走**离屏渲染（OSR）**，不依赖 X11 子窗口嵌入。但 CEF/Chromium 依赖以下**系统动态库**（NuGet 包无法携带，任何 Chromium 内核程序都一样）：

```bash
# Debian / Ubuntu 系（含 Zorin）
sudo apt install -y libnss3 libgtk-3-0 libgbm1 libasound2 libxshmfence1 \
  libx11-xcb1 libxcomposite1 libxdamage1 libxrandr2 libxtst6 libpango-1.0-0 \
  libcairo2 libcups2 libdbus-1-3 libfontconfig1 libgl1
```

- **glibc**：要求 ≥ 2.35（Ubuntu 22.04 / Debian 12 及以上）。原生宿主必须用与目标机一致或更低版本的 glibc 编译，否则启动报 `GLIBC_x.y not found`。
- **沙箱**：`chrome-sandbox` 未 setuid root 时 CEF 自动回退无沙箱（仅告警，可正常测试）。生产环境若要真沙箱：
  ```bash
  sudo chown root:root chrome-sandbox && sudo chmod 4755 chrome-sandbox
  ```
- **架构**：仅 x64（`win-x64` / `linux-x64`），不支持 32 位 / ARM。
- **输入**：Linux OSR 模式已支持鼠标 + 基础键盘；中文 IME、光标形状、文本选择/拖拽暂未实现。

> 若目标机器无法联网或不能 `apt install`，请用 **.deb 包**（把上面的库声明为 `Depends:`，安装时自动解析）或 **AppImage**（把依赖随镜像打包，单文件拷贝即运行）分发，**不要**把系统 `.so` 手工塞进发布目录——那样会因传递依赖与 glibc 版本耦合而不可靠。

---

## API 参考

### WebView

```csharp
var browser = new WebView();

// 事件
browser.AddressChanged += url => Console.WriteLine(url);
browser.TitleChanged  += title => this.Title = title;
browser.LoadingStateChanged += isLoading => button.IsEnabled = !isLoading;
browser.BrowserCrashed += () => Console.WriteLine("Browser crashed");
browser.LoadError += info => Console.WriteLine($"LoadError: {info}");
browser.OpenPopup += url => OpenNewTab(url);          // 含 about:blank 弹窗

// 导航
await browser.NavigateAsync("https://github.com");
await browser.ReloadAsync();
await browser.StopAsync();
browser.GoBack();
browser.GoForward();

// 属性
string currentUrl = browser.Url;       // 双向绑定可用
string title      = browser.Title;
bool isLoading    = browser.IsLoading;

// 生命周期
browser.AutoRecover = true;            // 宿主崩溃后自动恢复（默认开启，最多 3 次）
// 标签永久关闭时：
browser.Cleanup();                    // 等同 Dispose()
```

`WebView` 实现了 `IDisposable`：`Dispose()`/`Cleanup()` 只释放该浏览器槽位，共享宿主进程在应用退出时由 `CefHostProcess.ShutdownAll()`（库已在 `AppDomain.ProcessExit` 注册兜底）统一关闭。

---

## 已知限制与注意事项

- **键盘焦点**：点击页面后，焦点进入 CEF 的 HWND，Avalonia 层面的快捷键（`Ctrl+T`、`F12`、Tab 切换等）不会触发。需要使用加速键时，请在应用层通过 `WebView` 暴露的命令（`GoBack`/`ShowDeveloperTools`/缩放等）桥接，或在 Avalonia 顶层自行处理。
- **JS 对话框**：有托管宿主时，弹窗通过 `JsDialogRequested` 事件异步回调，不会阻塞共享消息泵；未订阅时自动以安全默认应答（alert 接受、其余取消），页面不会卡死。
- **诊断日志**：默认仅输出 `Error` 级。设置环境变量 `CEF_DIAG=1` 可开启全部诊断；或在代码中设置 `CefDiagnostics.Level` / `CefDiagnostics.Sink` 接入自有日志，避免污染宿主 stderr。

---

## 许可证

本项目以 [MIT 许可证](./LICENSE) 发布。
