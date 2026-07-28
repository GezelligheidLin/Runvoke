# Runvoke

一个使用 Tauri 2、Vue 3 和 TypeScript 构建的本地桌面项目启动器。

## 功能

- 管理项目名称、目录、端口、环境变量和多个可保存任务
- 选择目录后自动从常见项目清单识别项目名称
- 支持常驻服务、一次任务和不保存的临时命令
- 隐藏终端窗口启动后台进程，并同时运行同项目的多个任务
- 按每次运行查看 stdout、stderr、系统日志、状态、PID 和退出结果
- 停止指定运行实例并清理其完整子进程树
- 本地持久化项目配置
- 系统托盘驻留与随系统启动
- 使用 VS Code 打开项目目录
- 启动后及每 30 分钟检查 GitHub Release 新版本，也可在设置中立即检查；发现更新后下载、安装并重启应用
- 项目卡片右键可快速打开该项目的编辑配置

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

## 发布更新

更新包由 GitHub Releases 提供，应用只接受使用 Tauri 更新密钥签名的 Windows NSIS 安装包。

1. 在 GitHub 仓库 Secret 中配置 `TAURI_SIGNING_PRIVATE_KEY`。
2. 同步更新 `package.json` 与 `src-tauri/tauri.conf.json` 的版本号，例如 `0.1.1`。
3. 推送对应标签 `v0.1.1`。

GitHub Actions 会自动构建安装包、生成签名和 `latest.json`，再发布到对应 Release。不要将私钥提交到仓库。
