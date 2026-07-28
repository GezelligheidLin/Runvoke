use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    fs,
    io::{BufRead, BufReader, Read},
    path::{Path, PathBuf},
    process::{Child, Command, ExitStatus, Stdio},
    sync::Mutex,
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
    command: String,
    #[serde(default)]
    env: Vec<EnvVariable>,
    port: Option<u16>,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct StoreFile {
    #[serde(default)]
    projects: Vec<ProjectConfig>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct RuntimeStatus {
    project_id: String,
    state: String,
    pid: Option<u32>,
    started_at: Option<u64>,
    exit_code: Option<i32>,
}

impl RuntimeStatus {
    fn stopped(project_id: String, exit_code: Option<i32>) -> Self {
        Self {
            project_id,
            state: "stopped".into(),
            pid: None,
            started_at: None,
            exit_code,
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct LogEvent {
    project_id: String,
    stream: String,
    message: String,
    timestamp: u64,
}

struct ManagedProcess {
    child: Child,
    pid: u32,
    started_at: u64,
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
    exit_codes: Mutex<HashMap<String, Option<i32>>>,
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

fn validate_project(project: &ProjectConfig) -> Result<(), String> {
    if project.name.trim().is_empty() {
        return Err("项目名称不能为空".into());
    }
    if project.command.trim().is_empty() {
        return Err("启动命令不能为空".into());
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

#[tauri::command]
fn list_projects(app: AppHandle) -> Result<Vec<ProjectConfig>, String> {
    Ok(load_store(&app)?.projects)
}

#[tauri::command]
fn save_project(app: AppHandle, mut project: ProjectConfig) -> Result<ProjectConfig, String> {
    project.name = project.name.trim().to_owned();
    project.directory = project.directory.trim().to_owned();
    project.command = project.command.trim().to_owned();
    project.env.retain(|item| !item.key.trim().is_empty());
    for item in &mut project.env {
        item.key = item.key.trim().to_owned();
    }
    validate_project(&project)?;

    let mut store = load_store(&app)?;
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

fn delete_project_inner(app: &AppHandle, state: &AppState, project_id: &str) -> Result<(), String> {
    if state
        .processes
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .contains_key(project_id)
    {
        stop_project_inner(app, state, project_id)?;
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

fn emit_log(app: &AppHandle, project_id: &str, stream: &str, message: impl Into<String>) {
    let _ = app.emit(
        "project-log",
        LogEvent {
            project_id: project_id.to_owned(),
            stream: stream.to_owned(),
            message: message.into(),
            timestamp: now_millis(),
        },
    );
}

fn pipe_logs<R: Read + Send + 'static>(
    app: AppHandle,
    project_id: String,
    stream: &'static str,
    reader: R,
) {
    thread::spawn(move || {
        let mut reader = BufReader::new(reader);
        let mut buffer = Vec::new();
        loop {
            buffer.clear();
            match reader.read_until(b'\n', &mut buffer) {
                Ok(0) => break,
                Ok(_) => {
                    while matches!(buffer.last(), Some(b'\n' | b'\r')) {
                        buffer.pop();
                    }
                    emit_log(
                        &app,
                        &project_id,
                        stream,
                        String::from_utf8_lossy(&buffer).into_owned(),
                    );
                }
                Err(error) => {
                    emit_log(
                        &app,
                        &project_id,
                        "system",
                        format!("读取日志失败：{error}"),
                    );
                    break;
                }
            }
        }
    });
}

fn shell_command(project: &ProjectConfig) -> Command {
    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        // Keep the user's PowerShell profile so fnm and other shell-managed tools are available.
        let script = format!("& {{ {} }}", project.command);
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
        command.args(["-lc", &project.command]).process_group(0);
        command
    }
}

fn start_project_inner(
    app: &AppHandle,
    state: &AppState,
    project_id: &str,
) -> Result<RuntimeStatus, String> {
    {
        let mut processes = state.processes.lock().map_err(|_| "进程状态锁已损坏")?;
        if let Some(process) = processes.get_mut(project_id) {
            if process
                .child
                .try_wait()
                .map_err(|error| error.to_string())?
                .is_none()
            {
                return Err("项目已经在运行".into());
            }
            processes.remove(project_id);
        }
    }

    let project = load_store(app)?
        .projects
        .into_iter()
        .find(|project| project.id == project_id)
        .ok_or_else(|| "未找到项目配置".to_owned())?;
    validate_project(&project)?;

    let mut command = shell_command(&project);
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
        pipe_logs(app.clone(), project.id.clone(), "stdout", stdout);
    }
    if let Some(stderr) = child.stderr.take() {
        pipe_logs(app.clone(), project.id.clone(), "stderr", stderr);
    }

    state
        .processes
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .insert(
            project.id.clone(),
            ManagedProcess {
                child,
                pid,
                started_at,
                #[cfg(target_os = "windows")]
                job,
            },
        );
    state
        .exit_codes
        .lock()
        .map_err(|_| "退出状态锁已损坏")?
        .remove(&project.id);

    let status = RuntimeStatus {
        project_id: project.id.clone(),
        state: "running".into(),
        pid: Some(pid),
        started_at: Some(started_at),
        exit_code: None,
    };
    emit_log(
        &app,
        &project.id,
        "system",
        format!("已在后台启动 · PID {pid}"),
    );
    let _ = app.emit("project-status", status.clone());
    Ok(status)
}

#[tauri::command]
async fn start_project(app: AppHandle, project_id: String) -> Result<RuntimeStatus, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state = app.state::<AppState>();
        start_project_inner(&app, state.inner(), &project_id)
    })
    .await
    .map_err(|error| format!("启动项目后台任务失败：{error}"))?
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

fn stop_project_inner(
    app: &AppHandle,
    state: &AppState,
    project_id: &str,
) -> Result<RuntimeStatus, String> {
    let process = state
        .processes
        .lock()
        .map_err(|_| "进程状态锁已损坏")?
        .remove(project_id);

    let Some(mut process) = process else {
        return Ok(RuntimeStatus::stopped(project_id.to_owned(), None));
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
    state
        .exit_codes
        .lock()
        .map_err(|_| "退出状态锁已损坏")?
        .insert(project_id.to_owned(), exit_code);
    let status = RuntimeStatus::stopped(project_id.to_owned(), exit_code);
    for message in termination_messages {
        emit_log(app, project_id, "system", format!("回收提示：{message}"));
    }
    emit_log(app, project_id, "system", "项目已停止，子进程已回收");
    let _ = app.emit("project-status", status.clone());
    Ok(status)
}

#[tauri::command]
async fn stop_project(app: AppHandle, project_id: String) -> Result<RuntimeStatus, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state = app.state::<AppState>();
        stop_project_inner(&app, state.inner(), &project_id)
    })
    .await
    .map_err(|error| format!("停止项目后台任务失败：{error}"))?
}

#[tauri::command]
async fn restart_project(app: AppHandle, project_id: String) -> Result<RuntimeStatus, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let state = app.state::<AppState>();
        let _ = stop_project_inner(&app, state.inner(), &project_id)?;
        start_project_inner(&app, state.inner(), &project_id)
    })
    .await
    .map_err(|error| format!("重启项目后台任务失败：{error}"))?
}

#[tauri::command]
fn list_runtime_status(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<Vec<RuntimeStatus>, String> {
    let projects = load_store(&app)?.projects;
    let mut processes = state.processes.lock().map_err(|_| "进程状态锁已损坏")?;
    let mut exited = Vec::new();

    for (project_id, process) in processes.iter_mut() {
        if let Some(status) = process
            .child
            .try_wait()
            .map_err(|error| error.to_string())?
        {
            exited.push((project_id.clone(), status.code()));
        }
    }
    for (project_id, exit_code) in &exited {
        processes.remove(project_id);
        state
            .exit_codes
            .lock()
            .map_err(|_| "退出状态锁已损坏")?
            .insert(project_id.clone(), *exit_code);
        let status = RuntimeStatus::stopped(project_id.clone(), *exit_code);
        emit_log(
            &app,
            project_id,
            "system",
            format!(
                "进程已退出 · code {}",
                exit_code.map_or_else(|| "-".into(), |code| code.to_string())
            ),
        );
        let _ = app.emit("project-status", status);
    }

    let exit_codes = state.exit_codes.lock().map_err(|_| "退出状态锁已损坏")?;
    Ok(projects
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

    emit_log(&app, "app", "system", "已请求 VS Code 打开项目");
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

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            Some(vec!["--minimized"]),
        ))
        .manage(AppState::default())
        .invoke_handler(tauri::generate_handler![
            list_projects,
            save_project,
            delete_project,
            start_project,
            stop_project,
            restart_project,
            list_runtime_status,
            open_in_vscode,
            get_autostart_enabled,
            set_autostart_enabled,
        ])
        .setup(|app| {
            setup_tray(app)?;
            if let Some(window) = app.get_webview_window("main") {
                let window_for_event = window.clone();
                window.on_window_event(move |event| {
                    if let WindowEvent::CloseRequested { api, .. } = event {
                        api.prevent_close();
                        let _ = window_for_event.hide();
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
