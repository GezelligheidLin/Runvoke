# Runvoke 协作规范

## 项目目标

本项目是一个基于 Tauri 2、Vue 3 和 TypeScript 的桌面项目启动器，用于集中配置、启动、停止和查看本地开发项目的运行日志。

## 技术约束

- 包管理器使用 pnpm。
- 前端使用 Vue 3 Composition API 与 `<script setup lang="ts">`。
- 桌面端能力通过 Tauri 2 和 Rust 实现。
- 不随意更换现有技术栈、目录结构或状态管理方案。
- 进程启动必须隐藏终端窗口；停止时应清理完整子进程树。
- 不在日志或配置中泄露密钥、令牌等敏感信息。

## 编码规范

- 优先使用清晰的小组件、组合式函数和显式 TypeScript 类型。
- 界面文案默认使用简体中文。
- 新增功能应覆盖异常状态、重复启动和进程意外退出等边界情况。
- 修改 Rust 进程管理逻辑后，应验证 Windows 下的隐藏窗口和进程回收行为。

## 验证要求

- 前端改动至少执行 `pnpm typecheck` 和 `pnpm build`。
- Rust 改动至少执行 `cargo check --manifest-path src-tauri/Cargo.toml`。
- 提交前说明已完成的验证以及尚未验证的内容。

## 文档同步

- 修改项目目标、功能范围、技术栈、目录结构、运行方式或验收标准时，必须同步更新 `PROJECT_OVERVIEW.md`、`.agents/PROJECT_PROMPT.md` 与 `.agents/skills/runvoke/SKILL.md`。
- 仅修改实现细节且不影响上述内容时，无需更新项目文档，但应在交付说明中注明原因。
