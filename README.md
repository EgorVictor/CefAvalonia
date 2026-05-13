# CefBrowser

基于 **CEF (Chromium Embedded Framework)** 的 .NET Avalonia 浏览器控件，通过 C++ 原生子进程 + Named Pipe IPC 实现嵌入式浏览器 HWND 托管。

支持 **Windows 7** 兼容，使用 MSVC v143 工具链编译。

## 简介

CefBrowser 将 CEF 浏览器引擎封装为一个 Avalonia `UserControl`（`BrowserView`），提供完整的浏览功能（导航、加载状态、标题同步等），并暴露 C# 事件与异步 API。

**设计目标：**
- CEF 运行在独立原生进程，与 .NET 进程解耦
- 通过 HWND 嵌入（`SetParent`）实现浏览器与 Avalonia 控件无缝融合
- Named Pipe 进程间通信，命令与事件高效传递

---

## 架构

```
┌──────────────────────────────────────────────────┐
│              Avalonia App (.NET 8)               │
│  ┌─────────────────────────────────────────────┐  │
│  │              BrowserView                     │  │
│  │  ┌─────────────────────────────────────────┐ │  │
│  │  │   ExternalBrowserProcessHost            │ │  │
│  │  │   (NativeControlHost, HWND embedding)    │ │  │
│  │  └─────────────────────────────────────────┘ │  │
│  │  ┌─────────────────────────────────────────┐ │  │
│  │  │   BrowserProcessManager                 │ │  │
│  │  │   (子进程生命周期 + Named Pipe IPC)      │ │  │
│  │  └─────────────────────────────────────────┘ │  │
│  └─────────────────────────────────────────────┘  │
│                         │                          │
│                   Named Pipe                       │
│                   (Windows)                        │
└────────────────────┬─────────────────────────────┘
                     │
┌────────────────────┴─────────────────────────────┐
│          CefBrowser.Native (C++, CEF)             │
│  ┌─────────────────────────────────────────────┐  │
│  │              PipeServer                      │  │
│  │  (Named Pipe 服务端, 双线程: 读取+写入)      │  │
│  └─────────────────────────────────────────────┘  │
│  ┌─────────────────────────────────────────────┐  │
│  │              BrowserHandler                  │  │
│  │  (CEF Client / LifeSpan / Load / Display)     │  │
│  └─────────────────────────────────────────────┘  │
│  ┌─────────────────────────────────────────────┐  │
│  │           CEF Browser Instance               │  │
│  │  (Chromium 109, 独立进程)                    │  │
│  └─────────────────────────────────────────────┘  │
│                        │                           │
│                  SetParent(browser_hwnd)           │
└──────────────────────────────────────────────────┘
```

### 通信协议

| 方向 | 命令/事件 | 格式 |
|------|-----------|------|
| C# → C++ | `Navigate` | `Navigate\|url` |
| C# → C++ | `Reload` | `Reload` |
| C# → C++ | `Stop` | `Stop` |
| C# → C++ | `Close` | `Close` |
| C# → C++ | `EmbedDone` | `EmbedDone` |
| C# → C++ | `Resize` | `Resize\|width\|height` |
| C++ → C# | `Ready` | `Ready\|HWND_HEX` |
| C++ → C# | `AddressChanged` | `AddressChanged\|url` |
| C++ → C# | `LoadError` | `LoadError\|code\|text\|url` |
| C++ → C# | `NavState` | `NavState\|isLoading\|canGoBack\|canGoForward` |
| C++ → C# | `TitleChanged` | `TitleChanged\|title` |

---

## 项目结构

```
CefBrowser/
├── CefBrowser.Native/              # C++ CEF 原生子进程 (CMake 项目)
│   ├── CMakeLists.txt              # CMake 构建配置
│   └── src/
│       ├── main.cpp                # WinMain、CEF 初始化、消息泵、命令调度
│       ├── browser_handler.h/.cpp  # CEF Client 回调处理
│       └── pipe_server.h/.cpp      # Named Pipe 服务端 (双线程读写)
│
├── CefSharp.Avalonia/              # C# Avalonia 浏览器控件库 (.NET 8)
│   ├── CefSharp.Avalonia.csproj
│   ├── BrowserView.cs              # 可复用的 Avalonia UserControl
│   ├── BrowserProcessManager.cs    # 子进程管理 + Named Pipe 客户端 + 消息调度
│   ├── ExternalBrowserProcessHost.cs # HWND 嵌入 (NativeControlHost)
│   ├── CefSettings.cs              # CefSettings 镜像类
│   ├── build/                      # NuGet 打包 MSBuild targets
│   └── buildTransitive/
│
├── TestBrowserApp/                 # 测试演示应用 (.NET 8)
│   ├── TestBrowserApp.csproj
│   ├── Program.cs                  # 应用入口
│   ├── App.axaml / App.axaml.cs    # Avalonia Application
│   ├── MainWindow.axaml / .cs      # 主窗口 (地址栏 + 浏览器)
│   └── app.manifest                # Windows 兼容性清单
│
├── .github/workflows/publish.yml   # GitHub Actions 自动发布
├── package.ps1                     # 一键编译打包脚本
├── pack.ps1                        # 打包 NuGet 脚本
├── run.ps1                         # 启动 TestBrowserApp
├── run-standalone.ps1              # 启动 C++ 独立模式
├── run-dev.ps1                     # 启动 UI 测试模式
└── README.md                       # 本文件
```

---

## 启动方式

项目支持 3 种启动模式：

### 1. 完整应用模式（推荐）

运行 `TestBrowserApp`（.NET + CEF）：

```powershell
.\run.ps1
```

### 2. C++ 独立模式（调试 CEF）

直接运行 CEF 原生进程，不依赖 .NET：

```powershell
# 默认 about:blank
.\run-standalone.ps1

# 指定 URL
.\run-standalone.ps1 -Url "https://www.google.com"

# 加载本地文件
.\run-standalone.ps1 -Url "file:///F:/Test/test.html"
```

此模式用于快速验证 CEF 本身的运行状态。窗口标题为 "CEF Browser Test"。

### 3. 开发模式（无 CEF）

仅测试 UI 布局，跳过浏览器进程：

```powershell
.\run-dev.ps1
```

等价于 `TestBrowserApp.exe --no-cef`。

---

## 本地文件加载

CefBrowser 支持加载本地 HTML 文件。输入本地路径即可：

```
# 在地址栏输入（自动补全为 file:///）
F:\Test\test.html

# 或直接使用 file:// 协议
file:///F:/Test/test.html
```

底层通过 `--allow-file-access-from-files` 和 `--disable-web-security` 两个 CEF 开关实现。此方式用于**本地开发测试**，生产环境建议使用 HTTP 服务器。

> 注：`--disable-web-security` 会关闭同源策略，请勿用于加载不受信任的内容。

---

## 编译

### 环境要求

| 组件 | 版本 |
|------|------|
| Visual Studio | 2022 / 2026（含 C++ 桌面开发） |
| CMake | 3.20+ |
| .NET SDK | 8.0+ |
| CEF Binary | 109.1.11（Chromium 109.0.5414.87） |

### 一键编译

```powershell
.\package.ps1            # Release
.\package.ps1 -Debug     # Debug
```

此命令依次：
1. 编译 `CefBrowser.Native`（CMake + Ninja，v143 工具链）
2. 编译 .NET 项目
3. 打包为 `CefBrowser-Win7-x64.zip`

### 分步编译

#### 1. 下载 CEF

```
https://cef-builds.spotifycdn.com/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64.tar.bz2
```

解压到无空格路径，如 `C:\cef\cef_binary_109.1.11+...`

#### 2. 编译原生子进程

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
.\TestBrowserApp\bin\Release\net8.0-windows\TestBrowserApp.exe
```

---

## 配置 (CefSettings)

通过 `BrowserView.CefSettings` 属性配置 CEF 初始化参数：

```csharp
var browser = new BrowserView();
browser.CefSettings.NoSandbox = true;
browser.CefSettings.CommandLineSwitches.Add("--allow-file-access-from-files");
browser.CefSettings.Locale = "zh-CN";
```

所有 CEF 设置项通过 `--cef-*` 命令行参数序列化到原生进程。

---

## NuGet 包

`CefSharp.Avalonia` 已发布到 nuget.org：<https://www.nuget.org/packages/CefSharp.Avalonia>

### 安装

```xml
<PackageReference Include="CefSharp.Avalonia" Version="1.0.0" />
```

### 目标框架要求

```xml
<TargetFramework>net8.0-windows</TargetFramework>
<PlatformTarget>x64</PlatformTarget>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<UseWindowsForms>true</UseWindowsForms>
```

### 本地打包

```powershell
.\pack.ps1
```

输出：`artifacts\CefSharp.Avalonia.1.0.0.nupkg`

### 自动发布

推 tag 到 GitHub 自动触发 GitHub Actions：

```cmd
git tag v1.0.1
git push --tags
```

---

## API 参考

### BrowserView

```csharp
var browser = new BrowserView();

// 事件
browser.AddressChanged += url => Console.WriteLine(url);
browser.TitleChanged += title => this.Title = title;
browser.LoadingStateChanged += isLoading => button.IsEnabled = !isLoading;
browser.BrowserCrashed += () => Console.WriteLine("Browser crashed");
browser.LoadError += info => Console.WriteLine($"LoadError: {info}");

// 导航
await browser.NavigateAsync("https://github.com");
await browser.ReloadAsync();
await browser.StopAsync();

// 属性
browser.CefSettings.NoSandbox = false;
string currentUrl = browser.Url;
string title = browser.Title;
bool isLoading = browser.IsLoading;
```
