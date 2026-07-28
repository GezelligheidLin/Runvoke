# Runvoke

一个使用 Tauri 2、Vue 3 和 TypeScript 构建的本地桌面项目启动器。

## 功能

- 管理项目名称、目录、启动命令、端口和环境变量
- 隐藏终端窗口启动后台进程
- 启动、停止、重启并清理完整子进程树
- 实时查看 stdout、stderr 和系统日志
- 查看运行状态、PID、端口和运行时长
- 本地持久化项目配置
- 系统托盘驻留与随系统启动
- 使用 VS Code 打开项目目录

## 环境要求

- Node.js 20+
- pnpm 10+
- Rust stable
- Tauri 2 所需的 Windows 开发环境

## 开发

```bash
pnpm install
pnpm tauri dev
```

关闭主窗口后应用会继续驻留系统托盘。通过托盘菜单可重新显示窗口，或退出并停止所有项目。

## 检查与构建

```bash
pnpm typecheck
pnpm build
cargo check --manifest-path src-tauri/Cargo.toml
```
