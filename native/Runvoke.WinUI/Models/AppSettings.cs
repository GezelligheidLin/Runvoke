namespace Runvoke.WinUI.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "light";
    public bool GithubLinkVisible { get; set; } = true;
    public string LogLinkAction { get; set; } = "open";
    public bool AutostartEnabled { get; set; }
    public bool PreviewUpdatesEnabled { get; set; }
    public bool McpEnabled { get; set; }
    public ushort McpPort { get; set; } = 38465;
    public string McpToken { get; set; } = string.Empty;
    public string NotificationPosition { get; set; } = "bottom-right";
    public bool NotificationStacking { get; set; }
    public bool ReduceMotion { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool UngroupedCollapsed { get; set; }
    public bool HasShownImportPrompt { get; set; }
    public string LastOpenedVersion { get; set; } = string.Empty;
    public double WorkspaceSidebarWidth { get; set; } = 280;
    public double SettingsSidebarWidth { get; set; } = 260;
}
