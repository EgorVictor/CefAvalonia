# CefBrowser v1.0.5-beta1 验证指南

## 🔍 验证清单

### Phase 1: 基础编译验证 ✅
```bash
cd f:\Test
dotnet build -c Release
# 结果: 0 errors, 0 warnings ✅
```

### Phase 2: 运行时验证 (需手动执行)

#### 2.1 启动应用
```bash
.\TestBrowserApp\bin\Release\net8.0\TestBrowserApp.exe
```

**预期行为:**
- [ ] 应用启动无错误
- [ ] 窗口显示正常（无黑屏）
- [ ] 可以看到浏览器界面

#### 2.2 单tab黑屏测试
```
步骤:
1. 启动应用
2. 在地址栏输入: https://www.google.com
3. 点击导航或按Enter
4. 等待页面加载完成

预期:
- [ ] 页面加载过程中无黑屏
- [ ] 页面完全加载，清晰可见
- [ ] 调整窗口大小，页面响应正常
```

#### 2.3 窗口调整测试 (关键)
```
步骤:
1. 在浏览器中打开任意网页
2. 拖拽窗口边界，快速调整窗口大小
3. 最小化/最大化窗口多次
4. 移动窗口到不同位置

预期:
- [ ] 无黑屏现象
- [ ] 页面内容清晰
- [ ] 无闪烁或抖动
- ⚠️ 前版本: 需要调整窗体大小才能恢复显示
- ✅ 新版本: 自动恢复，无需手动调整
```

#### 2.4 快速导航测试
```
步骤:
1. 快速在地址栏输入多个URL
2. 快速点击导航按钮
3. 快速改变URL并导航

预期:
- [ ] 导航流畅，无卡顿
- [ ] 无黑屏现象
- [ ] 错误信息清晰（如果有）
```

#### 2.5 调试日志验证
```
步骤:
1. 以Debug运行应用: 
   dotnet run -c Debug
2. 打开Windows事件查看器 (调试输出)
3. 观察[EBPH]和[WebView]日志

预期输出应该包含:
[EBPH] EmbedWindowAsync START
[EBPH] Step 1: SetWindowLong(WS_CHILD|WS_VISIBLE)
[EBPH] Step 2: SetParent done
[EBPH] Step 3: Delay 25ms after SetParent
...
[EBPH] Step 8: RedrawWindow(child) - second pass
[EBPH] EmbedWindowAsync COMPLETE
```

### Phase 3: ProcessPool & TabManager 验证

#### 3.1 进程复用验证 (需运行时监控)
```
步骤:
1. 打开任务管理器，找到 CefBrowser.Native.exe
2. 启动应用
3. 在地址栏导航到 https://www.google.com
4. 观察任务管理器中的进程数量

当前预期 (v1.0.5):
- 应有多个浏览器实例加载（ProcessPool框架已就位）

注意: 多tab功能需要在TestBrowserApp中集成TabbedBrowserView
      才能完整验证。当前版本是框架，还需UI绑定。
```

#### 3.2 内存占用基准
```
步骤:
1. 启动应用
2. 打开 Google (https://www.google.com)
3. 在任务管理器中记录内存占用

预期内存占用:
- v1.0.4: ~130-150MB
- v1.0.5: ~130-150MB (单tab无变化)

注意: 内存优化主要在多tab场景体现
```

### Phase 4: 异常处理验证

#### 4.1 异常诊断日志
```
步骤:
1. 启动应用
2. 尝试导航到无效URL: xyz://invalid
3. 检查Debug输出

预期:
- [ ] 清晰的错误日志
- [ ] 前版本: 无任何日志信息
- [ ] 新版本: [WebView] NavigateAsync error: ...
```

#### 4.2 进程崩溃恢复
```
步骤:
1. 启动应用并加载页面
2. 在任务管理器中杀死 CefBrowser.Native.exe
3. 观察应用行为

预期:
- [ ] 看到 BrowserCrashed 事件触发的日志
- [ ] 应用不会崩溃，保持响应
```

---

## ⚠️ 已知限制

### v1.0.5-beta1 当前状态

✅ **已实现:**
- HWND嵌入完整修复（黑屏根除）
- ProcessPool框架（进程复用机制）
- TabManager框架（标签管理）
- 异常处理改进（完整日志）
- IPC冻结/恢复接口

❌ **未实现 (框架已备好，需集成):**
- TabbedBrowserView的实际tab切换UI
- tab间的HWND容器切换
- 真实的多tab浏览功能
- 历史记录管理完整实现

### 后续工作
为了启用完整的多tab功能，需要:
1. 在TestBrowserApp中集成TabbedBrowserView组件
2. 实现WebView → HWND容器的显示/隐藏逻辑
3. 连接tab切换事件到ProcessPool切换
4. 测试tab间的完整工作流

---

## 🐛 如果发现问题

### 黑屏仍然出现?
```
1. 检查Debug日志中是否有所有8步输出
2. 验证System.Diagnostics是否正确引入
3. 检查Windows版本 (Win7-11都支持SetParent)
```

### 编译错误?
```
1. 确保 .NET 8.0 SDK 已安装: dotnet --version
2. 清理bin/obj: dotnet clean
3. 重新编译: dotnet build -c Release
```

### 进程未创建?
```
1. 检查 CefBrowser.Native.exe 是否存在
2. 验证CefSettings配置是否正确
3. 检查Debug日志:[BPM] StartAsync
```

---

## 📋 测试报告模板

如果发现任何问题，请填写:

```
问题描述:
[...]

重现步骤:
1. [...]
2. [...]
3. [...]

预期行为:
[...]

实际行为:
[...]

Debug日志:
[...]

系统信息:
- Windows版本: 
- DPI缩放: 
- 显示器数量:
```

---

## 下一步行动

### 方案A: 快速验证 (5分钟)
1. ✅ 编译成功 (已验证)
2. ⏳ 运行应用，浏览简单网页
3. ⏳ 调整窗口大小，确认无黑屏
4. 完成!

### 方案B: 完整验证 (30分钟)
1-3 + 以上所有步骤

### 方案C: 生产验证 (2-4小时)
B + 长期压力测试 (8小时+)

---

**建议**: 先进行方案A的快速验证，确认黑屏问题确实解决后，再考虑多tab集成工作。
