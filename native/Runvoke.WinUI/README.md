# Runvoke.WinUI

这是 Runvoke 的 Windows 原生重写入口，目标平台为 **WinUI 3 + C#/.NET 8 + Windows App SDK**。

原生入口已接入共享 `projects.json` 配置模型和完整的 Windows 运行时边界：项目与任务编辑（支持手工路径和 manifest 名称/命令识别）、项目搜索、常驻/一次任务、临时命令、并发运行与运行实例隔离、停止全部和 Job Object 子进程树回收、隐藏 PowerShell、stdout/stderr 捕获、终端进度解析、敏感值脱敏、有界日志、日志筛选与清理、日志内联 HTTP 链接、自动滚动、按项目筛选运行实例、分组与排序、可键盘调整的侧栏、项目拖入、VS Code/Cursor 导入、Git 分支与状态复制、GitHub 入口、可配置软件源、主题/设置分类导航/开机启动、单实例与系统托盘、MCP Bearer 服务配置、稳定/预览更新清单、通知窗口和后台完成提示均已实现。首次启动会优先读取旧 Tauri 标识目录中的 `projects.json` 并迁移到原生配置目录。更新清单同时要求 SHA-256 摘要与签名字段；MSIX 包还会检查 `AppxSignature.p7x`。现有 Tauri 应用仍作为迁移期间的可用基线；最终打包验收需要安装 Visual Studio 的 Windows App SDK/MSIX 工具链。

## 本地构建

需要安装：

- Visual Studio 2022 17.10 或更高版本；
- .NET 8 SDK；
- Windows App SDK C# 工具和 Windows 11 SDK；
- x64 或 ARM64 Windows 环境。

构建命令：

```powershell
dotnet restore native/Runvoke.WinUI/Runvoke.WinUI.csproj
dotnet build native/Runvoke.WinUI/Runvoke.WinUI.csproj -c Debug -p:Platform=x64
```

完整 WinUI 编译还需要 Visual Studio 的 Windows App SDK/MSIX 工具链；仅有 .NET SDK 时，XAML 编译器或 `Microsoft.Build.AppxPackage.dll` 可能缺失。核心 C# 代码可用同一 SDK 独立编译检查，最终验收仍应在安装了上述工具链的 Windows 环境完成。
