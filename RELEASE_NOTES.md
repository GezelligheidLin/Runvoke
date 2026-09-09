# Runvoke v0.1.19-dev.1

预览版本：可能包含未完成或不稳定的功能，建议仅用于体验和测试。

## 问题修复

- 修复 Windows 未安装 PowerShell 7 或 PATH 缺少其入口时，任务提示“program not found”而无法启动的问题；找不到 PowerShell 7 时自动使用系统 Windows PowerShell。
- 支持从当前用户的 Microsoft Store 执行入口和系统标准安装目录查找 PowerShell 7，不依赖个人路径或具体安装版本。

## 体验优化

- 统一 PowerShell 任务的 UTF-8 输出，改善中文日志兼容性。
- 调整设置页软件源列表与编辑表单的自适应布局，减少窄窗口下的信息和操作按钮拥挤。
