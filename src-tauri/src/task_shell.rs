//! Resolve an installed PowerShell without requiring PowerShell 7 or a fresh PATH.
#[cfg(windows)]
use std::path::PathBuf;

#[cfg(windows)]
fn select_shell(candidates: impl IntoIterator<Item = PathBuf>) -> Result<PathBuf, String> {
    candidates.into_iter().find(|path| path.is_file()).ok_or_else(|| {
        "启动失败：未找到 PowerShell 7 或 Windows PowerShell，请检查系统 PowerShell 安装及 PATH".into()
    })
}

#[cfg(windows)]
pub(crate) fn resolve_powershell() -> Result<PathBuf, String> {
    let paths: Vec<_> = std::env::var_os("PATH")
        .map(|value| {
            std::env::split_paths(&value)
                .filter(|path| path.is_absolute())
                .collect()
        })
        .unwrap_or_default();
    let mut candidates: Vec<_> = paths.iter().map(|path| path.join("pwsh.exe")).collect();
    for key in ["ProgramW6432", "ProgramFiles", "LOCALAPPDATA"] {
        if let Some(root) = std::env::var_os(key) {
            let root = PathBuf::from(root);
            candidates.push(root.join("PowerShell/7/pwsh.exe"));
            if key == "LOCALAPPDATA" {
                candidates.push(root.join("Programs/PowerShell/7/pwsh.exe"));
                // Microsoft Store exposes a stable execution alias outside its versioned package.
                candidates.push(root.join("Microsoft/WindowsApps/pwsh.exe"));
            }
        }
    }
    // Use the system location before PATH so an absent/stale PATH still works.
    if let Some(root) = std::env::var_os("SystemRoot") {
        candidates.push(PathBuf::from(root).join("System32/WindowsPowerShell/v1.0/powershell.exe"));
    }
    candidates.extend(paths.iter().map(|path| path.join("powershell.exe")));
    select_shell(candidates)
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;

    #[test]
    fn missing_pwsh_falls_back_to_windows_powershell() {
        let fallback = PathBuf::from(std::env::var_os("SystemRoot").unwrap())
            .join("System32/WindowsPowerShell/v1.0/powershell.exe");
        assert_eq!(
            select_shell([
                std::env::temp_dir()
                    .join(format!("runvoke-missing-shell-{}", uuid::Uuid::new_v4()))
                    .join("pwsh.exe"),
                fallback.clone()
            ])
            .unwrap(),
            fallback
        );
        assert!(select_shell(Vec::new()).unwrap_err().contains("PowerShell"));
    }

    #[test]
    fn resolved_shell_executes_commands_and_preserves_failure_exit_code() {
        for (script, code) in [("Write-Output 'runvoke-shell-ok 中文'", 0), ("exit 7", 7)] {
            let output = crate::shell_command(script).unwrap().output().unwrap();
            assert_eq!(output.status.code(), Some(code));
            if code == 0 {
                assert!(String::from_utf8_lossy(&output.stdout).contains("runvoke-shell-ok 中文"));
            }
        }
    }

    #[test]
    fn resolved_shell_can_be_stopped_through_existing_job() {
        let mut child = crate::shell_command("Start-Sleep -Seconds 60")
            .unwrap()
            .stdout(std::process::Stdio::piped())
            .spawn()
            .unwrap();
        let job = crate::JobHandle::attach(&child).unwrap();
        job.terminate().unwrap();
        assert!(!child.wait().unwrap().success());
    }
}
