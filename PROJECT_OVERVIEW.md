# Runvoke 项目概览

## 项目目标

Runvoke 是面向本地开发者的桌面项目启动器。它将多个项目的工作目录、启动命令、端口和环境变量集中管理，使用户能在一个应用中启动、停止、重启项目并查看运行日志。

## 当前范围

- 管理项目配置，并将配置持久化在本机。
- 在后台启动项目进程，避免显示终端窗口。
- 查看运行状态、PID、端口、运行时长、stdout、stderr 与系统日志。
- 停止或重启项目时清理完整子进程树。
- 提供系统托盘驻留、开机启动与用 VS Code 打开项目目录的能力。

不在当前范围内：远程项目管理、团队协作、云端同步或代管项目的构建与部署。

## 技术栈

- 桌面框架：Tauri 2
- 前端：Vue 3、TypeScript、Vite
- 系统与进程管理：Rust
- 包管理器：pnpm 10

## 目录说明

```text
src/                 Vue 前端界面、类型和组合式函数
src-tauri/           Tauri 与 Rust 进程管理实现
.agents/             项目提示词和 AI 协作技能
AGENTS.md            项目协作与验证约束
README.md            面向使用者的安装与运行说明
```

## 运行与验证

环境要求：Node.js 20+、pnpm 10+、Rust stable，以及 Tauri 2 在 Windows 上要求的开发环境。

```bash
pnpm install
pnpm tauri dev
pnpm typecheck
pnpm build
cargo check --manifest-path src-tauri/Cargo.toml
```

## 验收标准

- 可新增、编辑、删除并持久化本地项目配置。
- 配置的命令能够在后台启动，运行状态和日志可在界面中查看。
- 停止和重启操作能够回收主进程及其子进程。
- 前端类型检查和生产构建通过；修改 Rust 代码时 Cargo 检查通过。

## 变更记录

- 2026-07-28：接入 AIWorkspace，补齐项目概览、标准提示词路径和项目协作技能；未修改应用功能。
