namespace Runvoke.WinUI.Core;

internal static class WindowsShellResolver
{
    public static string ResolvePowerShell()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "pwsh.exe")));

        foreach (var variable in new[] { "ProgramW6432", "ProgramFiles", "LOCALAPPDATA" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root)) continue;
            candidates.Add(Path.Combine(root, "PowerShell", "7", "pwsh.exe"));
            if (variable == "LOCALAPPDATA")
            {
                candidates.Add(Path.Combine(root, "Programs", "PowerShell", "7", "pwsh.exe"));
                candidates.Add(Path.Combine(root, "Microsoft", "WindowsApps", "pwsh.exe"));
            }
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
            candidates.Add(Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));

        if (!string.IsNullOrWhiteSpace(path))
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "powershell.exe")));

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("启动失败：未找到 PowerShell 7 或 Windows PowerShell。");
    }

    public static string BuildScript(string commandLine) => """
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding
$OutputEncoding = [Console]::OutputEncoding
foreach ($runvokeProfile in @($PROFILE.AllUsersAllHosts, $PROFILE.AllUsersCurrentHost, $PROFILE.CurrentUserAllHosts, $PROFILE.CurrentUserCurrentHost)) {
    if (Test-Path -LiteralPath $runvokeProfile) { . $runvokeProfile }
}
$global:LASTEXITCODE = 0
& {
""" + commandLine + "\n" + """
$runvokeTaskSucceeded = $?
if (-not $runvokeTaskSucceeded) {
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    exit 1
}
exit 0
}
""";
}
