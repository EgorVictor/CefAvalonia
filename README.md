# CefBrowser

基于 **CEF (Chromium Embedded Framework)** 的 .NET Avalonia 浏览器控件，通过 C++ 原生子进程 + Named Pipe IPC 实现嵌入式浏览器 HWND 托管。

## 简介

CefBrowser 将 CEF 浏览器引擎封装为一个 Avalonia `UserControl`（`BrowserView`），提供完整的浏览功能（导航、前进、后退、加载状态、标题同步等），并暴露 C# 事件与异步 API。

**设计目标：**

- 将 CEF 运行在独立的原生进程中，与 .NET 进程解耦
- 通过 HWND 嵌入（`SetParent`）实现浏览器窗口与 Avalonia 控件的无缝融合
- 使用 Named Pipe 进行进程间通信，确保命令与事件的高效传递

---

## 架构

```
┌──────────────────────────────────────────────────┐
│              Avalonia App (.NET 8)               │
│                                                   │
│  ┌─────────────────────────────────────────────┐  │
│  │              BrowserView                     │  │
│  │  (Avalonia UserControl)                      │  │
│  │                                              │  │
│  │  ┌─────────────────────────────────────────┐ │  │
│  │  │   ExternalBrowserProcessHost            │ │  │
│  │  │   (NativeControlHost, HWND embedding)    │ │  │
│  │  └─────────────────────────────────────────┘ │  │
│  │                                              │  │
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
│                                                   │
│  ┌─────────────────────────────────────────────┐  │
│  │              PipeServer                      │  │
│  │  (Named Pipe 服务端, 双线程: 读取+写入)      │  │
│  └─────────────────────────────────────────────┘  │
│                                                   │
│  ┌─────────────────────────────────────────────┐  │
│  │              BrowserHandler                  │  │
│  │  (CEF Client / LifeSpan / Load / Display)     │  │
│  └─────────────────────────────────────────────┘  │
│                                                   │
│  ┌─────────────────────────────────────────────┐  │
│  │           CEF Browser Instance               │  │
│  │  (Chromium 109, 独立进程)                    │  │
│  └─────────────────────────────────────────────┘  │
│                        │                           │
│                  SetParent(browser_hwnd)           │
│                        │                           │
│                   ┌────┴────┐                     │
│                   │  HWND   │ ←── Avalonia 面板    │
│                   └─────────┘                     │
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
│   ├── BUILD.md                    # 原生构建说明
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
├── package.ps1                     # 一键编译脚本
├── pack.ps1                        # 打包 NuGet 脚本
└── README.md                       # 本文件
```

---

## 编译与运行

### 环境要求

| 组件 | 版本 |
|------|------|
| Visual Studio | 2022 / 2026（含 C++ 桌面开发工作负载） |
| CMake | 3.20+（VS 自带） |
| .NET SDK | 8.0+ |
| CEF Binary | 109.1.11（匹配 Chromium 109.0.5414.87） |

### 一键编译

```powershell
.\package.ps1            # Release (默认)
.\package.ps1 -Debug     # Debug
```

此命令依次：
1. 编译 `CefBrowser.Native`（CMake + Ninja）
2. 编译 .NET 项目
3. 打包为 `CefBrowser-Win7-x64.zip`

### 分步编译

#### 1. 下载 CEF 二进制分发包

```cmd
# 下载地址（~200MB）：
# https://cef-builds.spotifycdn.com/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64.tar.bz2

# 解压到无空格的路径，例如：
C:\cef\cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64\
```

#### 2. 编译原生子进程

```cmd
cd CefBrowser.Native

cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release ^
  -DCEF_ROOT=C:/cef/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64 ^
  -DCMAKE_MODULE_PATH=C:/cef/cef_binary_109.1.11+g6d4fdb2+chromium-109.0.5414.87_windows64/cmake

cmake --build build --config Release
```

#### 3. 编译 .NET + 运行

```cmd
dotnet build TestBrowserApp -c Release
.\TestBrowserApp\bin\Release\net8.0-windows\TestBrowserApp.exe
```

---

## 测试应用 (TestBrowserApp)

`TestBrowserApp` 是一个演示如何使用 `BrowserView` 控件的完整 Avalonia 桌面应用。

### 功能

- 地址栏导航（支持 Enter 和 Go 按钮）
- 刷新页面按钮
- 浏览器标题同步到窗口标题
- 加载状态指示（Go 按钮在加载时禁用）
- 浏览器崩溃检测与提示

### 使用方式

```cmd
# 正常启动
TestBrowserApp.exe

# 跳过浏览器（仅测试 UI）
TestBrowserApp.exe --no-cef
```

### 代码结构

- `MainWindow.xaml` — XAML 布局（地址栏、按钮、BrowserContainer 面板）
- `MainWindow.xaml.cs` — 事件绑定（导航、刷新、状态同步）
- `BrowserView` 控件以代码方式创建并添加到 `BrowserContainer`

```csharp
var browser = new BrowserView();
browser.AddressChanged += url => Console.WriteLine($"Navigated to: {url}");
browser.TitleChanged += title => this.Title = title;
container.Children.Add(browser);

// 导航
await browser.NavigateAsync("https://github.com");
await browser.ReloadAsync();
await browser.StopAsync();
```

---

## NuGet 包

`CefSharp.Avalonia` 已发布到 nuget.org：

```
https://www.nuget.org/packages/CefSharp.Avalonia
```

### 安装

```xml
<PackageReference Include="CefSharp.Avalonia" Version="1.0.0" />
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

工作流：下载 CEF → 构建原生 → dotnet pack → 推送到 nuget.org

---

## 集成到自有项目

### 1. 安装 NuGet 包

```xml
<PackageReference Include="CefSharp.Avalonia" Version="1.0.0" />
```

### 2. 配置目标框架

```xml
<TargetFramework>net8.0-windows</TargetFramework>
<PlatformTarget>x64</PlatformTarget>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<UseWindowsForms>true</UseWindowsForms>
```

### 3. XAML 中使用

```xml
<Window xmlns:cef="clr-namespace:CefSharp.Avalonia;assembly=CefSharp.Avalonia">
  <cef:BrowserView />
</Window>
```

### 4. CefSettings 配置

可在代码中设置 CEF 初始化参数：

```csharp
var browser = new BrowserView();
browser.CefSettings.NoSandbox = true;
browser.CefSettings.Locale = "zh-CN";
```

所有 28 个 CEF 设置项通过 `--cef-*` 命令行参数序列化到原生进程。
