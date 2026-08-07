use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    fs,
    io::{BufReader, Read},
    path::{Path, PathBuf},
    process::{Child, Command, ExitStatus, Output, Stdio},
    sync::{
        mpsc::{sync_channel, RecvTimeoutError, SyncSender},
        Mutex, OnceLock,
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
#[cfg(not(windows))]
use tauri::LogicalSize;
use tauri::{
    menu::{MenuBuilder, MenuItemBuilder},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    AppHandle, Emitter, Manager, PhysicalPosition, RunEvent, State, WebviewUrl, WebviewWindow,
    WebviewWindowBuilder, WindowEvent,
};
use tauri_plugin_autostart::{MacosLauncher, ManagerExt};
use tauri_plugin_updater::{Update, UpdaterExt};
use uuid::Uuid;

mod mcp_server;
mod window_resize_guard;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct EnvVariable {
    key: String,
    value: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProjectConfig {
    id: String,
    name: String,
    directory: String,
    #[serde(default)]
    group_id: Option<String>,
    // Kept for backwards-compatible reads of existing project files.
    command: String,
    #[serde(default)]
    env: Vec<EnvVariable>,
    port: Option<u16>,
    #[serde(default)]
    tasks: Vec<ProjectTask>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct GitProjectStatus {
    git_branch: Option<String>,
    staged_changes: u32,
    unstaged_changes: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProjectGroup {
    id: String,
    name: String,
    #[serde(default)]
    collapsed: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProjectTask {
    id: String,
    name: String,
    command: String,
    mode: String,
}

const MAX_PROJECT_TASKS: usize = 3;
const LOG_CHANNEL_CAPACITY: usize = 8_192;
const LOG_BATCH_LIMIT: usize = 256;
const LOG_BATCH_INTERVAL: Duration = Duration::from_millis(50);
const LOG_MESSAGE_LIMIT: usize = 16_000;
const MCP_LOG_HISTORY_LIMIT: usize = 500;
const PREVIEW_UPDATE_ENDPOINT: &str =
    "https://runvoke-updates.oss-cn-shanghai.aliyuncs.com/runvoke/latest-prerelease.json";
const NOTIFICATION_WINDOW_LABEL: &str = "system-notification";
const NOTIFICATION_WINDOW_WIDTH: f64 = 380.0;
const NOTIFICATION_WINDOW_HEIGHT: f64 = 132.0;
const NOTIFICATION_WINDOW_MARGIN: f64 = 18.0;
const NOTIFICATION_CARD_HEIGHT: f64 = 114.0;
const NOTIFICATION_STACK_GAP: f64 = 8.0;
const MAX_VISIBLE_NOTIFICATIONS: u8 = 3;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct NotificationConfig {
    id: String,
    theme: String,
    position: String,
    stacking_enabled: bool,
    tone: String,
    title: String,
    message: String,
    meta: String,
    dedupe_key: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct NotificationHitRegion {
    x: f64,
    y: f64,
    width: f64,
    height: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct StoreFile {
    #[serde(default)]
    projects: Vec<ProjectConfig>,
    #[serde(default)]
    groups: Vec<ProjectGroup>,
    #[serde(default)]
    mcp: McpConfig,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct McpConfig {
    #[serde(default)]
    enabled: bool,
    #[serde(default = "default_mcp_port")]
    port: u16,
    #[serde(default)]
    token: String,
}

const fn default_mcp_port() -> u16 {
    38_465
}

impl Default for McpConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            port: default_mcp_port(),
            token: String::new(),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct McpServerStatus {
    enabled: bool,
    running: bool,
    port: u16,
    endpoint: String,
    authorization_token: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct PreviewUpdate {
    version: String,
    body: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct PreviewUpdateDownloadProgress {
    received: usize,
    total: Option<u64>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeStatus {
    run_id: String,
    project_id: String,
    task_id: String,
    task_name: String,
    mode: String,
    state: String,
    pid: Option<u32>,
    started_at: Option<u64>,
    exit_code: Option<i32>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct LogEvent {
    run_id: String,
    project_id: String,
    stream: String,
    message: String,
    timestamp: u64,
    mode: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct DetectedProjectName {
    name: String,
    source: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ImportedProject {
    name: String,
    directory: String,
    source: String,
    suggested_command: Option<String>,
}

struct ManagedProcess {
    child: Child,
    #[cfg(target_os = "windows")]
    job: JobHandle,
}

#[cfg(target_os = "windows")]
struct JobHandle(windows_sys::Win32::Foundation::HANDLE);

#[cfg(target_os = "windows")]
unsafe impl Send for JobHandle {}

#[cfg(target_os = "windows")]
impl JobHandle {
    fn attach(child: &Child) -> Result<Self, String> {
        use std::{mem::size_of, os::windows::io::AsRawHandle, ptr::null};
        use windows_sys::Win32::{
            Foundation::HANDLE,
            System::JobObjects::{
                AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
                SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
                JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        let raw_job = unsafe { CreateJobObjectW(null(), null()) };
        if raw_job.is_null() {
            return Err(format!(
                "无法创建 Windows Job Object：{}",
                std::io::Error::last_os_error()
            ));
        }
        let job = Self(raw_job);
        let mut information = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        information.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        let configured = unsafe {
            SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                &information as *const _ as *const _,
                size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            )
        };
        if configured == 0 {
            return Err(format!(
                "无法配置 Windows Job Object：{}",
                std::io::Error::last_os_error()
            ));
        }

        let process_handle = child.as_raw_handle() as HANDLE;
        let assigned = unsafe { AssignProcessToJobObject(job.0, process_handle) };
        if assigned == 0 {
            return Err(format!(
                "无法将项目进程加入 Windows Job Object：{}",
                std::io::Error::last_os_error()
            ));
        }
        Ok(job)
    }

    fn terminate(&self) -> Result<(), String> {
        use windows_sys::Win32::System::JobObjects::TerminateJobObject;
        if unsafe { TerminateJobObject(self.0, 1) } == 0 {
            Err(format!(
                "Windows Job Object 终止失败：{}",
                std::io::Error::last_os_error()
            ))
        } else {
            Ok(())
        }
    }
}

#[cfg(target_os = "windows")]
impl Drop for JobHandle {
    fn drop(&mut self) {
        use windows_sys::Win32::Foundation::CloseHandle;
        unsafe {
            CloseHandle(self.0);
        }
    }
}

#[derive(Default)]
struct NotificationBridge {
    ready: bool,
    pending: Vec<NotificationConfig>,
}

#[derive(Default)]
struct AppState {
    processes: Mutex<HashMap<String, ManagedProcess>>,
    runs: Mutex<HashMap<String, RuntimeStatus>>,
    exit_codes: Mutex<HashMap<String, Option<i32>>>,
    logs: Mutex<HashMap<String, Vec<LogEvent>>>,
    log_sender: OnceLock<SyncSender<LogEvent>>,
    mcp: Mutex<Option<mcp_server::McpServerRuntime>>,
    preview_update: Mutex<Option<Update>>,
    notification_bridge: Mutex<NotificationBridge>,
}

fn now_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}

fn store_path(app: &AppHandle) -> Result<PathBuf, String> {
    let directory = app
        .path()
        .app_config_dir()
        .map_err(|error| format!("无法定位配置目录：{error}"))?;
    fs::create_dir_all(&directory).map_err(|error| format!("无法创建配置目录：{error}"))?;
    Ok(directory.join("projects.json"))
}

fn load_store(app: &AppHandle) -> Result<StoreFile, String> {
    let path = store_path(app)?;
    if !path.exists() {
        return Ok(StoreFile::default());
    }

    let content = fs::read_to_string(&path).map_err(|error| format!("无法读取配置：{error}"))?;
    serde_json::from_str(&content).map_err(|error| format!("配置文件格式无效：{error}"))
}

fn save_store(app: &AppHandle, store: &StoreFile) -> Result<(), String> {
    let path = store_path(app)?;
    let content =
        serde_json::to_string_pretty(store).map_err(|error| format!("无法序列化配置：{error}"))?;
    fs::write(path, content).map_err(|error| format!("无法保存配置：{error}"))
}

fn normalize_project(project: &mut ProjectConfig) {
    project.directory = display_directory(&project.directory);
    if project.tasks.is_empty() {
        project.tasks.push(ProjectTask {
            id: "default".into(),
            name: "开发服务器".into(),
            command: project.command.clone(),
            mode: "service".into(),
        });
    }
    project.tasks.truncate(MAX_PROJECT_TASKS);
    for task in &mut project.tasks {
        task.id = if task.id.trim().is_empty() {
            Uuid::new_v4().to_string()
        } else {
            task.id.trim().to_owned()
        };
        task.name = task.name.trim().to_owned();
        task.command = task.command.trim().to_owned();
        task.mode = task.mode.trim().to_lowercase();
    }
    project.command = project
        .tasks
        .first()
        .map(|task| task.command.clone())
        .unwrap_or_default();
}

fn git_output(directory: &Path, arguments: &[&str]) -> Option<Output> {
    if !directory.is_dir() {
        return None;
    }

    let mut command = Command::new("git");
    command.arg("-C").arg(directory).args(arguments);
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }

    let output = command.output().ok()?;
    output.status.success().then_some(output)
}

fn git_change_counts(status: &[u8]) -> (u32, u32) {
    let mut staged_changes = 0;
    let mut unstaged_changes = 0;
    let mut skip_rename_origin = false;

    for entry in status.split(|byte| *byte == 0) {
        if entry.is_empty() {
            continue;
        }
        if skip_rename_origin {
            skip_rename_origin = false;
            continue;
        }
        if entry.len() < 2 {
            continue;
        }

        let index_status = entry[0];
        let worktree_status = entry[1];
        if index_status == b'?' && worktree_status == b'?' {
            unstaged_changes += 1;
            continue;
        }
        if index_status != b' ' {
            staged_changes += 1;
        }
        if worktree_status != b' ' {
            unstaged_changes += 1;
        }
        if matches!(index_status, b'R' | b'C') {
            skip_rename_origin = true;
        }
    }

    (staged_changes, unstaged_changes)
}

fn current_git_status(directory: &Path) -> Option<GitProjectStatus> {
    let branch_output = git_output(directory, &["branch", "--show-current"])?;
    let status_output = git_output(directory, &["status", "--porcelain=v1", "-z"])?;
    let branch = String::from_utf8_lossy(&branch_output.stdout)
        .trim()
        .to_owned();
    let (staged_changes, unstaged_changes) = git_change_counts(&status_output.stdout);

    Some(GitProjectStatus {
        git_branch: (!branch.is_empty()).then_some(branch),
        staged_changes,
        unstaged_changes,
    })
}

fn validate_project(project: &ProjectConfig) -> Result<(), String> {
    if project.name.trim().is_empty() {
        return Err("项目名称不能为空".into());
    }
    if project.tasks.is_empty() {
        return Err("至少需要配置一个任务".into());
    }
    if project.tasks.len() > MAX_PROJECT_TASKS {
        return Err(format!("预设任务最多只能配置 {MAX_PROJECT_TASKS} 条"));
    }
    if project.tasks.iter().any(|task| task.name.is_empty()) {
        return Err("任务名称不能为空".into());
    }
    if project.tasks.iter().any(|task| task.command.is_empty()) {
        return Err("任务命令不能为空".into());
    }
    if project
        .tasks
        .iter()
        .any(|task| task.mode != "service" && task.mode != "once")
    {
        return Err("任务类型必须是 service 或 once".into());
    }
    let directory = Path::new(project.directory.trim());
    if !directory.is_dir() {
        return Err("项目目录不存在或不是文件夹".into());
    }
    if project.env.iter().any(|item| item.key.trim().is_empty()) {
        return Err("环境变量名称不能为空".into());
    }
    Ok(())
}

fn quoted_value(value: &str) -> Option<String> {
    let value = value.trim().trim_matches('"').trim_matches('\'').trim();
    (!value.is_empty()).then(|| value.to_owned())
}

fn section_value(content: &str, section: &str, key: &str) -> Option<String> {
    let mut in_section = false;
    for line in content.lines() {
        let line = line.trim();
        if line.starts_with('[') && line.ends_with(']') {
            in_section = line == section;
            continue;
        }
        if in_section {
            let Some((candidate, value)) = line.split_once('=') else {
                continue;
            };
            if candidate.trim() == key {
                return quoted_value(value.split('#').next().unwrap_or_default());
            }
        }
    }
    None
}

fn file_name(path: &Path, source: &str) -> Option<DetectedProjectName> {
    let content = fs::read_to_string(path).ok()?;
    let name = match source {
        "package.json" => serde_json::from_str::<serde_json::Value>(&content)
            .ok()?
            .get("name")?
            .as_str()?
            .to_owned(),
        "composer.json" => serde_json::from_str::<serde_json::Value>(&content)
            .ok()?
            .get("name")?
            .as_str()?
            .rsplit('/')
            .next()?
            .to_owned(),
        "Cargo.toml" => section_value(&content, "[package]", "name")?,
        "pyproject.toml" => section_value(&content, "[project]", "name")?,
        "pubspec.yaml" => content
            .lines()
            .find_map(|line| line.trim().strip_prefix("name:").and_then(quoted_value))?,
        "go.mod" => content
            .lines()
            .find_map(|line| line.trim().strip_prefix("module ").and_then(quoted_value))?
            .rsplit('/')
            .next()?
            .to_owned(),
        "pom.xml" => content
            .split("<artifactId>")
            .nth(1)?
            .split("</artifactId>")
            .next()?
            .trim()
            .to_owned(),
        _ => return None,
    };
    (!name.is_empty()).then(|| DetectedProjectName {
        name,
        source: source.into(),
    })
}

fn csproj_name(directory: &Path) -> Option<DetectedProjectName> {
    let path = fs::read_dir(directory)
        .ok()?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .find(|path| {
            path.extension()
                .is_some_and(|extension| extension.eq_ignore_ascii_case("csproj"))
        })?;
    let content = fs::read_to_string(&path).ok()?;
    let name = content
        .split("<AssemblyName>")
        .nth(1)
        .and_then(|value| value.split("</AssemblyName>").next())
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_owned)
        .or_else(|| {
            path.file_stem()
                .and_then(|name| name.to_str())
                .map(str::to_owned)
        })?;
    Some(DetectedProjectName {
        name,
        source: path.file_name()?.to_string_lossy().into_owned(),
    })
}

#[tauri::command]
fn detect_project_name(directory: String) -> Result<DetectedProjectName, String> {
    let directory = PathBuf::from(directory.trim());
    if !directory.is_dir() {
        return Err("项目目录不存在或不是文件夹".into());
    }
    for source in [
        "package.json",
        "Cargo.toml",
        "pyproject.toml",
        "go.mod",
        "pom.xml",
        "pubspec.yaml",
        "composer.json",
    ] {
        if let Some(detected) = file_name(&directory.join(source), source) {
            return Ok(detected);
        }
    }
    if let Some(detected) = csproj_name(&directory) {
        return Ok(detected);
    }
    let name = directory
        .file_name()
        .and_then(|name| name.to_str())
        .filter(|name| !name.is_empty())
        .ok_or_else(|| "无法从项目目录推断名称".to_owned())?;
    Ok(DetectedProjectName {
        name: name.to_owned(),
        source: "目录名".into(),
    })
}

#[tauri::command]
async fn check_preview_update(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<Option<PreviewUpdate>, String> {
    let endpoint = PREVIEW_UPDATE_ENDPOINT
        .parse()
        .map_err(|error| format!("预览更新地址无效：{error}"))?;
    let updater = app
        .updater_builder()
        .endpoints(vec![endpoint])
        .map_err(|error| format!("初始化预览更新检查失败：{error}"))?
        .build()
        .map_err(|error| format!("初始化预览更新器失败：{error}"))?;
    let update = updater
        .check()
        .await
        .map_err(|error| format!("检查预览更新失败：{error}"))?;

    let preview = update.as_ref().map(|update| PreviewUpdate {
        version: update.version.clone(),
        body: update.body.clone().unwrap_or_default(),
    });
    let mut pending = state
        .preview_update
        .lock()
        .map_err(|_| "预览更新状态不可用".to_owned())?;
    *pending = update;
    Ok(preview)
}

#[tauri::command]
async fn install_preview_update(app: AppHandle, state: State<'_, AppState>) -> Result<(), String> {
    let update = state
        .preview_update
        .lock()
        .map_err(|_| "预览更新状态不可用".to_owned())?
        .take()
        .ok_or_else(|| "没有可安装的预览更新，请重新检查更新".to_owned())?;
    let progress_app = app.clone();
    let _ = app.emit(
        "preview-update-download-progress",
        PreviewUpdateDownloadProgress {
            received: 0,
            total: None,
        },
    );
    update
        .download_and_install(
            move |received, total| {
                let _ = progress_app.emit(
                    "preview-update-download-progress",
                    PreviewUpdateDownloadProgress { received, total },
                );
            },
            || {},
        )
        .await
        .map_err(|error| format!("安装预览更新失败：{error}"))
}

fn hex_value(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'a'..=b'f' => Some(value - b'a' + 10),
        b'A'..=b'F' => Some(value - b'A' + 10),
        _ => None,
    }
}

fn display_directory(value: &str) -> String {
    let directory = value.trim();
    #[cfg(windows)]
    {
        if let Some(unc_path) = directory.strip_prefix("\\\\?\\UNC\\") {
            return format!("\\\\{unc_path}");
        }
        return directory
            .strip_prefix("\\\\?\\")
            .unwrap_or(directory)
            .to_owned();
    }
    #[cfg(not(windows))]
    directory.to_owned()
}

fn percent_decode(value: &str) -> Option<String> {
    let bytes = value.as_bytes();
    let mut decoded = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'%' {
            let high = hex_value(*bytes.get(index + 1)?)?;
            let low = hex_value(*bytes.get(index + 2)?)?;
            decoded.push(high * 16 + low);
            index += 3;
        } else {
            decoded.push(bytes[index]);
            index += 1;
        }
    }
    String::from_utf8(decoded).ok()
}

fn vscode_uri_to_path(value: &str) -> Option<PathBuf> {
    let uri = value.strip_prefix("file://")?;
    let decoded = percent_decode(uri)?;
    #[cfg(windows)]
    {
        let path = decoded
            .strip_prefix('/')
            .unwrap_or(&decoded)
            .replace('/', "\\");
        return Some(PathBuf::from(path));
    }
    #[cfg(not(windows))]
    Some(PathBuf::from(decoded))
}

fn editor_storage_paths(editor_names: &[&str]) -> Vec<PathBuf> {
    let Some(app_data) = std::env::var_os("APPDATA") else {
        return Vec::new();
    };
    editor_names
        .iter()
        .map(|name| {
            PathBuf::from(&app_data)
                .join(name)
                .join("User")
                .join("globalStorage")
                .join("storage.json")
        })
        .collect()
}

fn workspace_paths_from_storage(storage: &serde_json::Value) -> Vec<PathBuf> {
    let mut paths = Vec::new();
    for key in ["workspaces", "folders"] {
        if let Some(entries) = storage
            .get("profileAssociations")
            .and_then(|value| value.get(key))
            .and_then(serde_json::Value::as_object)
        {
            for uri in entries.keys() {
                let Some(path) = vscode_uri_to_path(uri) else {
                    continue;
                };
                if path
                    .extension()
                    .is_some_and(|extension| extension.eq_ignore_ascii_case("code-workspace"))
                {
                    let Some(content) = fs::read_to_string(&path).ok() else {
                        continue;
                    };
                    let Ok(workspace) = serde_json::from_str::<serde_json::Value>(&content) else {
                        continue;
                    };
                    if let Some(folders) = workspace
                        .get("folders")
                        .and_then(serde_json::Value::as_array)
                    {
                        for folder in folders {
                            if let Some(folder_uri) =
                                folder.get("uri").and_then(serde_json::Value::as_str)
                            {
                                if let Some(folder_path) = vscode_uri_to_path(folder_uri) {
                                    paths.push(folder_path);
                                }
                            } else if let Some(folder_path) =
                                folder.get("path").and_then(serde_json::Value::as_str)
                            {
                                paths.push(
                                    path.parent().unwrap_or(Path::new(".")).join(folder_path),
                                );
                            }
                        }
                    }
                } else {
                    paths.push(path);
                }
            }
        }
    }
    paths
}

fn suggested_project_command(directory: &Path) -> Option<String> {
    if let Ok(content) = fs::read_to_string(directory.join("package.json")) {
        if let Ok(package) = serde_json::from_str::<serde_json::Value>(&content) {
            if let Some(scripts) = package
                .get("scripts")
                .and_then(serde_json::Value::as_object)
            {
                for name in ["dev", "start", "serve", "watch"] {
                    if scripts.contains_key(name) {
                        return Some(format!("pnpm {name}"));
                    }
                }
            }
        }
    }
    if directory.join("Cargo.toml").is_file() {
        return Some("cargo run".into());
    }
    if directory.join("go.mod").is_file() {
        return Some("go run .".into());
    }
    if directory.join("main.py").is_file() {
        return Some("python main.py".into());
    }
    None
}

fn list_editor_projects(
    source: &str,
    storage_paths: impl IntoIterator<Item = PathBuf>,
) -> Result<Vec<ImportedProject>, String> {
    let mut paths = Vec::new();
    for storage_path in storage_paths {
        let Ok(content) = fs::read_to_string(storage_path) else {
            continue;
        };
        let Ok(storage) = serde_json::from_str::<serde_json::Value>(&content) else {
            continue;
        };
        paths.extend(workspace_paths_from_storage(&storage));
    }

    let mut seen = HashSet::new();
    let mut projects = paths
        .into_iter()
        .filter_map(|path| {
            let canonical_directory = path.canonicalize().ok()?;
            if !canonical_directory.is_dir() {
                return None;
            }
            let key = canonical_directory.to_string_lossy().to_lowercase();
            if !seen.insert(key) {
                return None;
            }
            let name = canonical_directory
                .file_name()?
                .to_string_lossy()
                .into_owned();
            Some(ImportedProject {
                name,
                directory: display_directory(&canonical_directory.to_string_lossy()),
                source: source.into(),
                suggested_command: suggested_project_command(&canonical_directory),
            })
        })
        .collect::<Vec<_>>();
    projects.sort_by_key(|project| project.directory.to_lowercase());
    Ok(projects)
}

#[tauri::command]
fn list_vscode_projects() -> Result<Vec<ImportedProject>, String> {
    list_editor_projects(
        "Visual Studio Code",
        editor_storage_paths(&["Code", "Code - Insiders", "VSCodium"]),
    )
}

#[tauri::command]
fn list_cursor_projects() -> Result<Vec<ImportedProject>, String> {
    list_editor_projects("Cursor", editor_storage_paths(&["Cursor"]))
}

fn mcp_server_status(app: &AppHandle, state: &AppState) -> Result<McpServerStatus, String> {
    let store = load_store(app)?;
    let running = state
        .mcp
        .lock()
        .map_err(|_| "MCP 服务状态锁已损坏")?
        .is_some();
    let port = store.mcp.port;
    Ok(McpServerStatus {
        enabled: store.mcp.enabled,
        running,
        port,
        endpoint: format!("http://127.0.0.1:{port}/mcp"),
        authorization_token: store.mcp.token,
    })
}

#[tauri::command]
fn get_mcp_server_status(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<McpServerStatus, String> {
    mcp_server_status(&app, state.inner())
}

#[tauri::command]
async fn set_mcp_server_enabled(
    app: AppHandle,
    state: State<'_, AppState>,
    enabled: bool,
) -> Result<McpServerStatus, String> {
    if enabled {
        if state
            .mcp
            .lock()
            .map_err(|_| "MCP 服务状态锁已损坏")?
            .is_some()
        {
            return mcp_server_status(&app, state.inner());
        }

        let mut store = load_store(&app)?;
        if store.mcp.token.is_empty() {
            store.mcp.token = Uuid::new_v4().to_string();
        }
        let runtime =
            mcp_server::start(app.clone(), store.mcp.port, store.mcp.token.clone()).await?;
        state
            .mcp
            .lock()
            .map_err(|_| "MCP 服务状态锁已损坏")?
            .replace(runtime);
        store.mcp.enabled = true;
        save_store(&app, &store)?;
    } else {
        if let Some(runtime) = state.mcp.lock().map_err(|_| "MCP 服务状态锁已损坏")?.take()
        {
            runtime.stop();
        }
        let mut store = load_store(&app)?;
        store.mcp.enabled = false;
        save_store(&app, &store)?;
    }
    let status = mcp_server_status(&app, state.inner())?;
    let _ = app.emit("mcp-server-status", &status);
    Ok(status)
}

#[tauri::command]
fn list_projects(app: AppHandle) -> Result<Vec<ProjectConfig>, String> {
    let mut store = load_store(&app)?;
    let mut directories_changed = false;
    for project in &mut store.projects {
        let previous_directory = project.directory.clone();
        normalize_project(project);
        directories_changed |= project.directory != previous_directory;
    }
    if directories_changed {
        save_store(&app, &store)?;
    }
    Ok(store.projects)
}

#[tauri::command]
fn get_project_git_status(
    app: AppHandle,
    project_id: String,
) -> Result<Option<GitProjectStatus>, String> {
    let project = load_store(&app)?
        .projects
        .into_iter()
        .find(|project| project.id == project_id);

    Ok(project.and_then(|project| current_git_status(Path::new(&project.directory))))
}

#[tauri::command]
fn list_project_groups(app: AppHandle) -> Result<Vec<ProjectGroup>, String> {
    Ok(load_store(&app)?.groups)
}

#[tauri::command]
fn save_project(app: AppHandle, mut project: ProjectConfig) -> Result<ProjectConfig, String> {
    project.name = project.name.trim().to_owned();
    project.directory = project.directory.trim().to_owned();
    project.env.retain(|item| !item.key.trim().is_empty());
    for item in &mut project.env {
        item.key = item.key.trim().to_owned();
    }
    if project.tasks.len() > MAX_PROJECT_TASKS {
        return Err(format!("预设任务最多只能配置 {MAX_PROJECT_TASKS} 条"));
    }
    normalize_project(&mut project);
    validate_project(&project)?;

    let mut store = load_store(&app)?;
    if project
        .group_id
        .as_ref()
        .is_some_and(|group_id| !store.groups.iter().any(|group| group.id == *group_id))
    {
        return Err("所属分组不存在".into());
    }
    if project.id.trim().is_empty() {
        project.id = Uuid::new_v4().to_string();
        store.projects.push(project.clone());
    } else if let Some(existing) = store.projects.iter_mut().find(|item| item.id == project.id) {
        *existing = project.clone();
    } else {
        store.projects.push(project.clone());
    }
    save_store(&app, &store)?;
    Ok(project)
}

#[tauri::command]
fn save_project_group(app: AppHandle, mut group: ProjectGroup) -> Result<ProjectGroup, String> {
    group.name = group.name.trim().to_owned();
    if group.name.is_empty() {
        return Err("分组名称不能为空".into());
    }
    if group.name.chars().count() > 30 {
        return Err("分组名称不能超过 30 个字符".into());
    }

    let mut store = load_store(&app)?;
    if store
        .groups
        .iter()
        .any(|item| item.id != group.id && item.name.eq_ignore_ascii_case(&group.name))
    {
        return Err("已存在同名分组".into());
    }
    if group.id.trim().is_empty() {
        group.id = Uuid::new_v4().to_string();
        store.groups.push(group.clone());
    } else if let Some(existing) = store.groups.iter_mut().find(|item| item.id == group.id) {
        *existing = group.clone();
    } else {
        return Err("分组不存在".into());
    }
    save_store(&app, &store)?;
    Ok(group)
}

#[tauri::command]
fn set_project_group_collapsed(
    app: AppHandle,
    group_id: String,
    collapsed: bool,
) -> Result<ProjectGroup, String> {
    let mut store = load_store(&app)?;
    let group = store
        .groups
        .iter_mut()
        .find(|group| group.id == group_id)
        .ok_or_else(|| "分组不存在".to_owned())?;
    group.collapsed = collapsed;
    let result = group.clone();
    save_store(&app, &store)?;
    Ok(result)
}

#[tauri::command]
fn set_project_groups_collapsed(
    app: AppHandle,
    collapsed: bool,
) -> Result<Vec<ProjectGroup>, String> {
    let mut store = load_store(&app)?;
    for group in &mut store.groups {
        group.collapsed = collapsed;
    }
    save_store(&app, &store)?;
    Ok(store.groups)
}

#[tauri::command]
fn delete_project_group(app: AppHandle, group_id: String) -> Result<(), String> {
    let mut store = load_store(&app)?;
    if !store.groups.iter().any(|group| group.id == group_id) {
        return Err("分组不存在".into());
    }
    store.groups.retain(|group| group.id != group_id);
    for project in &mut store.projects {
        if project.group_id.as_deref() == Some(group_id.as_str()) {
            project.group_id = None;
        }
    }
    save_store(&app, &store)
}

#[tauri::command]
fn move_project(
    app: AppHandle,
    project_id: String,
    group_id: Option<String>,
    target_index: usize,
) -> Result<Vec<ProjectConfig>, String> {
    let mut store = load_store(&app)?;
    if group_id
        .as_ref()
        .is_some_and(|id| !store.groups.iter().any(|group| group.id == *id))
    {
        return Err("目标分组不存在".into());
    }

    let source_index = store
        .projects
        .iter()
        .position(|project| project.id == project_id)
        .ok_or_else(|| "项目不存在".to_owned())?;
    let mut project = store.projects.remove(source_index);
    project.group_id = group_id.clone();
    let matching_indices = store
        .projects
        .iter()
        .enumerate()
        .filter_map(|(index, item)| (item.group_id == group_id).then_some(index))
        .collect::<Vec<_>>();
    let insert_index = matching_indices
        .get(target_index)
        .copied()
        .or_else(|| matching_indices.last().map(|index| index + 1))
        .unwrap_or(store.projects.len());
    store.projects.insert(insert_index, project);
    save_store(&app, &store)?;
    Ok(store.projects)
}

#[tauri::command]
fn reorder_projects(app: AppHandle, project_ids: Vec<String>) -> Result<(), String> {
    let mut store = load_store(&app)?;
    if project_ids.len() != store.projects.len() {
        return Err("项目排序数据与当前项目数量不一致".into());
    }

    let mut positions = HashMap::with_capacity(project_ids.len());
    for (index, project_id) in project_ids.into_iter().enumerate() {
        if positions.insert(project_id, index).is_some() {
            return Err("项目排序数据包含重复项目".into());
        }
    }
    if store
        .projects
        .iter()
        .any(|project| !positions.contains_key(&project.id))
    {
        return Err("项目排序数据包含未知项目".into());
    }

    store
        .projects
        .sort_by_key(|project| positions.get(&project.id).copied().unwrap_or(usize::MAX));
    save_store(&app, &store)
}

fn delete_project_inner(app: &AppHandle, state: &AppState, project_id: &str) -> Result<(), String> {
    let run_ids = state
        .runs
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .values()
        .filter(|status| {
            status.project_id == project_id
                && matches!(status.state.as_str(), "starting" | "running" | "stopping")
        })
        .map(|status| status.run_id.clone())
        .collect::<Vec<_>>();
    for run_id in run_ids {
        let _ = stop_run_inner(app, state, &run_id)?;
    }

    let mut store = load_store(app)?;
    store.projects.retain(|project| project.id != project_id);
    save_store(app, &store)
}

#[tauri::command]
async fn delete_project(app: AppHandle, project_id: String) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state = app.state::<AppState>();
        delete_project_inner(&app, state.inner(), &project_id)
    })
    .await
    .map_err(|error| format!("删除项目后台任务失败：{error}"))?
}

fn emit_log(
    app: &AppHandle,
    run_id: &str,
    project_id: &str,
    stream: &str,
    message: impl Into<String>,
) {
    emit_log_with_mode(app, run_id, project_id, stream, message, "append");
}

fn emit_log_with_mode(
    app: &AppHandle,
    run_id: &str,
    project_id: &str,
    stream: &str,
    message: impl Into<String>,
    mode: &str,
) {
    let event = LogEvent {
        run_id: run_id.to_owned(),
        project_id: project_id.to_owned(),
        stream: stream.to_owned(),
        message: truncate_log_message(message.into()),
        timestamp: now_millis(),
        mode: mode.to_owned(),
    };
    if let Ok(mut logs) = app.state::<AppState>().logs.lock() {
        let history = logs.entry(event.run_id.clone()).or_default();
        history.push(event.clone());
        if history.len() > MCP_LOG_HISTORY_LIMIT {
            history.drain(..history.len() - MCP_LOG_HISTORY_LIMIT);
        }
    }
    if let Some(sender) = app.state::<AppState>().log_sender.get() {
        // Logging must never block the managed process. If the UI cannot keep up,
        // dropping old display output is safer than slowing the compiler itself.
        let _ = sender.try_send(event);
    } else {
        let _ = app.emit("project-logs", vec![event]);
    }
}

fn truncate_log_message(mut message: String) -> String {
    if message.len() <= LOG_MESSAGE_LIMIT {
        return message;
    }
    let mut end = LOG_MESSAGE_LIMIT;
    while !message.is_char_boundary(end) {
        end -= 1;
    }
    message.truncate(end);
    message.push_str("… [单条日志过长，已截断]");
    message
}

fn setup_log_dispatcher(app: &tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    let (sender, receiver) = sync_channel::<LogEvent>(LOG_CHANNEL_CAPACITY);
    app.state::<AppState>()
        .log_sender
        .set(sender)
        .map_err(|_| std::io::Error::other("日志分发器已初始化"))?;

    let app_handle = app.handle().clone();
    thread::spawn(move || {
        let mut disconnected = false;
        while !disconnected {
            let first = match receiver.recv() {
                Ok(event) => event,
                Err(_) => break,
            };
            let mut batch = Vec::with_capacity(LOG_BATCH_LIMIT);
            batch.push(first);
            let deadline = Instant::now() + LOG_BATCH_INTERVAL;

            while batch.len() < LOG_BATCH_LIMIT {
                let remaining = deadline.saturating_duration_since(Instant::now());
                match receiver.recv_timeout(remaining) {
                    Ok(event) => batch.push(event),
                    Err(RecvTimeoutError::Timeout) => break,
                    Err(RecvTimeoutError::Disconnected) => {
                        disconnected = true;
                        break;
                    }
                }
            }
            let _ = app_handle.emit("project-logs", batch);
        }
    });
    Ok(())
}

fn pipe_logs<R: Read + Send + 'static>(
    app: AppHandle,
    run_id: String,
    project_id: String,
    stream: &'static str,
    reader: R,
) {
    thread::spawn(move || {
        let mut reader = BufReader::new(reader);
        let mut chunk = [0_u8; 8_192];
        let mut line = Vec::new();
        let mut last_progress = Vec::new();
        let mut progress_active = false;
        loop {
            match reader.read(&mut chunk) {
                Ok(0) => break,
                Ok(length) => {
                    for &byte in &chunk[..length] {
                        match byte {
                            b'\r' => {
                                if !line.is_empty() {
                                    last_progress.clone_from(&line);
                                    emit_log_with_mode(
                                        &app,
                                        &run_id,
                                        &project_id,
                                        stream,
                                        String::from_utf8_lossy(&line).into_owned(),
                                        "progress",
                                    );
                                }
                                line.clear();
                                progress_active = true;
                            }
                            b'\n' => {
                                let completed = if line.is_empty() && progress_active {
                                    &last_progress
                                } else {
                                    &line
                                };
                                emit_log_with_mode(
                                    &app,
                                    &run_id,
                                    &project_id,
                                    stream,
                                    String::from_utf8_lossy(completed).into_owned(),
                                    if progress_active { "finish" } else { "append" },
                                );
                                line.clear();
                                last_progress.clear();
                                progress_active = false;
                            }
                            _ => line.push(byte),
                        }
                    }
                }
                Err(error) => {
                    emit_log(
                        &app,
                        &run_id,
                        &project_id,
                        "system",
                        format!("读取日志失败：{error}"),
                    );
                    break;
                }
            }
        }
        if !line.is_empty() || progress_active {
            let completed = if line.is_empty() {
                &last_progress
            } else {
                &line
            };
            emit_log_with_mode(
                &app,
                &run_id,
                &project_id,
                stream,
                String::from_utf8_lossy(completed).into_owned(),
                if progress_active { "finish" } else { "append" },
            );
        }
    });
}

fn shell_command(command_line: &str) -> Command {
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        // Keep the user's PowerShell profile so fnm and other shell-managed tools are available.
        let script = format!("& {{ {command_line} }}");
        let mut command = Command::new("pwsh.exe");
        command
            .args(["-NoLogo", "-NonInteractive", "-Command", &script])
            .creation_flags(CREATE_NO_WINDOW);
        command
    }

    #[cfg(not(target_os = "windows"))]
    {
        use std::os::unix::process::CommandExt;
        let mut command = Command::new("sh");
        command.args(["-lc", command_line]).process_group(0);
        command
    }
}

fn load_project(app: &AppHandle, project_id: &str) -> Result<ProjectConfig, String> {
    let mut project = load_store(app)?
        .projects
        .into_iter()
        .find(|project| project.id == project_id)
        .ok_or_else(|| "未找到项目配置".to_owned())?;
    normalize_project(&mut project);
    validate_project(&project)?;
    Ok(project)
}

fn start_task_inner(
    app: &AppHandle,
    state: &AppState,
    project: &ProjectConfig,
    task: ProjectTask,
) -> Result<RuntimeStatus, String> {
    if task.mode == "service" {
        let has_active_instance = state
            .runs
            .lock()
            .map_err(|_| "进程状态锁已损坏")?
            .values()
            .any(|status| {
                status.project_id == project.id
                    && status.task_id == task.id
                    && matches!(status.state.as_str(), "starting" | "running" | "stopping")
            });
        if has_active_instance {
            return Err("该常驻任务已经在运行".into());
        }
    }

    let run_id = Uuid::new_v4().to_string();
    let mut command = shell_command(&task.command);
    command
        .current_dir(&project.directory)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for item in &project.env {
        command.env(&item.key, &item.value);
    }

    let mut child = command
        .spawn()
        .map_err(|error| format!("启动失败：{error}"))?;
    #[cfg(target_os = "windows")]
    let job = JobHandle::attach(&child).map_err(|error| {
        let _ = child.kill();
        error
    })?;
    let pid = child.id();
    let started_at = now_millis();
    if let Some(stdout) = child.stdout.take() {
        pipe_logs(
            app.clone(),
            run_id.clone(),
            project.id.clone(),
            "stdout",
            stdout,
        );
    }
    if let Some(stderr) = child.stderr.take() {
        pipe_logs(
            app.clone(),
            run_id.clone(),
            project.id.clone(),
            "stderr",
            stderr,
        );
    }

    state
        .processes
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .insert(
            run_id.clone(),
            ManagedProcess {
                child,
                #[cfg(target_os = "windows")]
                job,
            },
        );
    let status = RuntimeStatus {
        run_id: run_id.clone(),
        project_id: project.id.clone(),
        task_id: task.id.clone(),
        task_name: task.name.clone(),
        mode: task.mode.clone(),
        state: "running".into(),
        pid: Some(pid),
        started_at: Some(started_at),
        exit_code: None,
    };
    emit_log(
        app,
        &run_id,
        &project.id,
        "system",
        format!("已启动「{}」· PID {pid}", task.name),
    );
    state
        .runs
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .insert(run_id, status.clone());
    let _ = app.emit("project-status", status.clone());
    Ok(status)
}

#[tauri::command]
async fn run_task(
    app: AppHandle,
    project_id: String,
    task_id: String,
) -> Result<RuntimeStatus, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state = app.state::<AppState>();
        let project = load_project(&app, &project_id)?;
        let task = project
            .tasks
            .iter()
            .find(|task| task.id == task_id)
            .cloned()
            .ok_or_else(|| "未找到项目任务".to_owned())?;
        start_task_inner(&app, state.inner(), &project, task)
    })
    .await
    .map_err(|error| format!("执行项目任务失败：{error}"))?
}

#[tauri::command]
async fn run_temporary_command(
    app: AppHandle,
    project_id: String,
    command: String,
) -> Result<RuntimeStatus, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let project = load_project(&app, &project_id)?;
        let command = command.trim().to_owned();
        if command.is_empty() {
            return Err("临时命令不能为空".into());
        }
        let task = ProjectTask {
            id: format!("temporary-{}", Uuid::new_v4()),
            name: "临时命令".into(),
            command,
            mode: "once".into(),
        };
        let state = app.state::<AppState>();
        start_task_inner(&app, state.inner(), &project, task)
    })
    .await
    .map_err(|error| format!("执行临时命令失败：{error}"))?
}

fn wait_for_child_exit(child: &mut Child, timeout: Duration) -> Result<Option<ExitStatus>, String> {
    let deadline = Instant::now() + timeout;
    loop {
        if let Some(status) = child
            .try_wait()
            .map_err(|error| format!("读取进程退出状态失败：{error}"))?
        {
            return Ok(Some(status));
        }
        if Instant::now() >= deadline {
            return Ok(None);
        }
        thread::sleep(Duration::from_millis(40));
    }
}

#[cfg(target_os = "windows")]
fn terminate_process_tree(process: &ManagedProcess) -> Result<(), String> {
    process.job.terminate()
}

#[cfg(not(target_os = "windows"))]
fn terminate_process_tree(process: &ManagedProcess) -> Result<(), String> {
    let group = format!("-{}", process.pid);
    let _ = Command::new("kill").args(["-TERM", &group]).status();
    thread::sleep(Duration::from_millis(350));
    let _ = Command::new("kill").args(["-KILL", &group]).status();
    Ok(())
}

fn stop_run_inner(
    app: &AppHandle,
    state: &AppState,
    run_id: &str,
) -> Result<RuntimeStatus, String> {
    let mut status = state
        .runs
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .get(run_id)
        .cloned()
        .ok_or_else(|| "未找到运行实例".to_owned())?;
    status.state = "stopping".into();
    state
        .runs
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .insert(run_id.to_owned(), status.clone());
    let _ = app.emit("project-status", status.clone());

    let process = state
        .processes
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .remove(run_id);

    let Some(mut process) = process else {
        status.state = "stopped".into();
        status.pid = None;
        state
            .runs
            .lock()
            .map_err(|_| "进程状态锁已损坏")?
            .insert(run_id.to_owned(), status.clone());
        let _ = app.emit("project-status", status.clone());
        return Ok(status);
    };

    let mut termination_messages = Vec::new();
    let mut exit_status = match process.child.try_wait() {
        Ok(status) => status,
        Err(error) => {
            termination_messages.push(format!("读取进程状态失败：{error}"));
            None
        }
    };
    if exit_status.is_none() {
        if let Err(error) = terminate_process_tree(&process) {
            termination_messages.push(error);
        }
        exit_status = wait_for_child_exit(&mut process.child, Duration::from_secs(3))
            .unwrap_or_else(|error| {
                termination_messages.push(error);
                None
            });
    }
    if exit_status.is_none() {
        if let Err(error) = process.child.kill() {
            termination_messages.push(format!("强制结束主进程失败：{error}"));
        }
        exit_status = wait_for_child_exit(&mut process.child, Duration::from_secs(1))
            .unwrap_or_else(|error| {
                termination_messages.push(error);
                None
            });
    }
    let exit_code = exit_status.and_then(|status| status.code());
    status.state = "stopped".into();
    status.pid = None;
    status.exit_code = exit_code;
    state
        .runs
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .insert(run_id.to_owned(), status.clone());
    for message in termination_messages {
        emit_log(
            app,
            run_id,
            &status.project_id,
            "system",
            format!("回收提示：{message}"),
        );
    }
    emit_log(
        app,
        run_id,
        &status.project_id,
        "system",
        "任务已停止，子进程已回收",
    );
    let _ = app.emit("project-status", status.clone());
    Ok(status)
}

#[tauri::command]
async fn stop_run(app: AppHandle, run_id: String) -> Result<RuntimeStatus, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state = app.state::<AppState>();
        stop_run_inner(&app, state.inner(), &run_id)
    })
    .await
    .map_err(|error| format!("停止任务后台操作失败：{error}"))?
}

#[tauri::command]
fn dismiss_run(state: State<'_, AppState>, run_id: String) -> Result<(), String> {
    let mut runs = state.runs.lock().map_err(|_| "进程状态锁已损坏")?;
    let status = runs
        .get(&run_id)
        .ok_or_else(|| "未找到运行实例".to_owned())?;
    if matches!(status.state.as_str(), "starting" | "running" | "stopping") {
        return Err("运行中的任务不能移除记录".into());
    }
    runs.remove(&run_id);
    drop(runs);
    state
        .exit_codes
        .lock()
        .map_err(|_| "退出状态锁已损坏")?
        .remove(&run_id);
    Ok(())
}

#[tauri::command]
fn dismiss_inactive_runs(state: State<'_, AppState>) -> Result<Vec<String>, String> {
    let mut runs = state.runs.lock().map_err(|_| "进程状态锁已损坏")?;
    let removed_run_ids = runs
        .iter()
        .filter(|(_, status)| !matches!(status.state.as_str(), "starting" | "running" | "stopping"))
        .map(|(run_id, _)| run_id.clone())
        .collect::<Vec<_>>();
    runs.retain(|run_id, _| !removed_run_ids.contains(run_id));
    drop(runs);

    let mut exit_codes = state.exit_codes.lock().map_err(|_| "退出状态锁已损坏")?;
    for run_id in &removed_run_ids {
        exit_codes.remove(run_id);
    }
    Ok(removed_run_ids)
}

#[tauri::command]
fn list_runtime_status(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<Vec<RuntimeStatus>, String> {
    let mut processes = state.processes.lock().map_err(|_| "进程状态锁已损坏")?;
    let mut exited = Vec::new();

    for (run_id, process) in processes.iter_mut() {
        if let Some(status) = process
            .child
            .try_wait()
            .map_err(|error| error.to_string())?
        {
            exited.push((run_id.clone(), status.code()));
        }
    }
    for (run_id, exit_code) in &exited {
        processes.remove(run_id);
        let mut runs = state.runs.lock().map_err(|_| "进程状态锁已损坏")?;
        let Some(status) = runs.get_mut(run_id) else {
            continue;
        };
        status.state = if status.mode == "once" {
            if exit_code == &Some(0) {
                "succeeded"
            } else {
                "failed"
            }
        } else if exit_code == &Some(0) {
            "stopped"
        } else {
            "failed"
        }
        .into();
        status.pid = None;
        status.exit_code = *exit_code;
        let status = status.clone();
        drop(runs);
        emit_log(
            &app,
            run_id,
            &status.project_id,
            "system",
            format!(
                "任务已结束 · code {}",
                exit_code.map_or_else(|| "-".into(), |code| code.to_string())
            ),
        );
        let _ = app.emit("project-status", status);
    }

    let exit_codes = state.exit_codes.lock().map_err(|_| "退出状态锁已损坏")?;
    let mut statuses = state
        .runs
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .values()
        .cloned()
        .collect::<Vec<_>>();
    let _ = &exit_codes;
    statuses.sort_by_key(|status| std::cmp::Reverse(status.started_at.unwrap_or_default()));
    Ok(statuses)
    /*
        .into_iter()
        .map(|project| {
            if let Some(process) = processes.get(&project.id) {
                RuntimeStatus {
                    project_id: project.id,
                    state: "running".into(),
                    pid: Some(process.pid),
                    started_at: Some(process.started_at),
                    exit_code: None,
                }
            } else {
                RuntimeStatus::stopped(
                    project.id.clone(),
                    exit_codes.get(&project.id).copied().flatten(),
                )
            }
        })
        .collect())
    */
}

#[tauri::command]
fn open_in_vscode(app: AppHandle, directory: String) -> Result<(), String> {
    if !Path::new(&directory).is_dir() {
        return Err("项目目录不存在".into());
    }

    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        Command::new("cmd.exe")
            .args(["/D", "/C", "code", "."])
            .current_dir(&directory)
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .map_err(|error| format!("无法打开 VS Code：{error}"))?;
    }
    #[cfg(not(target_os = "windows"))]
    Command::new("code")
        .arg(".")
        .current_dir(&directory)
        .spawn()
        .map_err(|error| format!("无法打开 VS Code：{error}"))?;

    emit_log(&app, "app", "app", "system", "已请求 VS Code 打开项目");
    Ok(())
}

#[tauri::command]
fn open_in_file_manager(app: AppHandle, directory: String) -> Result<(), String> {
    if !Path::new(&directory).is_dir() {
        return Err("项目目录不存在".into());
    }

    open_directory_in_file_manager(Path::new(&directory))?;
    emit_log(&app, "app", "app", "system", "已请求文件管理器打开项目");
    Ok(())
}

fn open_directory_in_file_manager(directory: &Path) -> Result<(), String> {
    if !directory.is_dir() {
        return Err("目录不存在".into());
    }

    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        Command::new("explorer.exe")
            .arg(directory)
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .map_err(|error| format!("无法打开文件管理器：{error}"))?;
    }
    #[cfg(target_os = "macos")]
    Command::new("open")
        .arg(directory)
        .spawn()
        .map_err(|error| format!("无法打开文件管理器：{error}"))?;
    #[cfg(all(unix, not(target_os = "macos")))]
    Command::new("xdg-open")
        .arg(directory)
        .spawn()
        .map_err(|error| format!("无法打开文件管理器：{error}"))?;

    Ok(())
}

#[tauri::command]
fn open_project_config_directory(app: AppHandle) -> Result<(), String> {
    let directory = app
        .path()
        .app_config_dir()
        .map_err(|error| format!("无法定位项目配置目录：{error}"))?;
    fs::create_dir_all(&directory).map_err(|error| format!("无法创建项目配置目录：{error}"))?;
    open_directory_in_file_manager(&directory)
}

#[tauri::command]
fn open_external_url(url: String) -> Result<(), String> {
    let url = url.trim();
    if url.len() > 8_192
        || url.chars().any(char::is_control)
        || !(url.starts_with("http://") || url.starts_with("https://"))
    {
        return Err("仅允许打开有效的 HTTP 或 HTTPS 链接".into());
    }

    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        Command::new("rundll32.exe")
            .arg("url.dll,FileProtocolHandler")
            .arg(url)
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .map_err(|error| format!("无法使用默认浏览器打开链接：{error}"))?;
    }
    #[cfg(target_os = "macos")]
    Command::new("open")
        .arg(url)
        .spawn()
        .map_err(|error| format!("无法使用默认浏览器打开链接：{error}"))?;
    #[cfg(all(unix, not(target_os = "macos")))]
    Command::new("xdg-open")
        .arg(url)
        .spawn()
        .map_err(|error| format!("无法使用默认浏览器打开链接：{error}"))?;
    Ok(())
}

#[tauri::command]
fn get_autostart_enabled(app: AppHandle) -> Result<bool, String> {
    app.autolaunch()
        .is_enabled()
        .map_err(|error| format!("无法读取开机启动状态：{error}"))
}

#[tauri::command]
fn set_autostart_enabled(app: AppHandle, enabled: bool) -> Result<bool, String> {
    let manager = app.autolaunch();
    if enabled {
        manager
            .enable()
            .map_err(|error| format!("无法启用开机启动：{error}"))?;
    } else {
        manager
            .disable()
            .map_err(|error| format!("无法关闭开机启动：{error}"))?;
    }
    Ok(enabled)
}

#[tauri::command]
fn set_resize_paint_color(red: u8, green: u8, blue: u8) {
    window_resize_guard::update_color(red, green, blue);
}

fn notification_window_coordinates(
    position: &str,
    work_x: i32,
    work_y: i32,
    work_width: u32,
    work_height: u32,
    scale_factor: f64,
    window_height: f64,
) -> Result<(i32, i32), String> {
    let window_width = (notification_window_width() * scale_factor).round() as i32;
    let window_height = (window_height * scale_factor).round() as i32;
    let available_width = i32::try_from(work_width).unwrap_or(i32::MAX);
    let available_height = i32::try_from(work_height).unwrap_or(i32::MAX);
    let left = work_x;
    let center = work_x.saturating_add((available_width - window_width).max(0) / 2);
    let right = work_x.saturating_add((available_width - window_width).max(0));
    let top = work_y;
    let bottom = work_y.saturating_add((available_height - window_height).max(0));

    match position {
        "top-left" => Ok((left, top)),
        "top-center" => Ok((center, top)),
        "top-right" => Ok((right, top)),
        "bottom-left" => Ok((left, bottom)),
        "bottom-center" => Ok((center, bottom)),
        "bottom-right" => Ok((right, bottom)),
        _ => Err("通知位置无效".into()),
    }
}

fn notification_window_width() -> f64 {
    NOTIFICATION_WINDOW_WIDTH + NOTIFICATION_WINDOW_MARGIN
}

fn notification_window_height(count: u8) -> f64 {
    NOTIFICATION_WINDOW_HEIGHT
        + f64::from(count.saturating_sub(1)) * (NOTIFICATION_CARD_HEIGHT + NOTIFICATION_STACK_GAP)
        + NOTIFICATION_WINDOW_MARGIN
}

fn build_notification_window(
    app: &AppHandle,
    theme: &str,
    position: &str,
    prewarmed: bool,
) -> Result<WebviewWindow, String> {
    let notification_config = serde_json::json!({
        "theme": theme,
        "position": position,
        "prewarmed": prewarmed,
    });
    let initialization_script =
        format!("window.__RUNVOKE_NOTIFICATION__ = {};", notification_config);
    let window = WebviewWindowBuilder::new(
        app,
        NOTIFICATION_WINDOW_LABEL,
        WebviewUrl::App("index.html".into()),
    )
    .initialization_script(initialization_script)
    .title("Runvoke notification")
    .inner_size(
        notification_window_width(),
        notification_window_height(MAX_VISIBLE_NOTIFICATIONS),
    )
    .resizable(false)
    .decorations(false)
    .transparent(true)
    .shadow(false)
    .always_on_top(true)
    .skip_taskbar(true)
    .focusable(false)
    .visible(false)
    .build()
    .map_err(|error| format!("无法创建测试通知：{error}"))?;
    window_resize_guard::configure_notification_window(&window)?;
    Ok(window)
}

fn ensure_notification_window(window: &WebviewWindow) -> Result<(), String> {
    if window.label() != NOTIFICATION_WINDOW_LABEL {
        return Err("该操作仅允许通知窗口调用".into());
    }
    Ok(())
}

fn show_notification_inner(
    app: &AppHandle,
    state: &AppState,
    position: String,
    theme: String,
    stacking_enabled: bool,
    tone: String,
    title: String,
    message: String,
    meta: String,
    dedupe_key: String,
) -> Result<(), String> {
    let notification_id = Uuid::new_v4().to_string();
    if theme != "light" && theme != "dark" {
        return Err("通知主题无效".into());
    }
    if !matches!(
        position.as_str(),
        "top-left" | "top-center" | "top-right" | "bottom-left" | "bottom-center" | "bottom-right"
    ) {
        return Err("通知位置无效".into());
    }
    let main_window = app
        .get_webview_window("main")
        .ok_or_else(|| "主窗口不可用".to_owned())?;
    let monitor = main_window
        .current_monitor()
        .map_err(|error| format!("无法读取当前显示器：{error}"))?
        .or(main_window
            .primary_monitor()
            .map_err(|error| format!("无法读取主显示器：{error}"))?)
        .ok_or_else(|| "没有可用的显示器".to_owned())?;
    let work_area = monitor.work_area();
    let (x, y) = notification_window_coordinates(
        &position,
        work_area.position.x,
        work_area.position.y,
        work_area.size.width,
        work_area.size.height,
        monitor.scale_factor(),
        notification_window_height(MAX_VISIBLE_NOTIFICATIONS),
    )?;

    let notification = match app.get_webview_window(NOTIFICATION_WINDOW_LABEL) {
        Some(window) => window,
        None => {
            state
                .notification_bridge
                .lock()
                .map_err(|_| "通知窗口状态锁已损坏")?
                .ready = false;
            build_notification_window(&app, &theme, &position, false)?
        }
    };
    notification
        .set_position(PhysicalPosition::new(x, y))
        .map_err(|error| format!("无法定位测试通知：{error}"))?;
    let notification_config = NotificationConfig {
        id: notification_id.clone(),
        theme,
        position,
        stacking_enabled,
        tone,
        title,
        message,
        meta,
        dedupe_key,
    };
    let mut notification_bridge = state
        .notification_bridge
        .lock()
        .map_err(|_| "通知窗口状态锁已损坏")?;
    notification_bridge
        .pending
        .push(notification_config.clone());
    if notification_bridge.pending.len() > usize::from(MAX_VISIBLE_NOTIFICATIONS) {
        notification_bridge.pending.remove(0);
    }
    let notification_ready = notification_bridge.ready;
    drop(notification_bridge);
    if notification_ready {
        if notification
            .emit("notification-config", notification_config)
            .is_err()
        {
            state
                .notification_bridge
                .lock()
                .map_err(|_| "通知窗口状态锁已损坏")?
                .ready = false;
        }
    }
    Ok(())
}

fn validate_notification_text(
    value: String,
    field: &str,
    maximum_length: usize,
) -> Result<String, String> {
    let value = value.trim().to_owned();
    if value.chars().count() > maximum_length {
        return Err(format!("通知{field}过长"));
    }
    Ok(value)
}

#[tauri::command]
async fn show_desktop_notification(
    app: AppHandle,
    state: State<'_, AppState>,
    position: String,
    theme: String,
    stacking_enabled: bool,
    tone: String,
    title: String,
    message: String,
    meta: String,
    dedupe_key: String,
) -> Result<(), String> {
    if !matches!(tone.as_str(), "success" | "error") {
        return Err("通知类型无效".into());
    }
    let title = validate_notification_text(title, "标题", 120)?;
    if title.is_empty() {
        return Err("通知标题不能为空".into());
    }
    let message = validate_notification_text(message, "内容", 280)?;
    let meta = validate_notification_text(meta, "附加信息", 120)?;
    let dedupe_key = validate_notification_text(dedupe_key, "去重标识", 120)?;
    show_notification_inner(
        &app,
        state.inner(),
        position,
        theme,
        stacking_enabled,
        tone,
        title,
        message,
        meta,
        dedupe_key,
    )
}

#[tauri::command]
async fn show_test_notification(
    app: AppHandle,
    state: State<'_, AppState>,
    position: String,
    theme: String,
    stacking_enabled: bool,
) -> Result<(), String> {
    show_notification_inner(
        &app,
        state.inner(),
        position,
        theme,
        stacking_enabled,
        "success".into(),
        "测试通知已送达".into(),
        "这是一条由独立桌面窗口显示的自定义通知。".into(),
        "系统通知测试".into(),
        String::new(),
    )
}

#[tauri::command]
fn notification_window_ready(
    window: WebviewWindow,
    state: State<'_, AppState>,
) -> Result<Vec<NotificationConfig>, String> {
    ensure_notification_window(&window)?;
    let mut notification_bridge = state
        .notification_bridge
        .lock()
        .map_err(|_| "通知窗口状态锁已损坏")?;
    notification_bridge.ready = true;
    Ok(notification_bridge.pending.clone())
}

#[tauri::command]
fn notification_received(
    window: WebviewWindow,
    state: State<'_, AppState>,
    id: String,
) -> Result<(), String> {
    ensure_notification_window(&window)?;
    let mut notification_bridge = state
        .notification_bridge
        .lock()
        .map_err(|_| "通知窗口状态锁已损坏")?;
    notification_bridge
        .pending
        .retain(|notification| notification.id != id);
    Ok(())
}

#[tauri::command]
fn set_notification_hit_regions(
    window: WebviewWindow,
    regions: Vec<NotificationHitRegion>,
) -> Result<(), String> {
    ensure_notification_window(&window)?;
    if regions.len() > usize::from(MAX_VISIBLE_NOTIFICATIONS) {
        return Err("通知命中区域数量无效".into());
    }
    let scale = window
        .scale_factor()
        .map_err(|error| format!("无法读取通知窗口缩放比例：{error}"))?;
    let regions = regions
        .into_iter()
        .filter(|region| {
            region.x.is_finite()
                && region.y.is_finite()
                && region.width.is_finite()
                && region.height.is_finite()
                && region.width > 0.0
                && region.height > 0.0
        })
        .map(|region| {
            (
                (region.x * scale).floor() as i32,
                (region.y * scale).floor() as i32,
                ((region.x + region.width) * scale).ceil() as i32,
                ((region.y + region.height) * scale).ceil() as i32,
            )
        })
        .collect();
    window_resize_guard::set_notification_window_regions(&window, regions)
}

#[tauri::command]
fn redraw_notification_window(window: WebviewWindow) -> Result<(), String> {
    ensure_notification_window(&window)?;
    window_resize_guard::show_notification_window(&window)?;
    window_resize_guard::redraw(&window)?;
    Ok(())
}

#[tauri::command]
async fn resize_notification_window(
    app: AppHandle,
    position: String,
    count: u8,
) -> Result<(), String> {
    if count == 0 || count > MAX_VISIBLE_NOTIFICATIONS {
        return Err("通知数量无效".into());
    }
    let main_window = app
        .get_webview_window("main")
        .ok_or_else(|| "主窗口不可用".to_owned())?;
    let monitor = main_window
        .current_monitor()
        .map_err(|error| format!("无法读取当前显示器：{error}"))?
        .or(main_window
            .primary_monitor()
            .map_err(|error| format!("无法读取主显示器：{error}"))?)
        .ok_or_else(|| "没有可用的显示器".to_owned())?;
    let height = notification_window_height(count);
    let work_area = monitor.work_area();
    let (x, y) = notification_window_coordinates(
        &position,
        work_area.position.x,
        work_area.position.y,
        work_area.size.width,
        work_area.size.height,
        monitor.scale_factor(),
        height,
    )?;
    let notification = app
        .get_webview_window(NOTIFICATION_WINDOW_LABEL)
        .ok_or_else(|| "通知窗口不可用".to_owned())?;
    #[cfg(windows)]
    window_resize_guard::set_outer_bounds(
        &notification,
        x,
        y,
        (notification_window_width() * monitor.scale_factor()).round() as i32,
        (height * monitor.scale_factor()).round() as i32,
    )?;
    #[cfg(not(windows))]
    {
        notification
            .set_size(LogicalSize::new(notification_window_width(), height))
            .map_err(|error| format!("无法调整测试通知：{error}"))?;
        notification
            .set_position(PhysicalPosition::new(x, y))
            .map_err(|error| format!("无法定位测试通知：{error}"))?;
    }
    window_resize_guard::redraw(&notification)?;
    Ok(())
}

#[tauri::command]
fn close_notification_window(window: WebviewWindow) -> Result<(), String> {
    ensure_notification_window(&window)?;
    let region_result = window_resize_guard::set_notification_window_regions(&window, Vec::new());
    let hide_result = window
        .hide()
        .map_err(|error| format!("无法关闭测试通知：{error}"));
    region_result.and(hide_result)
}

fn show_main_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn setup_tray(app: &tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    let show = MenuItemBuilder::with_id("show", "显示主窗口").build(app)?;
    let quit = MenuItemBuilder::with_id("quit", "退出并停止所有项目").build(app)?;
    let menu = MenuBuilder::new(app).items(&[&show, &quit]).build()?;
    let icon = app.default_window_icon().cloned().ok_or_else(|| {
        std::io::Error::new(std::io::ErrorKind::NotFound, "Runvoke 默认图标不可用")
    })?;

    TrayIconBuilder::with_id("main-tray")
        .icon(icon)
        .tooltip("Runvoke")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "show" => show_main_window(app),
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                show_main_window(tray.app_handle());
            }
        })
        .build(app)?;
    Ok(())
}

fn stop_all_processes(state: &AppState) {
    if let Ok(mut processes) = state.processes.lock() {
        for (_, mut process) in processes.drain() {
            let _ = terminate_process_tree(&process);
            if !matches!(
                wait_for_child_exit(&mut process.child, Duration::from_secs(2)),
                Ok(Some(_))
            ) {
                let _ = process.child.kill();
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn project(tasks: Vec<ProjectTask>) -> ProjectConfig {
        ProjectConfig {
            id: "project-1".into(),
            name: "测试项目".into(),
            directory: ".".into(),
            group_id: None,
            command: "pnpm dev".into(),
            env: Vec::new(),
            port: None,
            tasks,
        }
    }

    #[test]
    fn legacy_command_becomes_default_service_task() {
        let mut project = project(Vec::new());
        normalize_project(&mut project);

        assert_eq!(project.tasks.len(), 1);
        assert_eq!(project.tasks[0].name, "开发服务器");
        assert_eq!(project.tasks[0].command, "pnpm dev");
        assert_eq!(project.tasks[0].mode, "service");
    }

    #[test]
    fn task_modes_must_be_service_or_once() {
        let mut project = project(vec![ProjectTask {
            id: "build".into(),
            name: "构建".into(),
            command: "pnpm build".into(),
            mode: "background".into(),
        }]);
        normalize_project(&mut project);

        assert!(validate_project(&project).is_err());
    }

    #[test]
    fn project_accepts_at_most_three_preset_tasks() {
        let project = project(
            (0..4)
                .map(|index| ProjectTask {
                    id: format!("task-{index}"),
                    name: format!("任务 {index}"),
                    command: "pnpm dev".into(),
                    mode: "service".into(),
                })
                .collect(),
        );

        assert!(validate_project(&project).is_err());
    }

    #[test]
    fn git_project_status_uses_runtime_field_names() {
        let value = serde_json::to_value(GitProjectStatus {
            git_branch: Some("develop".into()),
            staged_changes: 2,
            unstaged_changes: 1,
        })
        .unwrap();

        assert_eq!(
            value.get("gitBranch").and_then(|value| value.as_str()),
            Some("develop")
        );
        assert_eq!(
            value.get("stagedChanges").and_then(|value| value.as_u64()),
            Some(2)
        );
        assert_eq!(
            value
                .get("unstagedChanges")
                .and_then(|value| value.as_u64()),
            Some(1)
        );
        assert!(value.get("project").is_none());
    }

    #[test]
    fn counts_staged_and_unstaged_git_changes_by_file() {
        let status = b"M  staged.txt\0 M modified.txt\0MM both.txt\0?? untracked.txt\0R  renamed.txt\0original.txt\0";

        assert_eq!(git_change_counts(status), (3, 3));
    }

    #[test]
    fn reads_project_name_from_toml_sections() {
        let content = "[build-system]\nrequires = []\n\n[project]\nname = \"utility-kit\"\n";
        assert_eq!(
            section_value(content, "[project]", "name"),
            Some("utility-kit".into())
        );
    }

    #[test]
    fn detects_package_name_from_directory_manifest() {
        let directory = std::env::temp_dir().join(format!("runvoke-name-test-{}", Uuid::new_v4()));
        fs::create_dir_all(&directory).unwrap();
        fs::write(
            directory.join("package.json"),
            r#"{ "name": "sample-dashboard" }"#,
        )
        .unwrap();

        let detected = detect_project_name(directory.to_string_lossy().into_owned()).unwrap();
        fs::remove_dir_all(directory).unwrap();

        assert_eq!(detected.name, "sample-dashboard");
        assert_eq!(detected.source, "package.json");
    }

    #[test]
    fn positions_notification_inside_monitor_work_area() {
        assert_eq!(
            notification_window_coordinates(
                "bottom-right",
                0,
                0,
                1920,
                1040,
                1.0,
                notification_window_height(1),
            ),
            Ok((1522, 890))
        );
        assert_eq!(
            notification_window_coordinates(
                "top-center",
                -1920,
                0,
                1920,
                1040,
                1.0,
                notification_window_height(1),
            ),
            Ok((-1159, 0))
        );
        assert!(notification_window_coordinates(
            "center",
            0,
            0,
            1920,
            1040,
            1.0,
            notification_window_height(1),
        )
        .is_err());
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        // Register this first so a second process exits before initializing its window or tray.
        .plugin(tauri_plugin_single_instance::init(
            |app, _arguments, _cwd| {
                show_main_window(app);
            },
        ))
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            Some(vec!["--minimized"]),
        ))
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![
            list_projects,
            get_project_git_status,
            list_project_groups,
            detect_project_name,
            list_vscode_projects,
            list_cursor_projects,
            get_mcp_server_status,
            set_mcp_server_enabled,
            save_project,
            save_project_group,
            set_project_group_collapsed,
            set_project_groups_collapsed,
            delete_project_group,
            move_project,
            reorder_projects,
            delete_project,
            run_task,
            run_temporary_command,
            stop_run,
            dismiss_run,
            dismiss_inactive_runs,
            list_runtime_status,
            open_in_vscode,
            open_in_file_manager,
            open_project_config_directory,
            open_external_url,
            get_autostart_enabled,
            set_autostart_enabled,
            set_resize_paint_color,
            check_preview_update,
            install_preview_update,
            show_desktop_notification,
            show_test_notification,
            notification_window_ready,
            notification_received,
            set_notification_hit_regions,
            redraw_notification_window,
            resize_notification_window,
            close_notification_window,
        ])
        .setup(|app| {
            setup_log_dispatcher(app)?;
            setup_tray(app)?;
            let _ = build_notification_window(app.handle(), "light", "bottom-right", true);
            let app_handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                let mut store = match load_store(&app_handle) {
                    Ok(store) => store,
                    Err(_) => return,
                };
                if !store.mcp.enabled {
                    return;
                }
                if store.mcp.token.is_empty() {
                    store.mcp.token = Uuid::new_v4().to_string();
                    if save_store(&app_handle, &store).is_err() {
                        return;
                    }
                }
                if let Ok(runtime) =
                    mcp_server::start(app_handle.clone(), store.mcp.port, store.mcp.token.clone())
                        .await
                {
                    if let Ok(mut mcp) = app_handle.state::<AppState>().mcp.lock() {
                        mcp.replace(runtime);
                    }
                    if let Ok(status) =
                        mcp_server_status(&app_handle, app_handle.state::<AppState>().inner())
                    {
                        let _ = app_handle.emit("mcp-server-status", status);
                    }
                }
            });
            if let Some(window) = app.get_webview_window("main") {
                window_resize_guard::install(&window, (247, 245, 241))
                    .map_err(std::io::Error::other)?;
                let window_for_event = window.clone();
                window.on_window_event(move |event| {
                    match event {
                        WindowEvent::Resized(_) => {
                            // Yield once so WebView2 can consume its pending child-window resize.
                            std::thread::sleep(Duration::from_nanos(1));
                        }
                        WindowEvent::CloseRequested { api, .. } => {
                            api.prevent_close();
                            let _ = window_for_event.hide();
                        }
                        _ => {}
                    }
                });
                if std::env::args().any(|argument| argument == "--minimized") {
                    let _ = window.hide();
                }
            }
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building Runvoke");

    app.run(|app_handle, event| {
        if let RunEvent::Exit = event {
            if let Ok(mut mcp) = app_handle.state::<AppState>().mcp.lock() {
                if let Some(runtime) = mcp.take() {
                    runtime.stop();
                }
            }
            stop_all_processes(&app_handle.state::<AppState>());
        }
    });
}
