use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    fs,
    io::{BufReader, Read},
    path::{Path, PathBuf},
    process::{Child, Command, ExitStatus, Stdio},
    sync::{
        mpsc::{sync_channel, RecvTimeoutError, SyncSender},
        Mutex, OnceLock,
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use tauri::{
    menu::{MenuBuilder, MenuItemBuilder},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    AppHandle, Emitter, Manager, RunEvent, State, WindowEvent,
};
use tauri_plugin_autostart::{MacosLauncher, ManagerExt};
use uuid::Uuid;

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

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct StoreFile {
    #[serde(default)]
    projects: Vec<ProjectConfig>,
    #[serde(default)]
    groups: Vec<ProjectGroup>,
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
struct AppState {
    processes: Mutex<HashMap<String, ManagedProcess>>,
    runs: Mutex<HashMap<String, RuntimeStatus>>,
    exit_codes: Mutex<HashMap<String, Option<i32>>>,
    log_sender: OnceLock<SyncSender<LogEvent>>,
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
fn list_projects(app: AppHandle) -> Result<Vec<ProjectConfig>, String> {
    let mut store = load_store(&app)?;
    for project in &mut store.projects {
        normalize_project(project);
    }
    Ok(store.projects)
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
        } else {
            "stopped"
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

    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        Command::new("explorer.exe")
            .arg(&directory)
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .map_err(|error| format!("无法打开文件管理器：{error}"))?;
    }
    #[cfg(target_os = "macos")]
    Command::new("open")
        .arg(&directory)
        .spawn()
        .map_err(|error| format!("无法打开文件管理器：{error}"))?;
    #[cfg(all(unix, not(target_os = "macos")))]
    Command::new("xdg-open")
        .arg(&directory)
        .spawn()
        .map_err(|error| format!("无法打开文件管理器：{error}"))?;

    emit_log(&app, "app", "app", "system", "已请求文件管理器打开项目");
    Ok(())
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
            list_project_groups,
            detect_project_name,
            save_project,
            save_project_group,
            set_project_group_collapsed,
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
            open_external_url,
            get_autostart_enabled,
            set_autostart_enabled,
            set_resize_paint_color,
        ])
        .setup(|app| {
            setup_log_dispatcher(app)?;
            setup_tray(app)?;
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
            stop_all_processes(&app_handle.state::<AppState>());
        }
    });
}
