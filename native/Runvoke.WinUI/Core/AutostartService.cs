using Microsoft.Win32;

namespace Runvoke.WinUI.Core;

public sealed class AutostartService
{
    private const string RegistryPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "Runvoke";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        return key?.GetValue(ValueName) is string;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        if (key is null) throw new InvalidOperationException("无法打开 Windows 开机启动配置。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("无法定位 Runvoke 程序路径。");
        key.SetValue(ValueName, $"\"{executable}\"");
    }
}
