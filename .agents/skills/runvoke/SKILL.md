---
name: runvoke
description: 在 Runvoke 桌面项目启动器中实现、验证和交付功能变更。
---

# Runvoke 工作流

在修改 Runvoke 的 Vue 前端、Tauri 配置或 Rust 后端时使用本技能。

## 工作流程

1. 阅读根目录 `AGENTS.md`、`PROJECT_OVERVIEW.md` 和相关实现代码。
2. 保持 Tauri 2、Vue 3、TypeScript、Rust 和 pnpm 的既有技术方案。
3. 进程管理相关改动必须考虑重复启动、异常退出、子进程回收、隐藏终端窗口和日志线程。
4. 根据改动范围运行对应验证命令，并记录未能执行的检查及原因。

## 验证清单

```bash
pnpm typecheck
pnpm build
cargo check --manifest-path src-tauri/Cargo.toml
```

- 前端改动至少执行前两项。
- Rust 或 Tauri 后端改动至少执行 Cargo 检查。
- 涉及进程生命周期的改动，在 Windows 上验证启动、停止、重启和子进程回收。

## 文档同步

当项目需求、范围、技术栈、架构、目录结构、运行方式或验收标准变化时，同步更新：

- `AGENTS.md`
- `PROJECT_OVERVIEW.md`
- `.agents/PROJECT_PROMPT.md`
- `.agents/skills/runvoke/SKILL.md`

仅修改不影响上述内容的实现细节时，应在交付说明中说明无需同步文档的原因。
