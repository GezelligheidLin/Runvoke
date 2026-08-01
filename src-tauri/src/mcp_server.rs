use std::{collections::HashSet, net::Ipv4Addr, path::Path, sync::Arc};

use axum::{
    extract::{Request, State},
    http::StatusCode,
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::post,
    Json, Router,
};
use serde_json::{json, Value};
use tauri::{AppHandle, Emitter, Manager};
use tokio_util::sync::CancellationToken;

use super::{
    load_store, run_task, run_temporary_command, save_project, save_project_group, stop_run,
    AppState, LogEvent, ProjectConfig, ProjectGroup, ProjectTask, RuntimeStatus,
};

#[derive(Clone)]
struct McpHttpState {
    app: AppHandle,
    token: Arc<str>,
}

pub(crate) struct McpServerRuntime {
    cancellation: CancellationToken,
}

impl McpServerRuntime {
    pub(crate) fn stop(self) {
        self.cancellation.cancel();
    }
}

pub(crate) async fn start(
    app: AppHandle,
    port: u16,
    token: String,
) -> Result<McpServerRuntime, String> {
    let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, port))
        .await
        .map_err(|error| format!("无法启动本地 MCP 服务（端口 {port}）：{error}"))?;
    let cancellation = CancellationToken::new();
    let shutdown = cancellation.clone();
    let state = McpHttpState {
        app,
        token: Arc::from(token),
    };
    let router = Router::new()
        .route("/mcp", post(handle_request))
        .layer(middleware::from_fn_with_state(state.clone(), authorize))
        .with_state(state);

    tokio::spawn(async move {
        let _ = axum::serve(listener, router)
            .with_graceful_shutdown(async move { shutdown.cancelled().await })
            .await;
    });
    Ok(McpServerRuntime { cancellation })
}

async fn authorize(State(state): State<McpHttpState>, request: Request, next: Next) -> Response {
    let expected = format!("Bearer {}", state.token);
    let authorized = request
        .headers()
        .get("authorization")
        .and_then(|value| value.to_str().ok())
        .is_some_and(|value| value == expected);
    if authorized {
        next.run(request).await
    } else {
        (
            StatusCode::UNAUTHORIZED,
            [("www-authenticate", "Bearer")],
            "本地 MCP 服务需要 Bearer 令牌",
        )
            .into_response()
    }
}

async fn handle_request(State(state): State<McpHttpState>, Json(request): Json<Value>) -> Response {
    let id = request.get("id").cloned().unwrap_or(Value::Null);
    let method = request
        .get("method")
        .and_then(Value::as_str)
        .unwrap_or_default();
    let params = request.get("params").cloned().unwrap_or_else(|| json!({}));

    if method == "notifications/initialized" || method.starts_with("notifications/") {
        return StatusCode::ACCEPTED.into_response();
    }

    let result: Result<Value, String> = match method {
        "initialize" => Ok(json!({
            "protocolVersion": "2025-03-26",
            "capabilities": { "tools": { "listChanged": false } },
            "serverInfo": { "name": "runvoke", "version": env!("CARGO_PKG_VERSION") },
            "instructions": "Runvoke 仅提供本地项目工作台操作。不存在删除工具；导入请求会在应用内打开由用户确认的清单。"
        })),
        "ping" => Ok(json!({})),
        "tools/list" => Ok(json!({ "tools": tool_definitions() })),
        "tools/call" => match params.get("name").and_then(Value::as_str) {
            Some(name) => Ok(tool_response(
                dispatch_tool(
                    &state,
                    name,
                    params
                        .get("arguments")
                        .cloned()
                        .unwrap_or_else(|| json!({})),
                )
                .await,
            )),
            None => Err("tools/call 缺少工具名称".into()),
        },
        _ => return json_error(id, -32601, "未支持的 MCP 方法"),
    };

    match result {
        Ok(value) => json_result(id, value),
        Err(error) => json_error(id, -32602, error),
    }
}

fn json_result(id: Value, result: Value) -> Response {
    Json(json!({ "jsonrpc": "2.0", "id": id, "result": result })).into_response()
}

fn json_error(id: Value, code: i32, message: impl Into<String>) -> Response {
    Json(
        json!({ "jsonrpc": "2.0", "id": id, "error": { "code": code, "message": message.into() } }),
    )
    .into_response()
}

fn tool_response(result: Result<Value, String>) -> Value {
    match result {
        Ok(value) => json!({ "content": [{ "type": "text", "text": value.to_string() }] }),
        Err(error) => json!({ "content": [{ "type": "text", "text": error }], "isError": true }),
    }
}

fn tool_definitions() -> Vec<Value> {
    vec![
        tool("list_projects", "列出已接入项目。环境变量值不会返回。", json!({"type":"object","properties":{}})),
        tool("list_groups", "列出项目分组。", json!({"type":"object","properties":{}})),
        tool("list_runs", "列出项目运行实例和状态。", json!({"type":"object","properties":{}})),
        tool("get_logs", "读取指定运行实例的近期日志。", json!({"type":"object","properties":{"runId":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":500}},"required":["runId"]})),
        tool("save_project", "编辑已有项目。不会创建或删除项目，编辑时会保留已有环境变量。", json!({"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"directory":{"type":"string"},"groupId":{"type":["string","null"]},"port":{"type":["integer","null"]},"tasks":{"type":"array"}},"required":["id","name","directory"]})),
        tool("save_group", "新建或编辑项目分组。", json!({"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"collapsed":{"type":"boolean"}},"required":["name"]})),
        tool("move_project", "将项目移动到分组中的指定位置。", json!({"type":"object","properties":{"projectId":{"type":"string"},"groupId":{"type":["string","null"]},"targetIndex":{"type":"integer","minimum":0}},"required":["projectId","targetIndex"]})),
        tool("start_project_task", "启动项目中已配置的任务。", json!({"type":"object","properties":{"projectId":{"type":"string"},"taskId":{"type":"string"}},"required":["projectId","taskId"]})),
        tool("run_project_command", "在指定项目目录执行一次性临时命令。", json!({"type":"object","properties":{"projectId":{"type":"string"},"command":{"type":"string"}},"required":["projectId","command"]})),
        tool("stop_run", "停止一个运行实例及其子进程树。", json!({"type":"object","properties":{"runId":{"type":"string"}},"required":["runId"]})),
        tool("stop_all_runs", "停止全部活动运行实例及其子进程树。", json!({"type":"object","properties":{}})),
        tool("request_project_import", "请求在 Runvoke 中打开独立的 Agent 项目纳入清单。传入候选项目后，必须由用户勾选并确认，AI 不能直接导入。", json!({"type":"object","properties":{"projects":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object","properties":{"name":{"type":"string"},"directory":{"type":"string"},"suggestedCommand":{"type":["string","null"]}},"required":["name","directory"]}}},"required":["projects"]})),
        tool("update_settings", "修改应用设置。", json!({"type":"object","properties":{"theme":{"type":"string","enum":["light","dark"]},"logLinkAction":{"type":"string","enum":["open","copy"]},"githubLinkVisible":{"type":"boolean"},"autostartEnabled":{"type":"boolean"}}})),
        tool("check_updates", "请求应用检查更新。", json!({"type":"object","properties":{}})),
        tool("request_install_update", "请求在 Runvoke 窗口中确认并安装已发现的更新。", json!({"type":"object","properties":{}})),
    ]
}

fn tool(name: &str, description: &str, input_schema: Value) -> Value {
    json!({ "name": name, "description": description, "inputSchema": input_schema })
}

async fn dispatch_tool(state: &McpHttpState, name: &str, args: Value) -> Result<Value, String> {
    match name {
        "list_projects" => list_projects(state),
        "list_groups" => list_groups(state),
        "list_runs" => list_runs(state),
        "get_logs" => get_logs(state, &args),
        "save_project" => save_project_from_mcp(state, &args),
        "save_group" => save_group_from_mcp(state, &args),
        "move_project" => move_project_from_mcp(state, &args),
        "start_project_task" => start_project_task(state, &args).await,
        "run_project_command" => run_project_command(state, &args).await,
        "stop_run" => stop_project_run(state, &args).await,
        "stop_all_runs" => stop_all_runs(state).await,
        "request_project_import" => request_project_import(state, &args),
        "update_settings" => update_settings(state, &args),
        "check_updates" => emit_ui_request(
            state,
            "mcp-check-updates",
            json!({}),
            "已请求 Runvoke 检查更新",
        ),
        "request_install_update" => emit_ui_request(
            state,
            "mcp-install-update",
            json!({}),
            "已请求在 Runvoke 中确认安装更新",
        ),
        _ => Err("未支持的工具".into()),
    }
}

fn list_projects(state: &McpHttpState) -> Result<Value, String> {
    let mut store = load_store(&state.app)?;
    for project in &mut store.projects {
        super::normalize_project(project);
    }
    Ok(Value::Array(
        store.projects.iter().map(project_summary).collect(),
    ))
}

fn project_summary(project: &ProjectConfig) -> Value {
    json!({
        "id": project.id,
        "name": project.name,
        "directory": project.directory,
        "groupId": project.group_id,
        "command": project.command,
        "port": project.port,
        "tasks": project.tasks,
        "environmentVariableKeys": project.env.iter().map(|item| item.key.clone()).collect::<Vec<_>>(),
    })
}

fn list_groups(state: &McpHttpState) -> Result<Value, String> {
    Ok(serde_json::to_value(load_store(&state.app)?.groups).map_err(|error| error.to_string())?)
}

fn active_runs(state: &McpHttpState) -> Result<Vec<RuntimeStatus>, String> {
    let app_state = state.app.state::<AppState>();
    let mut runs = app_state
        .runs
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .values()
        .cloned()
        .collect::<Vec<_>>();
    runs.sort_by_key(|status| std::cmp::Reverse(status.started_at.unwrap_or_default()));
    Ok(runs)
}

fn list_runs(state: &McpHttpState) -> Result<Value, String> {
    serde_json::to_value(active_runs(state)?).map_err(|error| error.to_string())
}

fn get_logs(state: &McpHttpState, args: &Value) -> Result<Value, String> {
    let run_id = required_string(args, "runId")?;
    let limit = optional_u64(args, "limit").unwrap_or(200).clamp(1, 500) as usize;
    let app_state = state.app.state::<AppState>();
    let logs = app_state.logs.lock().map_err(|_| "日志状态锁已损坏")?;
    let entries = logs.get(run_id).cloned().unwrap_or_default();
    drop(logs);
    let start = entries.len().saturating_sub(limit);
    let secrets = load_store(&state.app)?
        .projects
        .iter()
        .flat_map(|project| project.env.iter().map(|variable| variable.value.trim()))
        .filter(|value| value.len() >= 3)
        .map(str::to_owned)
        .collect::<Vec<_>>();
    let redacted = entries[start..]
        .iter()
        .map(|entry| redact_log_entry(entry, &secrets))
        .collect::<Vec<_>>();
    serde_json::to_value(redacted).map_err(|error| error.to_string())
}

fn redact_log_entry(entry: &LogEvent, secrets: &[String]) -> LogEvent {
    let mut redacted = entry.clone();
    for secret in secrets {
        redacted.message = redacted.message.replace(secret, "***");
    }
    redacted
}

fn save_project_from_mcp(state: &McpHttpState, args: &Value) -> Result<Value, String> {
    let id = required_string(args, "id")?.to_owned();
    let store = load_store(&state.app)?;
    let existing = store
        .projects
        .iter()
        .find(|project| project.id == id)
        .cloned();
    let Some(existing) = existing else {
        return Err("项目不存在；请使用 request_project_import 请求用户筛选导入".into());
    };
    let tasks = args
        .get("tasks")
        .map(|value| {
            serde_json::from_value::<Vec<ProjectTask>>(value.clone())
                .map_err(|error| format!("任务参数无效：{error}"))
        })
        .transpose()?
        .unwrap_or_else(|| existing.tasks.clone());
    let group_id =
        optional_nullable_string(args, "groupId").unwrap_or_else(|| existing.group_id.clone());
    let port = optional_nullable_u16(args, "port").unwrap_or(existing.port);
    let project = ProjectConfig {
        id,
        name: required_string(args, "name")?.to_owned(),
        directory: required_string(args, "directory")?.to_owned(),
        group_id,
        command: tasks
            .first()
            .map(|task| task.command.clone())
            .unwrap_or_default(),
        env: existing.env,
        port,
        tasks,
    };
    let saved = save_project(state.app.clone(), project)?;
    notify_workspace_changed(state);
    serde_json::to_value(project_summary(&saved)).map_err(|error| error.to_string())
}

fn save_group_from_mcp(state: &McpHttpState, args: &Value) -> Result<Value, String> {
    let id = optional_string(args, "id").unwrap_or_default();
    let existing = (!id.is_empty())
        .then(|| {
            load_store(&state.app)
                .ok()?
                .groups
                .into_iter()
                .find(|group| group.id == id)
        })
        .flatten();
    let group = ProjectGroup {
        id,
        name: required_string(args, "name")?.to_owned(),
        collapsed: args
            .get("collapsed")
            .and_then(Value::as_bool)
            .unwrap_or_else(|| existing.as_ref().is_some_and(|group| group.collapsed)),
    };
    let saved = save_project_group(state.app.clone(), group)?;
    notify_workspace_changed(state);
    serde_json::to_value(saved).map_err(|error| error.to_string())
}

fn move_project_from_mcp(state: &McpHttpState, args: &Value) -> Result<Value, String> {
    let project_id = required_string(args, "projectId")?.to_owned();
    let group_id = optional_nullable_string(args, "groupId").unwrap_or(None);
    let target_index = optional_u64(args, "targetIndex").unwrap_or_default() as usize;
    let projects = super::move_project(state.app.clone(), project_id, group_id, target_index)?;
    notify_workspace_changed(state);
    Ok(Value::Array(projects.iter().map(project_summary).collect()))
}

async fn start_project_task(state: &McpHttpState, args: &Value) -> Result<Value, String> {
    let result = run_task(
        state.app.clone(),
        required_string(args, "projectId")?.to_owned(),
        required_string(args, "taskId")?.to_owned(),
    )
    .await?;
    serde_json::to_value(result).map_err(|error| error.to_string())
}

async fn run_project_command(state: &McpHttpState, args: &Value) -> Result<Value, String> {
    let result = run_temporary_command(
        state.app.clone(),
        required_string(args, "projectId")?.to_owned(),
        required_string(args, "command")?.to_owned(),
    )
    .await?;
    serde_json::to_value(result).map_err(|error| error.to_string())
}

async fn stop_project_run(state: &McpHttpState, args: &Value) -> Result<Value, String> {
    let result = stop_run(
        state.app.clone(),
        required_string(args, "runId")?.to_owned(),
    )
    .await?;
    serde_json::to_value(result).map_err(|error| error.to_string())
}

async fn stop_all_runs(state: &McpHttpState) -> Result<Value, String> {
    let run_ids = active_runs(state)?
        .into_iter()
        .filter(|run| matches!(run.state.as_str(), "starting" | "running" | "stopping"))
        .map(|run| run.run_id)
        .collect::<Vec<_>>();
    let mut stopped = Vec::with_capacity(run_ids.len());
    for run_id in run_ids {
        stopped.push(stop_run(state.app.clone(), run_id).await?);
    }
    serde_json::to_value(stopped).map_err(|error| error.to_string())
}

fn request_project_import(state: &McpHttpState, args: &Value) -> Result<Value, String> {
    let projects = args
        .get("projects")
        .and_then(Value::as_array)
        .filter(|projects| !projects.is_empty() && projects.len() <= 100)
        .ok_or_else(|| "projects 必须是包含 1 至 100 个候选项目的数组".to_owned())?;
    let mut directories = HashSet::new();
    let candidates = projects
        .iter()
        .filter_map(|project| {
            let name = required_string(project, "name").ok()?.trim().to_owned();
            let directory = required_string(project, "directory")
                .ok()?
                .trim()
                .to_owned();
            if !Path::new(&directory).is_dir() {
                return None;
            }
            let normalized = directory.trim_end_matches(['\\', '/']).to_lowercase();
            if !directories.insert(normalized) {
                return None;
            }
            let suggested_command = project
                .get("suggestedCommand")
                .and_then(Value::as_str)
                .map(str::trim)
                .filter(|command| !command.is_empty())
                .map(str::to_owned);
            Some(super::ImportedProject {
                name,
                directory,
                source: "agent".into(),
                suggested_command,
            })
        })
        .collect::<Vec<_>>();
    if candidates.is_empty() {
        return Err("候选项目目录无效或已重复；请提供存在的本地目录".into());
    }
    emit_ui_request(
        state,
        "mcp-project-import-request",
        json!({ "projects": candidates }),
        "已在 Runvoke 中打开 Agent 项目筛选，请由用户勾选并确认纳入",
    )
}

fn update_settings(state: &McpHttpState, args: &Value) -> Result<Value, String> {
    if let Some(enabled) = args.get("autostartEnabled").and_then(Value::as_bool) {
        super::set_autostart_enabled(state.app.clone(), enabled)?;
    }
    emit_ui_request(
        state,
        "mcp-settings-update",
        args.clone(),
        "设置更新请求已交给 Runvoke",
    )
}

fn emit_ui_request(
    state: &McpHttpState,
    event: &str,
    payload: Value,
    message: &str,
) -> Result<Value, String> {
    state
        .app
        .emit(event, payload)
        .map_err(|error| error.to_string())?;
    Ok(json!({ "accepted": true, "message": message }))
}

fn notify_workspace_changed(state: &McpHttpState) {
    let _ = state.app.emit("mcp-workspace-changed", ());
}

fn required_string<'a>(args: &'a Value, name: &str) -> Result<&'a str, String> {
    args.get(name)
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| format!("缺少参数 {name}"))
}

fn optional_string(args: &Value, name: &str) -> Option<String> {
    args.get(name)
        .and_then(Value::as_str)
        .map(str::to_owned)
        .filter(|value| !value.trim().is_empty())
}

fn optional_nullable_string(args: &Value, name: &str) -> Option<Option<String>> {
    args.get(name)
        .map(|value| value.as_str().map(str::to_owned))
}

fn optional_u64(args: &Value, name: &str) -> Option<u64> {
    args.get(name).and_then(Value::as_u64)
}

fn optional_nullable_u16(args: &Value, name: &str) -> Option<Option<u16>> {
    args.get(name)
        .map(|value| value.as_u64().and_then(|value| u16::try_from(value).ok()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn tool_list_has_no_delete_operations() {
        assert!(tool_definitions().iter().all(|tool| {
            !tool
                .get("name")
                .and_then(Value::as_str)
                .is_some_and(|name| name.contains("delete") || name.contains("remove"))
        }));
    }

    #[test]
    fn project_save_is_edit_only() {
        let project_tool = tool_definitions()
            .into_iter()
            .find(|tool| tool.get("name") == Some(&Value::String("save_project".into())))
            .expect("save_project 工具应存在");
        let required = project_tool
            .pointer("/inputSchema/required")
            .and_then(Value::as_array)
            .expect("save_project 应声明必填字段");

        assert!(required.iter().any(|value| value == "id"));
    }

    #[test]
    fn agent_import_requires_agent_supplied_candidates() {
        let import_tool = tool_definitions()
            .into_iter()
            .find(|tool| tool.get("name") == Some(&Value::String("request_project_import".into())))
            .expect("request_project_import 工具应存在");
        let required = import_tool
            .pointer("/inputSchema/required")
            .and_then(Value::as_array)
            .expect("导入工具应声明必填字段");

        assert!(required.iter().any(|value| value == "projects"));
        assert!(import_tool
            .pointer("/inputSchema/properties/source")
            .is_none());
    }

    #[test]
    fn log_entries_redact_saved_environment_values() {
        let entry = LogEvent {
            run_id: "run-1".into(),
            project_id: "project-1".into(),
            stream: "stdout".into(),
            message: "token=secret-value".into(),
            timestamp: 0,
            mode: "append".into(),
        };

        assert_eq!(
            redact_log_entry(&entry, &["secret-value".into()]).message,
            "token=***"
        );
    }
}
