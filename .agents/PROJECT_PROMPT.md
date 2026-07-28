# Runvoke 项目提示词

## 项目背景

Runvoke 是一个本地桌面项目启动器，解决多个开发项目需要分别打开终端并手工追踪其运行状态的问题。用户为项目配置工作目录、启动命令、端口和环境变量后，可在应用内管理完整进程生命周期并实时查看日志。

## 当前产品要求

1. 支持新增、编辑、删除及本地持久化项目配置。
2. 后台启动命令且不弹出终端窗口。
3. 展示启动中、运行中、已停止和异常退出等状态。
4. 停止项目时销毁主进程及完整子进程树。
5. 实时采集并展示 stdout、stderr 和系统日志。
6. 保持系统托盘驻留、开机启动及 VS Code 打开项目目录能力。

## 技术约束

- 使用 Tauri 2、Vue 3、TypeScript 和 Rust；包管理器固定为 pnpm。
- Vue 使用 Composition API 与 `<script setup lang="ts">`。
- 进程管理、文件访问和持久化应放在 Rust 侧；前端通过类型明确的 Tauri command 或 event 通信。
- 默认面向 Windows，但避免无必要的平台绑定。
- 进程管理改动优先保证子进程回收、隐藏窗口和日志线程可靠性；不得在日志或配置中暴露敏感信息。

## AI 工作规则

- 先阅读根目录 `AGENTS.md`，保持现有技术栈、架构和编码约定。
- 前端改动至少运行 `pnpm typecheck` 与 `pnpm build`；Rust 改动至少运行 `cargo check --manifest-path src-tauri/Cargo.toml`。
- 项目需求、范围、技术栈、目录、运行方式或验收标准变化时，同步更新 `AGENTS.md`、`PROJECT_OVERVIEW.md` 和 `.agents/skills/runvoke/SKILL.md`。
