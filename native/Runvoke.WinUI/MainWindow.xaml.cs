using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Runvoke.WinUI.Core;
using Runvoke.WinUI.Models;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Runvoke.WinUI;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly IRunvokeCore _core = new NativeRunvokeCore(new WorkspaceStoreService());
    private readonly SettingsStoreService _settingsStore = new();
    private readonly AutostartService _autostart = new();
    private readonly UpdateService _updates = new();
    private readonly LogLinkService _logLinks = new();
    private readonly NotificationService _notifications = new();
    private NotificationWindow? _notificationWindow;
    private readonly TrayService _tray;
    private readonly McpService _mcp;
    private AppSettings _settings = new();
    private ProjectConfig? _selectedProject;
    private ProjectGroup? _selectedGroup;
    private RuntimeStatus? _selectedRun;
    private GitProjectStatus? _gitStatus;
    private int _gitStatusRequest;
    private string _logFilter = "all";
    private string _projectSearch = string.Empty;
    private bool _windowActive = true;
    private bool _loadingSettings;
    private UpdateInfo? _pendingUpdate;
    private DispatcherQueueTimer? _updateTimer;
    private readonly HashSet<string> _userStoppedRuns = [];
    private string? _draggedProjectId;
    private List<ProjectConfig>? _projectOrderBeforeDrag;
    private bool _resizingWorkspaceSidebar;
    private double _resizeStartWidth;
    private double _resizeStartX;
    private bool _resizingSettingsSidebar;
    private double _settingsResizeStartWidth;
    private double _settingsResizeStartX;
    private bool _allowClose;
    private AppWindow? _mainAppWindow;

    public ObservableCollection<ProjectConfig> Projects { get; } = [];
    public ObservableCollection<ProjectConfig> VisibleProjects { get; } = [];
    public ObservableCollection<ProjectGroup> Groups { get; } = [];
    public ObservableCollection<ProjectTask> SelectedTasks { get; } = [];
    public ObservableCollection<RuntimeStatus> Runs { get; } = [];
    public ObservableCollection<RuntimeStatus> VisibleRuns { get; } = [];
    public ObservableCollection<LogEvent> Logs { get; } = [];
    public ObservableCollection<LogEvent> VisibleLogs { get; } = [];
    public ObservableCollection<LogLineView> VisibleLogLines { get; } = [];
    public ObservableCollection<OpenSourceConfig> OpenSources { get; } = [];

    public ProjectConfig? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (ReferenceEquals(_selectedProject, value)) return;
            _selectedProject = value;
            SelectedTasks.Clear();
            if (value is not null)
                foreach (var task in value.Tasks.Take(3)) SelectedTasks.Add(task);
            RefreshTaskRuntimeLabels();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProject)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProjectName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStart)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEdit)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTasksEmptyVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTasksEmptyText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectDirectoryText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectPortText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitStatusText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitBranchText)));
            RefreshVisibleRuns();
            SelectedRun = Runs.FirstOrDefault(run => run.ProjectId == value?.Id && run.State is ("starting" or "running" or "stopping"))
                ?? Runs.FirstOrDefault(run => run.ProjectId == value?.Id);
            _ = LoadGitStatusAsync(value);
        }
    }

    public string SelectedProjectName => SelectedProject?.Name ?? "未选择项目";

    public string AppVersionText => $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
    public Visibility SelectedTasksEmptyVisibility => SelectedTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string SelectedTasksEmptyText => SelectedProject is null
        ? "从左侧选择一个项目开始管理任务。"
        : "当前项目还没有可运行的任务，请编辑项目后添加。";
    public Visibility LogEmptyVisibility => VisibleLogLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string LogEmptyText => SelectedRun is null
        ? "从上方选择一个运行实例开始查看日志。"
        : "等待任务输出。标准输出和错误输出会显示在这里。";

    public ProjectGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (ReferenceEquals(_selectedGroup, value)) return;
            _selectedGroup = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedGroup)));
        }
    }

    public RuntimeStatus? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (ReferenceEquals(_selectedRun, value)) return;
            _selectedRun = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRun)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStop)));
            Logs.Clear();
            if (value is not null)
                foreach (var entry in _core.GetRecentLogs(value.RunId, 500)) Logs.Add(entry);
            RefreshVisibleLogs();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogEmptyVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogEmptyText)));
            ScrollLogsToEnd();
        }
    }

    public bool CanStart => SelectedProject?.Tasks.Count > 0;
    public bool CanEdit => SelectedProject is not null;
    public bool CanStop => SelectedRun is { State: "running" };
    public int ActiveRunCount => Runs.Count(run => run.State is "starting" or "running" or "stopping");
    public string ActiveRunSummary => ActiveRunCount > 0 ? $"运行中：{ActiveRunCount}" : "当前没有运行任务";
    public string ProjectDirectoryText => SelectedProject?.Directory ?? "未选择项目";
    public string ProjectPortText => SelectedProject?.PortLabel ?? string.Empty;
    public Visibility ProjectPortVisibility => string.IsNullOrEmpty(ProjectPortText) ? Visibility.Collapsed : Visibility.Visible;
    public string ProjectCountSummary => ActiveRunCount > 0
        ? $"{ActiveRunCount} 个任务运行中 · 共 {Projects.Count} 个项目"
        : $"当前没有运行任务 · 共 {Projects.Count} 个项目";
    public string ProjectListEmptyText => Projects.Count == 0 ? "还没有接入项目" : "没有匹配的项目";
    public Visibility ProjectListEmptyVisibility => VisibleProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string GitStatusText => _gitStatus is null
        ? "Git：未检测到仓库"
        : $"Git：{_gitStatus.Branch ?? "无分支"} · 暂存 {_gitStatus.StagedChanges} · 未暂存 {_gitStatus.UnstagedChanges}";
    public string GitBranchText => _gitStatus?.Branch is { Length: > 0 } branch ? branch : "Git：无分支";
    public Uri? LatestLogLink => _logLinks.FindLinks(VisibleLogs.Select(log => log.Message)).LastOrDefault();
    public bool CanOpenLogLink => LatestLogLink is not null;
    public Uri? LocalLogLink
    {
        get
        {
            var localhostEntries = Logs
                .Select(log => (Log: log, Links: _logLinks.FindLinks([log.Message])))
                .Where(item => item.Links.Any(uri => uri.Scheme == Uri.UriSchemeHttp &&
                    string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var primary = localhostEntries
                .Where(item => item.Log.Message.Contains("Local:", StringComparison.OrdinalIgnoreCase))
                .LastOrDefault();
            var selected = primary.Log is not null ? primary : localhostEntries.LastOrDefault();
            return selected.Log is null
                ? null
                : selected.Links.LastOrDefault(uri => uri.Scheme == Uri.UriSchemeHttp &&
                    string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        }
    }
    public bool CanOpenLocalLogLink => LocalLogLink is not null;

    public MainWindow()
    {
        InitializeComponent();
        // Keep the native navigation chrome consistent with the Chinese product UI.
        if (Navigation.SettingsItem is NavigationViewItem settingsItem)
            settingsItem.Content = "设置";
        if (string.Equals(Environment.GetEnvironmentVariable("RUNVOKE_SHOW_TESTS"), "1", StringComparison.Ordinal))
            TestSettingsItem.Visibility = Visibility.Visible;
        var mainHandle = WindowNative.GetWindowHandle(this);
        _mainAppWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(mainHandle));
        _mainAppWindow.Closing += MainAppWindow_Closing;
        _mainAppWindow.Changed += MainAppWindow_Changed;
        var initialSize = _mainAppWindow.Size;
        if (initialSize.Width < 840 || initialSize.Height < 560)
            _mainAppWindow.Resize(new SizeInt32(Math.Max(initialSize.Width, 1020), Math.Max(initialSize.Height, 820)));
        _notificationWindow = new NotificationWindow();
        _notifications.Initialize(_notificationWindow, "bottom-right", false, false, mainHandle);
        _tray = new TrayService(mainHandle, ActivateWindow, StopAllFromTrayAsync, ExitFromTray);
        _mcp = new McpService(_core);
        _mcp.InstallUpdateRequested += (_, update) => DispatcherQueue.TryEnqueue(() => _ = ConfirmAndInstallUpdateAsync(update));
        _mcp.AgentImportRequested += (_, candidates) => DispatcherQueue.TryEnqueue(() => _ = ConfirmAndImportAgentProjectsAsync(candidates));
        _mcp.SettingsUpdateRequested += (_, request) => DispatcherQueue.TryEnqueue(() => _ = ApplyMcpSettingsAsync(request));
        Closed += async (_, _) => await _mcp.StopAsync();
        Closed += (_, _) => _notifications.Dispose();
        Closed += (_, _) => _tray.Dispose();
        Activated += (_, args) => _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        _core.RuntimeStatusChanged += (_, status) => DispatcherQueue.TryEnqueue(() => ApplyRuntimeStatus(status));
        _core.LogReceived += (_, log) => DispatcherQueue.TryEnqueue(() => ApplyLog(log));
        _core.WorkspaceChanged += (_, _) => DispatcherQueue.TryEnqueue(() => _ = LoadWorkspaceAsync());
        _ = LoadWorkspaceAsync();
        _ = LoadSettingsAsync();
        _updateTimer = DispatcherQueue.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromMinutes(30);
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(false);
        _updateTimer.Start();
    }

    public MainWindow ViewModel => this;

    private void ActivateWindow()
    {
        _windowActive = true;
        var handle = WindowNative.GetWindowHandle(this);
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
        Activate();
    }

    private void MainAppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || !_settings.MinimizeToTray) return;
        args.Cancel = true;
        _windowActive = false;
        sender.Hide();
    }

    private void MainAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;
        var size = sender.Size;
        var width = Math.Max(size.Width, 840);
        var height = Math.Max(size.Height, 560);
        if (width != size.Width || height != size.Height) sender.Resize(new SizeInt32(width, height));
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        Close();
    }

    private async Task StopAllFromTrayAsync()
    {
        try
        {
            await StopAllForUserAsync();
        }
        catch (Exception error)
        {
            DispatcherQueue.TryEnqueue(() => _ = ShowErrorAsync("停止全部任务失败", error));
        }
    }

    private async Task StopAllForUserAsync()
    {
        foreach (var run in Runs.Where(run => run.State is "starting" or "running" or "stopping"))
            _userStoppedRuns.Add(run.RunId);
        await _core.StopAllAsync();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task LoadWorkspaceAsync()
    {
        try
        {
            var selectedProjectId = _selectedProject?.Id;
            var workspace = await _core.LoadWorkspaceAsync();
            Projects.Clear();
            Groups.Clear();
            foreach (var group in workspace.Groups) Groups.Add(group);
            foreach (var project in workspace.Projects) Projects.Add(project);
            OpenSources.Clear();
            foreach (var source in workspace.OpenSources) OpenSources.Add(source);
            RefreshVisibleProjects();
            SelectedProject = Projects.FirstOrDefault(project => project.Id == selectedProjectId) ?? Projects.FirstOrDefault();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectCountSummary)));
            EmptyWorkspaceView.Visibility = Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error)
        {
            ContentDialog dialog = new()
            {
                Title = "无法读取项目配置",
                Content = error.Message,
                CloseButtonText = "关闭",
                XamlRoot = RootGrid.XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    private void DropRoot_DragOver(object sender, DragEventArgs args)
    {
        DropOverlay.Visibility = Visibility.Visible;
        if (!_settings.ReduceMotion) DropScale.ScaleX = DropScale.ScaleY = 0.995;
        args.AcceptedOperation = args.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private void DropRoot_ContextRequested(UIElement sender, ContextRequestedEventArgs args) => args.Handled = true;

    private void Project_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement element && element.DataContext is ProjectConfig project)
            SelectedProject = project;
        args.Handled = true;
    }

    private void DropRoot_DragLeave(object sender, DragEventArgs args)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        DropScale.ScaleX = DropScale.ScaleY = 1;
    }

    private async void DropRoot_Drop(object sender, DragEventArgs args)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        DropScale.ScaleX = DropScale.ScaleY = 1;
        if (!args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            var workspace = await _core.LoadWorkspaceAsync();
            var existing = workspace.Projects
                .Select(project => NormalizeDirectory(project.Directory))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = false;
            foreach (var folder in items.OfType<StorageFolder>())
            {
                try
                {
                    var path = NormalizeDirectory(folder.Path);
                    if (!Directory.Exists(path) || !existing.Add(path)) continue;
                    await _core.CreateDetectedProjectAsync(path);
                    added = true;
                }
                catch (ArgumentException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidOperationException) { }
            }
            if (added) await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("添加项目失败", error); }
    }

    private static string NormalizeDirectory(string directory) => PathNormalization.Directory(directory);

    private static string GetDirectoryName(string directory)
    {
        var normalized = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(normalized) is { Length: > 0 } name ? name : normalized;
    }

    private async Task LoadGitStatusAsync(ProjectConfig? project)
    {
        var request = ++_gitStatusRequest;
        _gitStatus = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitBranchText)));
        if (project is null) return;
        try
        {
            var status = await _core.GetGitStatusAsync(project.Directory);
            if (request != _gitStatusRequest) return;
            _gitStatus = status;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitStatusText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitBranchText)));
        }
        catch { /* Git is optional for a project. */ }
    }

    private async void CopyProjectPathButton_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProject is null) return;
        try { CopyText(SelectedProject.Directory); }
        catch (Exception error) { await ShowErrorAsync("复制项目路径失败", error); }
    }

    private async void CopyGitStatusButton_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProject is null) return;
        try { CopyText(GitStatusText); }
        catch (Exception error) { await ShowErrorAsync("复制 Git 状态失败", error); }
    }

    private async void CopyGitBranchButton_Click(object sender, RoutedEventArgs args)
    {
        if (_gitStatus?.Branch is not { Length: > 0 } branch) return;
        try { CopyText(branch); }
        catch (Exception error) { await ShowErrorAsync("复制 Git 分支失败", error); }
    }

    private async void OpenProjectDirectoryButton_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProject is null) return;
        try { _core.OpenProjectDirectory(SelectedProject.Directory); }
        catch (Exception error) { await ShowErrorAsync("打开项目目录失败", error); }
    }

    private async void OpenProjectInCodeButton_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProject is null) return;
        try { _core.OpenProjectInVisualStudioCode(SelectedProject.Directory); }
        catch (Exception error) { await ShowErrorAsync("打开 VS Code 失败", error); }
    }

    private async Task LoadSettingsAsync()
    {
        _loadingSettings = true;
        try
        {
            _settings = await _settingsStore.LoadAsync();
            _mcp.PreviewUpdatesEnabled = _settings.PreviewUpdatesEnabled;
            WorkspaceSidebarColumn.Width = new GridLength(Math.Clamp(_settings.WorkspaceSidebarWidth, 220, 420));
            SettingsSidebarColumn.Width = new GridLength(Math.Clamp(_settings.SettingsSidebarWidth, 200, 360));
            _notifications.Configure(_settings.NotificationPosition, _settings.ReduceMotion, _settings.NotificationStacking);
            ApplyTheme();
            GithubLinkCheckBox.IsChecked = _settings.GithubLinkVisible;
            GithubFooterItem.Visibility = _settings.GithubLinkVisible ? Visibility.Visible : Visibility.Collapsed;
            WorkspaceGithubButton.Visibility = _settings.GithubLinkVisible ? Visibility.Visible : Visibility.Collapsed;
            AutostartCheckBox.IsChecked = _settings.AutostartEnabled;
            PreviewUpdatesCheckBox.IsChecked = _settings.PreviewUpdatesEnabled;
            TrayEnabledCheckBox.IsChecked = _settings.MinimizeToTray;
            ReduceMotionCheckBox.IsChecked = _settings.ReduceMotion;
            UngroupedToggleButton.Content = _settings.UngroupedCollapsed ? "展开" : "收起";
            _tray.SetVisible(_settings.MinimizeToTray);
            NotificationStackingCheckBox.IsChecked = _settings.NotificationStacking;
            McpEnabledCheckBox.IsChecked = _settings.McpEnabled;
            McpPortTextBox.Text = _settings.McpPort.ToString();
            McpTokenTextBox.Password = _settings.McpToken;
            McpStatusText.Text = _settings.McpEnabled ? "正在启动…" : "已关闭";
            SelectComboTag(ThemeComboBox, _settings.Theme);
            SelectComboTag(LogLinkActionComboBox, _settings.LogLinkAction);
            SelectComboTag(NotificationPositionComboBox, _settings.NotificationPosition);
            SelectComboTag(TestNotificationPositionComboBox, _settings.NotificationPosition);
            SelectComboTag(ImportSourceComboBox, "vscode");
            if (_settings.McpEnabled) await StartMcpAsync();
            _ = CheckForUpdateAsync(false);
            DispatcherQueue.TryEnqueue(() => _ = MaybePromptProjectImportAsync());
        }
        catch (Exception error) { await ShowErrorAsync("读取设置失败", error); }
        finally { _loadingSettings = false; }
    }

    private async Task MaybePromptProjectImportAsync()
    {
        var currentVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var previousVersion = _settings.LastOpenedVersion;
        if (string.IsNullOrWhiteSpace(previousVersion))
        {
            _settings.LastOpenedVersion = currentVersion;
            await SaveSettingsAsync();
            return;
        }
        if (_settings.HasShownImportPrompt || string.Equals(previousVersion, currentVersion, StringComparison.OrdinalIgnoreCase)) return;
        _settings.LastOpenedVersion = currentVersion;
        _settings.HasShownImportPrompt = true;
        await SaveSettingsAsync();
        var dialog = new ContentDialog
        {
            Title = "导入其他软件的项目",
            Content = "检测到 Runvoke 版本已更新。是否读取 VS Code、Code Insiders、VSCodium 和 Cursor 的项目记录并选择导入？导入不会自动运行命令。",
            PrimaryButtonText = "选择项目",
            CloseButtonText = "稍后",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ImportProjectsButtonAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try { await _settingsStore.SaveAsync(_settings); }
        catch (Exception error) { await ShowErrorAsync("保存设置失败", error); }
    }

    private void ApplyTheme()
    {
        var theme = _settings.Theme == "dark" ? ElementTheme.Dark : ElementTheme.Light;
        RootGrid.RequestedTheme = theme;
        Navigation.RequestedTheme = theme;
        _notificationWindow?.SetTheme(theme);
    }

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshVisibleProjects()
    {
        foreach (var group in Groups)
            group.CountLabel = Projects.Count(project => project.GroupId == group.Id).ToString();
        var collapsedGroups = Groups.Where(group => group.Collapsed).Select(group => group.Id).ToHashSet();
        var keyword = _projectSearch.Trim();
        VisibleProjects.Clear();
        string? previousGroupId = null;
        foreach (var project in Projects)
        {
            var label = project.GroupId is { } groupId
                ? Groups.FirstOrDefault(group => group.Id == groupId)?.Name ?? "未分组"
                : "未分组";
            var activeStatus = Runs.Where(run => run.ProjectId == project.Id)
                .OrderByDescending(run => run.StartedAt ?? 0)
                .FirstOrDefault(run => run.State is "starting" or "running" or "stopping");
            project.StatusLabel = activeStatus?.StateText ?? string.Empty;
            if (keyword.Length > 0 && !string.Join(" ", project.Name, project.Directory, project.Command)
                    .Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            var visible = keyword.Length > 0;
            if (!visible)
                visible = project.GroupId is null
                    ? !_settings.UngroupedCollapsed
                    : !collapsedGroups.Contains(project.GroupId);
            if (!visible) continue;
            project.ShowGroupLabel = !string.Equals(previousGroupId, project.GroupId, StringComparison.Ordinal);
            project.GroupLabel = project.ShowGroupLabel ? label : string.Empty;
            previousGroupId = project.GroupId;
            VisibleProjects.Add(project);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleProjects)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectListEmptyVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectListEmptyText)));
    }

    private void RefreshVisibleRuns()
    {
        VisibleRuns.Clear();
        foreach (var run in Runs.Where(run => SelectedProject is null || run.ProjectId == SelectedProject.Id))
            VisibleRuns.Add(run);
    }

    private void RefreshTaskRuntimeLabels()
    {
        if (SelectedProject is null) return;
        foreach (var task in SelectedProject.Tasks)
        {
            var run = Runs.FirstOrDefault(item => item.ProjectId == SelectedProject.Id && item.TaskId == task.Id && item.State is ("starting" or "running" or "stopping"));
            task.RuntimeStateText = run?.StateText is { Length: > 0 } state ? $"{state} · 点击停止" : "待运行 · 点击启动";
        }
        var selectedTasks = SelectedProject.Tasks.Take(3).ToArray();
        SelectedTasks.Clear();
        foreach (var task in selectedTasks) SelectedTasks.Add(task);
    }

    private void ProjectSearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        _projectSearch = ProjectSearchBox.Text;
        RefreshVisibleProjects();
    }

    private void OverflowText_Loaded(object sender, RoutedEventArgs args) => UpdateOverflowTooltip(sender as FrameworkElement);

    private void OverflowText_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateOverflowTooltip(sender as FrameworkElement);

    private static void UpdateOverflowTooltip(FrameworkElement? element)
    {
        if (element is null) return;
        var text = element switch
        {
            TextBlock block => block.Text,
            Button button => button.Content?.ToString() ?? string.Empty,
            _ => string.Empty,
        };
        var overflow = !string.IsNullOrWhiteSpace(text) && element.ActualWidth > 0 && element.DesiredSize.Width > element.ActualWidth + 0.5;
        ToolTipService.SetToolTip(element, overflow ? text : null);
    }

    private async Task RefreshVisibleProjectsAnimatedAsync()
    {
        if (_settings.ReduceMotion)
        {
            RefreshVisibleProjects();
            return;
        }
        ProjectListView.Opacity = 0.72;
        await Task.Delay(90);
        RefreshVisibleProjects();
        ProjectListView.Opacity = 1;
    }

    private async void ToggleUngroupedButton_Click(object sender, RoutedEventArgs args)
    {
        var previous = _settings.UngroupedCollapsed;
        _settings.UngroupedCollapsed = !_settings.UngroupedCollapsed;
        UngroupedToggleButton.Content = _settings.UngroupedCollapsed ? "展开" : "收起";
        await RefreshVisibleProjectsAnimatedAsync();
        try { await _settingsStore.SaveAsync(_settings); }
        catch (Exception error)
        {
            _settings.UngroupedCollapsed = previous;
            UngroupedToggleButton.Content = previous ? "展开" : "收起";
            await RefreshVisibleProjectsAnimatedAsync();
            await ShowErrorAsync("保存未分组状态失败", error);
        }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: "github" })
        {
            try { _logLinks.Open(new Uri("https://github.com/GezelligheidLin/Runvoke")); }
            catch { }
            Navigation.SelectedItem = Navigation.MenuItems.FirstOrDefault();
            return;
        }
        if (args.SelectedItem is NavigationViewItem { Tag: "updates" })
        {
            WorkspaceView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Visible;
            Navigation.IsPaneVisible = false;
            SettingsSectionList.SelectedIndex = 2;
            SetSettingsSection("updates");
            return;
        }
        if (!args.IsSettingsSelected) return;
        WorkspaceView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        Navigation.IsPaneVisible = false;
        SettingsSectionList.SelectedIndex = 0;
        SetSettingsSection("general");
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs args)
    {
        WorkspaceView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        Navigation.IsPaneVisible = false;
        SettingsSectionList.SelectedIndex = 0;
        SetSettingsSection("general");
    }

    private void OpenGitHubButton_Click(object sender, RoutedEventArgs args)
    {
        try { _logLinks.Open(new Uri("https://github.com/GezelligheidLin/Runvoke")); }
        catch { }
    }

    private void OpenUpdateButton_Click(object sender, RoutedEventArgs args)
    {
        WorkspaceView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        Navigation.IsPaneVisible = false;
        SettingsSectionList.SelectedIndex = 2;
        SetSettingsSection("updates");
    }

    private void BackToWorkspaceButton_Click(object sender, RoutedEventArgs args)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        WorkspaceView.Visibility = Visibility.Visible;
        Navigation.IsPaneVisible = false;
        SettingsSectionList.SelectedIndex = -1;
        Navigation.SelectedItem = Navigation.MenuItems.FirstOrDefault();
    }

    private void SettingsSection_ItemClick(object sender, ItemClickEventArgs args)
    {
        var tag = (args.ClickedItem as ListViewItem)?.Tag?.ToString();
        SetSettingsSection(tag ?? "general");
    }

    private void SetSettingsSection(string tag)
    {
        GeneralSettingsPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        BehaviorSettingsPanel.Visibility = tag == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesSettingsPanel.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        ProjectSettingsPanel.Visibility = tag == "project-config" ? Visibility.Visible : Visibility.Collapsed;
        IntegrationSettingsPanel.Visibility = tag == "integration" ? Visibility.Visible : Visibility.Collapsed;
        TestSettingsPanel.Visibility = tag == "test" ? Visibility.Visible : Visibility.Collapsed;
        var heading = tag switch
        {
            "behavior" => BehaviorSettingsHeading,
            "updates" => UpdatesSettingsHeading,
            "project-config" => ProjectSettingsHeading,
            "integration" => IntegrationSettingsHeading,
            "test" => TestSettingsHeading,
            _ => GeneralSettingsHeading,
        };
        var point = heading.TransformToVisual(SettingsScrollViewer)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        SettingsScrollViewer.ChangeView(null, Math.Max(0, SettingsScrollViewer.VerticalOffset + point.Y - 8), null);
    }

    private async void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingSettings) return;
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item || item.Tag is null) return;
        _settings.Theme = item.Tag.ToString() ?? "light";
        ApplyTheme();
        await SaveSettingsAsync();
    }

    private void WorkspaceSplitter_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _resizingWorkspaceSidebar = true;
        _resizeStartWidth = WorkspaceSidebarColumn.Width.Value;
        _resizeStartX = args.GetCurrentPoint(RootGrid).Position.X;
        (sender as UIElement)?.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void WorkspaceSplitter_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_resizingWorkspaceSidebar) return;
        var currentX = args.GetCurrentPoint(RootGrid).Position.X;
        WorkspaceSidebarColumn.Width = new GridLength(Math.Clamp(_resizeStartWidth + currentX - _resizeStartX, 220, 420));
        args.Handled = true;
    }

    private async void WorkspaceSplitter_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_resizingWorkspaceSidebar) return;
        _resizingWorkspaceSidebar = false;
        (sender as UIElement)?.ReleasePointerCapture(args.Pointer);
        _settings.WorkspaceSidebarWidth = WorkspaceSidebarColumn.Width.Value;
        await SaveSettingsAsync();
        args.Handled = true;
    }

    private void WorkspaceSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        _resizingWorkspaceSidebar = false;
    }

    private async void WorkspaceSplitter_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        var width = WorkspaceSidebarColumn.Width.Value;
        var next = args.Key switch
        {
            Windows.System.VirtualKey.Left => width - 16,
            Windows.System.VirtualKey.Right => width + 16,
            Windows.System.VirtualKey.Home => 220,
            Windows.System.VirtualKey.End => 420,
            _ => width,
        };
        if (Math.Abs(next - width) < 0.1) return;
        WorkspaceSidebarColumn.Width = new GridLength(Math.Clamp(next, 220, 420));
        _settings.WorkspaceSidebarWidth = WorkspaceSidebarColumn.Width.Value;
        await SaveSettingsAsync();
        args.Handled = true;
    }

    private void SettingsSplitter_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _resizingSettingsSidebar = true;
        _settingsResizeStartWidth = SettingsSidebarColumn.Width.Value;
        _settingsResizeStartX = args.GetCurrentPoint(SettingsView).Position.X;
        (sender as UIElement)?.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void SettingsSplitter_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_resizingSettingsSidebar) return;
        var currentX = args.GetCurrentPoint(SettingsView).Position.X;
        SettingsSidebarColumn.Width = new GridLength(Math.Clamp(_settingsResizeStartWidth + currentX - _settingsResizeStartX, 200, 360));
        args.Handled = true;
    }

    private async void SettingsSplitter_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_resizingSettingsSidebar) return;
        _resizingSettingsSidebar = false;
        (sender as UIElement)?.ReleasePointerCapture(args.Pointer);
        _settings.SettingsSidebarWidth = SettingsSidebarColumn.Width.Value;
        await SaveSettingsAsync();
        args.Handled = true;
    }

    private void SettingsSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        _resizingSettingsSidebar = false;
    }

    private async void SettingsSplitter_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        var width = SettingsSidebarColumn.Width.Value;
        var next = args.Key switch
        {
            Windows.System.VirtualKey.Left => width - 16,
            Windows.System.VirtualKey.Right => width + 16,
            Windows.System.VirtualKey.Home => 200,
            Windows.System.VirtualKey.End => 360,
            _ => width,
        };
        if (Math.Abs(next - width) < 0.1) return;
        SettingsSidebarColumn.Width = new GridLength(Math.Clamp(next, 200, 360));
        _settings.SettingsSidebarWidth = SettingsSidebarColumn.Width.Value;
        await SaveSettingsAsync();
        args.Handled = true;
    }

    private async void LogLinkActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingSettings) return;
        if (LogLinkActionComboBox.SelectedItem is not ComboBoxItem item || item.Tag is null) return;
        _settings.LogLinkAction = item.Tag.ToString() ?? "open";
        await SaveSettingsAsync();
    }

    private async void GithubLinkCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        _settings.GithubLinkVisible = GithubLinkCheckBox.IsChecked == true;
        GithubFooterItem.Visibility = _settings.GithubLinkVisible ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceGithubButton.Visibility = _settings.GithubLinkVisible ? Visibility.Visible : Visibility.Collapsed;
        await SaveSettingsAsync();
    }

    private async void AutostartCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        _settings.AutostartEnabled = AutostartCheckBox.IsChecked == true;
        try
        {
            _autostart.SetEnabled(_settings.AutostartEnabled);
            await SaveSettingsAsync();
        }
        catch (Exception error)
        {
            AutostartCheckBox.IsChecked = !_settings.AutostartEnabled;
            _settings.AutostartEnabled = !_settings.AutostartEnabled;
            await ShowErrorAsync("设置开机启动失败", error);
        }
    }

    private async void PreviewUpdatesCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        _settings.PreviewUpdatesEnabled = PreviewUpdatesCheckBox.IsChecked == true;
        _mcp.PreviewUpdatesEnabled = _settings.PreviewUpdatesEnabled;
        await SaveSettingsAsync();
        _ = CheckForUpdateAsync(false);
    }

    private async void TrayEnabledCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        _settings.MinimizeToTray = TrayEnabledCheckBox.IsChecked == true;
        _tray.SetVisible(_settings.MinimizeToTray);
        await SaveSettingsAsync();
    }

    private async void NotificationStackingCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        _settings.NotificationStacking = NotificationStackingCheckBox.IsChecked == true;
        _notifications.Configure(_settings.NotificationPosition, _settings.ReduceMotion, _settings.NotificationStacking);
        await SaveSettingsAsync();
    }

    private async void ReduceMotionCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        _settings.ReduceMotion = ReduceMotionCheckBox.IsChecked == true;
        _notifications.Configure(_settings.NotificationPosition, _settings.ReduceMotion, _settings.NotificationStacking);
        await SaveSettingsAsync();
    }

    private async void NotificationPositionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingSettings) return;
        if (NotificationPositionComboBox.SelectedItem is not ComboBoxItem item || item.Tag is null) return;
        _settings.NotificationPosition = item.Tag.ToString() ?? "bottom-right";
        _notifications.Configure(_settings.NotificationPosition, _settings.ReduceMotion, _settings.NotificationStacking);
        await SaveSettingsAsync();
    }

    private void TestNotificationPositionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (TestNotificationPositionComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string position) return;
        SelectComboTag(NotificationPositionComboBox, position);
        _settings.NotificationPosition = position;
        _notifications.Configure(_settings.NotificationPosition, _settings.ReduceMotion, _settings.NotificationStacking);
        if (!_loadingSettings) _ = SaveSettingsAsync();
    }

    private async void ImportProjectsButton_Click(object sender, RoutedEventArgs args)
        => await ImportProjectsButtonAsync();

    private async Task ImportProjectsButtonAsync()
    {
        try
        {
            var candidates = await _core.ReadWorkspaceImportCandidatesAsync();
            if (ImportSourceComboBox.SelectedItem is ComboBoxItem sourceItem && sourceItem.Tag is string sourceTag && sourceTag != "all")
            {
                candidates = candidates.Where(candidate => sourceTag == "cursor"
                    ? candidate.Source.Contains("Cursor", StringComparison.OrdinalIgnoreCase)
                    : !candidate.Source.Contains("Cursor", StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            if (candidates.Count == 0)
            {
                await ShowErrorAsync("没有可导入的项目", new InvalidOperationException("未找到 VS Code 或 Cursor 的有效最近工作区目录。"));
                return;
            }
            var rows = new List<(ImportedProjectCandidate Candidate, CheckBox Select, CheckBox DirectoryName)>();
            var panel = new StackPanel { Spacing = 6 };
            var useDirectoryNames = new CheckBox { Content = "全部使用目录名" };
            panel.Children.Add(useDirectoryNames);
            foreach (var candidate in candidates)
            {
                var select = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top };
                var directoryName = new CheckBox { Content = "使用目录名" };
                var details = new StackPanel { Spacing = 2 };
                details.Children.Add(new TextBlock { Text = $"{candidate.Name} · {candidate.Source}", TextTrimming = TextTrimming.CharacterEllipsis });
                details.Children.Add(new TextBlock { Text = candidate.Directory, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
                details.Children.Add(new TextBlock { Text = $"命令：{candidate.SuggestedCommand ?? "pnpm dev"}", Opacity = 0.7 });
                details.Children.Add(directoryName);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(select);
                row.Children.Add(details);
                rows.Add((candidate, select, directoryName));
                panel.Children.Add(row);
            }
            useDirectoryNames.Click += (_, _) =>
            {
                foreach (var row in rows) row.DirectoryName.IsChecked = useDirectoryNames.IsChecked == true;
            };
            var dialog = new ContentDialog
            {
                Title = "导入其他软件的项目",
                Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
                PrimaryButtonText = "导入已选项目",
                CloseButtonText = "取消",
                XamlRoot = RootGrid.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var workspace = await _core.LoadWorkspaceAsync();
            var existing = workspace.Projects.Select(project => NormalizeDirectory(project.Directory)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (candidate, select, directoryName) in rows)
            {
                if (select.IsChecked != true || !existing.Add(NormalizeDirectory(candidate.Directory))) continue;
                var command = string.IsNullOrWhiteSpace(candidate.SuggestedCommand) ? "pnpm dev" : candidate.SuggestedCommand.Trim();
                await _core.SaveProjectAsync(new ProjectConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = directoryName.IsChecked == true ? GetDirectoryName(candidate.Directory) : candidate.Name,
                    Directory = candidate.Directory,
                    Command = command,
                    Tasks = [new ProjectTask { Id = Guid.NewGuid().ToString("N"), Name = "开发服务器", Command = command, Mode = "service" }],
                });
            }
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("导入项目失败", error); }
    }

    private async Task ConfirmAndImportAgentProjectsAsync(IReadOnlyList<ImportedProjectCandidate> candidates)
    {
        var rows = new List<(ImportedProjectCandidate Candidate, CheckBox Select, CheckBox DirectoryName)>();
        var panel = new StackPanel { Spacing = 6 };
        var useDirectoryNames = new CheckBox { Content = "全部使用目录名" };
        panel.Children.Add(useDirectoryNames);
        foreach (var candidate in candidates)
        {
            var command = candidate.SuggestedCommand ?? "pnpm dev";
            var select = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top };
            var directoryName = new CheckBox { Content = "使用目录名" };
            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(new TextBlock { Text = candidate.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            details.Children.Add(new TextBlock { Text = candidate.Directory, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            details.Children.Add(new TextBlock { Text = $"命令：{command}", Opacity = 0.7 });
            details.Children.Add(directoryName);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(select);
            row.Children.Add(details);
            rows.Add((candidate, select, directoryName));
            panel.Children.Add(row);
        }
        useDirectoryNames.Click += (_, _) =>
        {
            foreach (var row in rows) row.DirectoryName.IsChecked = useDirectoryNames.IsChecked == true;
        };
        var dialog = new ContentDialog
        {
            Title = "MCP Agent 项目纳入清单",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = "纳入已选项目",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var workspace = await _core.LoadWorkspaceAsync();
        var existing = workspace.Projects.Select(project => NormalizeDirectory(project.Directory)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (candidate, select, directoryName) in rows)
        {
            if (select.IsChecked != true || !existing.Add(NormalizeDirectory(candidate.Directory))) continue;
            var command = candidate.SuggestedCommand?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                try { command = (await _core.DetectProjectAsync(candidate.Directory)).Command; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            command = string.IsNullOrWhiteSpace(command) ? "pnpm dev" : command;
            await _core.SaveProjectAsync(new ProjectConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = directoryName.IsChecked == true ? GetDirectoryName(candidate.Directory) : candidate.Name.Trim(),
                Directory = candidate.Directory,
                Command = command,
                Tasks = [new ProjectTask { Id = Guid.NewGuid().ToString("N"), Name = "开发服务器", Command = command, Mode = "service" }],
            });
        }
        await LoadWorkspaceAsync();
    }

    private async void OpenConfigDirectoryButton_Click(object sender, RoutedEventArgs args)
    {
        try { _core.OpenConfigDirectory(); }
        catch (Exception error) { await ShowErrorAsync("打开配置目录失败", error); }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs args)
    {
        await CheckForUpdateAsync(true);
    }

    private void TestNotificationButton_Click(object sender, RoutedEventArgs args)
    {
        _notifications.ShowAt(
            "Runvoke 通知预览",
            "这是内部桌面通知测试，不会改变项目运行状态。",
            _settings.ReduceMotion,
            _settings.NotificationPosition);
    }

    private async Task CheckForUpdateAsync(bool showResult)
    {
        if (showResult) UpdateStatusText.Text = "正在检查更新…";
        try
        {
            _pendingUpdate = await _updates.CheckAsync(_settings.PreviewUpdatesEnabled);
            UpdateStatusText.Text = _pendingUpdate is null ? "当前已是最新版本" : $"发现版本 {_pendingUpdate.Version}，可安装";
            InstallUpdateButton.IsEnabled = _pendingUpdate is not null;
            UpdateFooterItem.Visibility = _pendingUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            WorkspaceUpdateButton.Visibility = _pendingUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateFooterItem.Content = _pendingUpdate is null ? "发现更新" : $"更新 {_pendingUpdate.Version}";
            UpdateNotesText.Text = _pendingUpdate is null || string.IsNullOrWhiteSpace(_pendingUpdate.Notes)
                ? string.Empty
                : $"更新说明：{_pendingUpdate.Notes}";
            UpdateNotesText.Visibility = _pendingUpdate is null || string.IsNullOrWhiteSpace(_pendingUpdate.Notes)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch (Exception error)
        {
            if (showResult) { UpdateStatusText.Text = "检查更新失败"; await ShowErrorAsync("检查更新失败", error); }
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs args)
    {
        if (_pendingUpdate is not null) await ConfirmAndInstallUpdateAsync(_pendingUpdate);
    }

    private async Task ConfirmAndInstallUpdateAsync(UpdateInfo update)
    {
        var previewNotice = update.Preview ? "这是预览版本，可能不稳定。\n\n" : string.Empty;
        var dialog = new ContentDialog
        {
            Title = $"安装 Runvoke {update.Version}",
            Content = $"{previewNotice}{(string.IsNullOrWhiteSpace(update.Notes) ? string.Empty : update.Notes + "\n\n")}安装前会停止所有活动项目任务，停止失败时不会继续。是否下载并安装？",
            PrimaryButtonText = "停止任务并安装",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await StopAllForUserAsync();
            UpdateStatusText.Text = "正在下载更新…";
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                UpdateStatusText.Text = value.Total is > 0
                    ? $"正在下载更新… {value.Received * 100 / value.Total.Value}%"
                    : $"正在下载更新… {value.Received / 1024 / 1024} MB";
            });
            var package = await _updates.DownloadAndPrepareAsync(update, progress);
            _updates.LaunchInstaller(package);
            UpdateStatusText.Text = "更新包已启动安装程序";
        }
        catch (Exception error) { await ShowErrorAsync("安装更新失败", error); }
    }

    private async Task StartMcpAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.McpToken))
        {
            _settings.McpToken = McpService.CreateToken();
            await SaveSettingsAsync();
        }
        await _mcp.StartAsync(_settings.McpPort, _settings.McpToken);
        McpStatusText.Text = $"已启动：http://127.0.0.1:{_settings.McpPort}/mcp/";
    }

    private async Task ApplyMcpSettingsAsync(JsonElement request)
    {
        try
        {
            if (request.TryGetProperty("theme", out var theme) && theme.ValueKind == JsonValueKind.String && theme.GetString() is { } themeValue && themeValue is "light" or "dark")
            {
                _settings.Theme = themeValue;
                SelectComboTag(ThemeComboBox, themeValue);
            }
            if (request.TryGetProperty("logLinkAction", out var linkAction) && linkAction.ValueKind == JsonValueKind.String && linkAction.GetString() is { } linkValue && linkValue is "open" or "copy")
            {
                _settings.LogLinkAction = linkValue;
                SelectComboTag(LogLinkActionComboBox, linkValue);
            }
            if (request.TryGetProperty("githubLinkVisible", out var github) && github.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _settings.GithubLinkVisible = github.GetBoolean();
                GithubLinkCheckBox.IsChecked = _settings.GithubLinkVisible;
                GithubFooterItem.Visibility = _settings.GithubLinkVisible ? Visibility.Visible : Visibility.Collapsed;
            }
            if (request.TryGetProperty("autostartEnabled", out var autostart) && autostart.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var enabled = autostart.GetBoolean();
                _autostart.SetEnabled(enabled);
                _settings.AutostartEnabled = enabled;
                AutostartCheckBox.IsChecked = enabled;
            }
            if (request.TryGetProperty("previewUpdatesEnabled", out var preview) && preview.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _settings.PreviewUpdatesEnabled = preview.GetBoolean();
                _mcp.PreviewUpdatesEnabled = _settings.PreviewUpdatesEnabled;
                PreviewUpdatesCheckBox.IsChecked = _settings.PreviewUpdatesEnabled;
            }
            if (request.TryGetProperty("notificationStacking", out var stacking) && stacking.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _settings.NotificationStacking = stacking.GetBoolean();
                NotificationStackingCheckBox.IsChecked = _settings.NotificationStacking;
            }
            if (request.TryGetProperty("reduceMotion", out var reduceMotion) && reduceMotion.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _settings.ReduceMotion = reduceMotion.GetBoolean();
                ReduceMotionCheckBox.IsChecked = _settings.ReduceMotion;
            }
            if (request.TryGetProperty("notificationPosition", out var position) && position.ValueKind == JsonValueKind.String &&
                position.GetString() is { } positionValue && positionValue is "top-left" or "top-center" or "top-right" or "bottom-left" or "bottom-center" or "bottom-right")
            {
                _settings.NotificationPosition = positionValue;
                SelectComboTag(NotificationPositionComboBox, positionValue);
            }
            _notifications.Configure(_settings.NotificationPosition, _settings.ReduceMotion, _settings.NotificationStacking);
            ApplyTheme();
            await SaveSettingsAsync();
        }
        catch (Exception error) { await ShowErrorAsync("应用 MCP 设置请求失败", error); }
    }

    private async void McpEnabledCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        _settings.McpEnabled = McpEnabledCheckBox.IsChecked == true;
        try
        {
            if (_settings.McpEnabled) await StartMcpAsync();
            else
            {
                await _mcp.StopAsync();
                McpStatusText.Text = "已关闭";
            }
            await SaveSettingsAsync();
        }
        catch (Exception error)
        {
            _settings.McpEnabled = false;
            McpEnabledCheckBox.IsChecked = false;
            McpStatusText.Text = "已关闭";
            await ShowErrorAsync("启动 MCP 服务失败", error);
        }
    }

    private void GenerateMcpTokenButton_Click(object sender, RoutedEventArgs args)
    {
        var token = McpService.CreateToken();
        McpTokenTextBox.Password = token;
    }

    private async void ApplyMcpConfigButton_Click(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings) return;
        if (!ushort.TryParse(McpPortTextBox.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            await ShowErrorAsync("保存 MCP 配置失败", new InvalidOperationException("端口必须位于 1024-65535。"));
            return;
        }
        if (string.IsNullOrWhiteSpace(McpTokenTextBox.Password))
        {
            await ShowErrorAsync("保存 MCP 配置失败", new InvalidOperationException("Bearer 令牌不能为空。"));
            return;
        }
        try
        {
            _settings.McpPort = port;
            _settings.McpToken = McpTokenTextBox.Password.Trim();
            if (_settings.McpEnabled) await StartMcpAsync();
            McpStatusText.Text = _settings.McpEnabled
                ? $"已启动：http://127.0.0.1:{_settings.McpPort}/mcp/"
                : "配置已保存，启用后生效";
            await SaveSettingsAsync();
        }
        catch (Exception error) { await ShowErrorAsync("应用 MCP 配置失败", error); }
    }

    private async void CopyMcpConfigButton_Click(object sender, RoutedEventArgs args)
    {
        if (!ushort.TryParse(McpPortTextBox.Text.Trim(), out var port) || string.IsNullOrWhiteSpace(McpTokenTextBox.Password)) return;
        var configuration = JsonSerializer.Serialize(new
        {
            endpoint = $"http://127.0.0.1:{port}/mcp/",
            authorization = $"Bearer {McpTokenTextBox.Password.Trim()}",
        }, new JsonSerializerOptions { WriteIndented = true });
        try { CopyText(configuration); }
        catch (Exception error) { await ShowErrorAsync("复制 MCP 配置失败", error); }
    }

    private async void CreateGroupButton_Click(object sender, RoutedEventArgs args)
    {
        var input = new TextBox { PlaceholderText = "例如：前端项目" };
        var moveSelected = new CheckBox
        {
            Content = SelectedProject is null ? "" : $"将当前项目“{SelectedProject.Name}”移入此分组",
            IsChecked = SelectedProject is not null,
            IsEnabled = SelectedProject is not null,
        };
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(input);
        if (SelectedProject is not null) form.Children.Add(moveSelected);
        var dialog = new ContentDialog
        {
            Title = "新建分组",
            Content = form,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return;
        try
        {
            var group = new ProjectGroup { Id = Guid.NewGuid().ToString("N"), Name = input.Text.Trim() };
            await _core.SaveGroupAsync(group);
            if (moveSelected.IsChecked == true && SelectedProject is { } project)
                await _core.MoveProjectAsync(project.Id, group.Id, int.MaxValue);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("创建分组失败", error); }
    }

    private async void ToggleAllGroupsButton_Click(object sender, RoutedEventArgs args)
    {
        var collapse = Groups.Any(group => !group.Collapsed);
        try
        {
            var workspace = await _core.LoadWorkspaceAsync();
            foreach (var group in workspace.Groups) group.Collapsed = collapse;
            await _core.SaveWorkspaceAsync(workspace);
            Groups.Clear();
            foreach (var group in workspace.Groups) Groups.Add(group);
            await RefreshVisibleProjectsAnimatedAsync();
        }
        catch (Exception error) { await ShowErrorAsync("保存分组状态失败", error); }
    }

    private async void RenameGroupButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is not ProjectGroup group) return;
        var input = new TextBox { Text = group.Name, PlaceholderText = "分组名称" };
        var dialog = new ContentDialog
        {
            Title = "重命名分组",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return;
        try
        {
            group.Name = input.Text.Trim();
            await _core.SaveGroupAsync(group);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("重命名分组失败", error); }
    }

    private void Group_DragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = _draggedProjectId is null && SelectedProject is null ? DataPackageOperation.None : DataPackageOperation.Move;
    }

    private async void Group_Drop(object sender, DragEventArgs args)
    {
        var projectId = _draggedProjectId ?? SelectedProject?.Id;
        if (projectId is null || (sender as FrameworkElement)?.DataContext is not ProjectGroup group) return;
        try
        {
            var targetIndex = Projects.Count(project => project.GroupId == group.Id);
            await _core.MoveProjectAsync(projectId, group.Id, targetIndex);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("移动项目失败", error); }
        finally { _draggedProjectId = null; }
    }

    private void Ungrouped_DragOver(object sender, DragEventArgs args)
    {
        args.AcceptedOperation = _draggedProjectId is null && SelectedProject is null ? DataPackageOperation.None : DataPackageOperation.Move;
    }

    private async void Ungrouped_Drop(object sender, DragEventArgs args)
    {
        var projectId = _draggedProjectId ?? SelectedProject?.Id;
        if (projectId is null) return;
        try
        {
            var targetIndex = Projects.Count(project => project.GroupId is null);
            await _core.MoveProjectAsync(projectId, null, targetIndex);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("移动项目失败", error); }
        finally { _draggedProjectId = null; }
    }

    private async void GitHubButton_Click(object sender, RoutedEventArgs args)
    {
        try { _logLinks.Open(new Uri("https://github.com/GezelligheidLin/Runvoke")); }
        catch (Exception error) { await ShowErrorAsync("打开 GitHub 仓库失败", error); }
    }

    private async void AddProjectButton_Click(object sender, RoutedEventArgs args) =>
        await ShowProjectEditorAsync(null);

    private async void EditProjectButton_Click(object sender, RoutedEventArgs args) =>
        await ShowProjectEditorAsync(SelectedProject);

    private async void DeleteProjectButton_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProject is null) return;
        var project = SelectedProject;
        var activeRuns = Runs
            .Where(run => run.ProjectId == project.Id && run.State is ("starting" or "running" or "stopping"))
            .ToArray();
        var dialog = new ContentDialog
        {
            Title = "删除项目",
            Content = activeRuns.Length == 0
                ? $"确定删除“{project.Name}”的配置吗？项目目录本身不会被删除。"
                : $"确定删除“{project.Name}”的配置吗？该项目当前有 {activeRuns.Length} 个运行实例，删除前会停止它们；项目目录本身不会被删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            foreach (var run in activeRuns) _userStoppedRuns.Add(run.RunId);
            foreach (var run in activeRuns)
            {
                try { await _core.StopRunAsync(run.RunId); }
                catch (KeyNotFoundException) { }
            }
            await _core.DeleteProjectAsync(project.Id);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("删除项目失败", error); }
    }

    private async void ProjectContextEdit_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as MenuFlyoutItem)?.Tag is ProjectConfig project)
        {
            SelectedProject = project;
            await ShowProjectEditorAsync(project);
        }
    }

    private async void ProjectContextOpen_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not ProjectConfig project) return;
        try { _core.OpenProjectDirectory(project.Directory); }
        catch (Exception error) { await ShowErrorAsync("打开项目目录失败", error); }
    }

    private async void ProjectContextMove_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not ProjectConfig project) return;
        var options = new List<(string? Id, string Name)> { (null, "未分组") };
        options.AddRange(Groups.Select(group => ((string?)group.Id, group.Name)));
        var combo = new ComboBox { ItemsSource = options.Select(item => item.Name).ToList(), SelectedIndex = Math.Max(0, options.FindIndex(item => item.Id == project.GroupId)) };
        var dialog = new ContentDialog
        {
            Title = $"移动“{project.Name}”",
            Content = combo,
            PrimaryButtonText = "移动",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || combo.SelectedIndex < 0) return;
        try
        {
            await _core.MoveProjectAsync(project.Id, options[combo.SelectedIndex].Id, int.MaxValue);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("移动项目失败", error); }
    }

    private async void ProjectContextDelete_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not ProjectConfig project) return;
        SelectedProject = project;
        await DeleteProjectButton_ClickAsync();
    }

    private async Task DeleteProjectButton_ClickAsync()
    {
        if (SelectedProject is null) return;
        var project = SelectedProject;
        var activeRuns = Runs
            .Where(run => run.ProjectId == project.Id && run.State is ("starting" or "running" or "stopping"))
            .ToArray();
        var dialog = new ContentDialog
        {
            Title = "删除项目",
            Content = activeRuns.Length == 0
                ? $"确定删除“{project.Name}”的配置吗？项目目录本身不会被删除。"
                : $"确定删除“{project.Name}”的配置吗？该项目当前有 {activeRuns.Length} 个运行实例，删除前会停止它们；项目目录本身不会被删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            foreach (var run in activeRuns) _userStoppedRuns.Add(run.RunId);
            foreach (var run in activeRuns)
            {
                try { await _core.StopRunAsync(run.RunId); }
                catch (KeyNotFoundException) { }
            }
            await _core.DeleteProjectAsync(project.Id);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("删除项目失败", error); }
    }

    private async Task ShowProjectEditorAsync(ProjectConfig? source)
    {
        var project = source is null ? new ProjectConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Tasks = [],
        } : CloneProject(source);
        var taskRows = new List<TaskEditorRow>();
        var name = new TextBox { Header = "项目名称", Text = project.Name, PlaceholderText = "留空时使用目录名" };
        var directory = new TextBox { Header = "项目目录", Text = project.Directory, PlaceholderText = "输入绝对路径，或使用选择目录" };
        var chooseDirectory = new Button { Content = "选择目录", HorizontalAlignment = HorizontalAlignment.Left };
        chooseDirectory.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                directory.Text = folder.Path;
                try
                {
                    var detected = await _core.DetectProjectAsync(folder.Path);
                    if (string.IsNullOrWhiteSpace(name.Text)) name.Text = detected.Name;
                    if (taskRows.Count == 0) AddTaskRow(detected.Tasks.FirstOrDefault() ?? new ProjectTask { Id = Guid.NewGuid().ToString("N"), Name = "开发服务器", Command = detected.Command, Mode = "service" });
                    else if (string.IsNullOrWhiteSpace(taskRows[0].Command.Text)) taskRows[0].Command.Text = detected.Command;
                }
                catch
                {
                    if (string.IsNullOrWhiteSpace(name.Text)) name.Text = folder.Name;
                }
            }
        };
        var group = new ComboBox
        {
            Header = "分组",
            ItemsSource = Groups,
            DisplayMemberPath = "Name",
            SelectedItem = Groups.FirstOrDefault(item => item.Id == project.GroupId),
        };
        var port = new TextBox { Header = "本地端口（可选）", Text = project.Port?.ToString() ?? string.Empty, PlaceholderText = "例如 3000" };
        var form = new StackPanel { Spacing = 10 };
        var taskList = new StackPanel { Spacing = 8 };
        var envRows = new List<EnvEditorRow>();
        var envList = new StackPanel { Spacing = 8 };
        var addTask = new Button { Content = "新增任务", HorizontalAlignment = HorizontalAlignment.Left };
        var addEnv = new Button { Content = "新增环境变量", HorizontalAlignment = HorizontalAlignment.Left };
        void AddTaskRow(ProjectTask task)
        {
            if (taskRows.Count >= 3) return;
            var row = new TaskEditorRow(task);
            taskRows.Add(row);
            row.Remove.Click += (_, _) =>
            {
                taskRows.Remove(row);
                taskList.Children.Remove(row.Root);
            };
            taskList.Children.Add(row.Root);
        }
        foreach (var task in project.Tasks.Take(3)) AddTaskRow(task);
        if (taskRows.Count == 0) AddTaskRow(new ProjectTask { Id = "default", Name = "开发服务器", Mode = "service" });
        addTask.Click += (_, _) => AddTaskRow(new ProjectTask { Id = Guid.NewGuid().ToString("N"), Name = $"任务 {taskRows.Count + 1}", Mode = "service" });
        void AddEnvRow(EnvVariable variable)
        {
            var row = new EnvEditorRow(variable);
            envRows.Add(row);
            row.Remove.Click += (_, _) => { envRows.Remove(row); envList.Children.Remove(row.Root); };
            envList.Children.Add(row.Root);
        }
        foreach (var variable in project.Env) AddEnvRow(variable);
        addEnv.Click += (_, _) => AddEnvRow(new EnvVariable());
        form.Children.Add(name);
        form.Children.Add(directory);
        form.Children.Add(chooseDirectory);
        form.Children.Add(group);
        form.Children.Add(port);
        form.Children.Add(new TextBlock { Text = "预设任务（最多 3 条）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        form.Children.Add(taskList);
        form.Children.Add(addTask);
        form.Children.Add(new TextBlock { Text = "环境变量", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        form.Children.Add(envList);
        form.Children.Add(addEnv);
        var dialog = new ContentDialog
        {
            Title = source is null ? "添加项目" : "编辑项目",
            Content = form,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(directory.Text) || !Directory.Exists(directory.Text))
        {
            await ShowErrorAsync("保存项目失败", new InvalidOperationException("请选择存在的项目目录。"));
            return;
        }
        project.Directory = directory.Text;
        project.GroupId = (group.SelectedItem as ProjectGroup)?.Id;
        project.Port = ushort.TryParse(port.Text.Trim(), out var parsedPort) && parsedPort > 0 ? parsedPort : null;
        project.Env = envRows
            .Select(row => new EnvVariable { Key = row.Key.Text.Trim(), Value = row.Value.Text })
            .Where(variable => IsValidEnvironmentKey(variable.Key))
            .Take(64)
            .ToList();
        project.Name = string.IsNullOrWhiteSpace(name.Text) ? Path.GetFileName(project.Directory.TrimEnd(Path.DirectorySeparatorChar)) : name.Text.Trim();
        project.Tasks = taskRows
            .Select(row => new ProjectTask
            {
                Id = row.Task.Id,
                Name = string.IsNullOrWhiteSpace(row.Name.Text) ? "未命名任务" : row.Name.Text.Trim(),
                Command = row.Command.Text.Trim(),
                Mode = (row.Mode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "service",
            })
            .Where(task => !string.IsNullOrWhiteSpace(task.Command))
            .Take(3)
            .ToList();
        if (project.Tasks.Count == 0)
        {
            await ShowErrorAsync("保存项目失败", new InvalidOperationException("至少需要一条包含命令的任务。"));
            return;
        }
        project.Command = project.Tasks[0].Command;
        try
        {
            await _core.SaveProjectAsync(project);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("保存项目失败", error); }
    }

    private static ProjectConfig CloneProject(ProjectConfig source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Directory = source.Directory,
        GroupId = source.GroupId,
        Command = source.Command,
        Port = source.Port,
        Env = source.Env.Select(variable => new EnvVariable { Key = variable.Key, Value = variable.Value }).ToList(),
        Tasks = source.Tasks.Select(task => new ProjectTask { Id = task.Id, Name = task.Name, Command = task.Command, Mode = task.Mode }).ToList(),
    };

    private static bool IsValidEnvironmentKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 128 &&
        key.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.');

    private sealed class TaskEditorRow
    {
        public TaskEditorRow(ProjectTask task)
        {
            Task = task;
            Name = new TextBox { Header = "任务名称", Text = task.Name, PlaceholderText = "例如：开发服务器" };
            Command = new TextBox { Header = "命令", Text = task.Command, PlaceholderText = "例如：pnpm dev" };
                Mode = new ComboBox { Header = "类型" };
                Mode.Items.Add(new ComboBoxItem { Content = "常驻服务", Tag = "service" });
                Mode.Items.Add(new ComboBoxItem { Content = "一次任务", Tag = "once" });
                Mode.SelectedIndex = string.Equals(task.Mode, "once", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            Remove = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Top };
            Root = new Grid();
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var fields = new StackPanel { Spacing = 6 };
            fields.Children.Add(Name);
            fields.Children.Add(Command);
            fields.Children.Add(Mode);
            Root.Children.Add(fields);
            Grid.SetColumn(Remove, 1);
            Root.Children.Add(Remove);
        }

        public ProjectTask Task { get; }
        public TextBox Name { get; }
        public TextBox Command { get; }
        public ComboBox Mode { get; }
        public Button Remove { get; }
        public Grid Root { get; }
    }

    private sealed class EnvEditorRow
    {
        public EnvEditorRow(EnvVariable variable)
        {
            Key = new TextBox { PlaceholderText = "名称", Text = variable.Key, Width = 180 };
            Value = new TextBox { PlaceholderText = "值", Text = variable.Value, Width = 300 };
            Remove = new Button { Content = "移除" };
            Root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            Root.Children.Add(Key); Root.Children.Add(Value); Root.Children.Add(Remove);
        }
        public TextBox Key { get; }
        public TextBox Value { get; }
        public Button Remove { get; }
        public StackPanel Root { get; }
    }

    private async void DeleteGroupButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is not ProjectGroup group) return;
        var dialog = new ContentDialog
        {
            Title = "删除分组",
            Content = $"删除“{group.Name}”后，组内项目会保留并移到未分组。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _core.DeleteGroupAsync(group.Id);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("删除分组失败", error); }
    }

    private async void ToggleGroupButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is not ProjectGroup group) return;
        var previous = group.Collapsed;
        group.Collapsed = !group.Collapsed;
        try
        {
            await _core.SaveGroupAsync(group);
            await RefreshVisibleProjectsAnimatedAsync();
        }
        catch (Exception error)
        {
            group.Collapsed = previous;
            await RefreshVisibleProjectsAnimatedAsync();
            await ShowErrorAsync("保存分组状态失败", error);
        }
    }

    private async void MoveProjectUpButton_Click(object sender, RoutedEventArgs args) => await MoveSelectedProjectAsync(-1);

    private async void MoveProjectDownButton_Click(object sender, RoutedEventArgs args) => await MoveSelectedProjectAsync(1);

    private async Task MoveSelectedProjectAsync(int offset)
    {
        if (SelectedProject is null) return;
        var previous = Projects.ToList();
        var index = Projects.IndexOf(SelectedProject);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Projects.Count) return;
        Projects.Move(index, target);
        RefreshVisibleProjects();
        try { await _core.ReorderProjectsAsync(Projects.Select(project => project.Id).ToArray()); }
        catch (Exception error)
        {
            Projects.Clear();
            foreach (var project in previous) Projects.Add(project);
            RefreshVisibleProjects();
            await ShowErrorAsync("保存项目顺序失败", error);
        }
    }

    private async void ProjectList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        try
        {
            var visible = VisibleProjects.Select(project => project.Id).ToList();
            var order = new List<string>();
            var cursor = 0;
            foreach (var project in Projects)
            {
                if (visible.Contains(project.Id, StringComparer.Ordinal)) order.Add(visible[cursor++]);
                else order.Add(project.Id);
            }
            await _core.ReorderProjectsAsync(order);
            await LoadWorkspaceAsync();
        }
        catch (Exception error)
        {
            if (_projectOrderBeforeDrag is not null)
            {
                Projects.Clear();
                foreach (var project in _projectOrderBeforeDrag) Projects.Add(project);
                RefreshVisibleProjects();
            }
            await ShowErrorAsync("保存项目顺序失败", error);
        }
        finally
        {
            _draggedProjectId = null;
            _projectOrderBeforeDrag = null;
        }
    }

    private void ProjectList_DragItemsStarting(ListViewBase sender, DragItemsStartingEventArgs args)
    {
        _projectOrderBeforeDrag = Projects.ToList();
        _draggedProjectId = args.Items.FirstOrDefault() is ProjectConfig project ? project.Id : null;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs args)
    {
        var project = SelectedProject;
        var task = project?.Tasks.FirstOrDefault();
        if (project is null || task is null) return;
        try { SelectedRun = await _core.StartTaskAsync(project.Id, task.Id); }
        catch (Exception error) { await ShowErrorAsync("任务操作失败", error); }
    }

    private async void StartTaskButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is not ProjectTask task || SelectedProject is null) return;
        var active = Runs.FirstOrDefault(run => run.ProjectId == SelectedProject.Id && run.TaskId == task.Id && run.State is ("starting" or "running" or "stopping"));
        try
        {
            if (active is not null)
            {
                if (active.State != "stopping") await ConfirmStopRunAsync(active);
            }
            else
            {
                SelectedRun = await _core.StartTaskAsync(SelectedProject.Id, task.Id);
            }
        }
        catch (Exception error) { await ShowErrorAsync("任务操作失败", error); }
    }

    private async void RunTemporaryCommandButton_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProject is null || string.IsNullOrWhiteSpace(TemporaryCommandTextBox.Text)) return;
        try
        {
            SelectedRun = await _core.RunTemporaryCommandAsync(SelectedProject.Id, TemporaryCommandTextBox.Text);
            TemporaryCommandTextBox.Text = string.Empty;
        }
        catch (Exception error) { await ShowErrorAsync("运行临时命令失败", error); }
    }

    private async void OpenSourceButton_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProject is null || (sender as Button)?.Tag is not OpenSourceConfig source) return;
        try { _core.OpenProjectWithSource(SelectedProject.Directory, source); }
        catch (Exception error) { await ShowErrorAsync("打开项目失败", error); }
    }

    private async void AddSourceButton_Click(object sender, RoutedEventArgs args) => await ShowSourceEditorAsync(null);

    private async void EditSourceButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is OpenSourceConfig source) await ShowSourceEditorAsync(source);
    }

    private async void DeleteSourceButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is not OpenSourceConfig source) return;
        var dialog = new ContentDialog
        {
            Title = "删除软件源",
            Content = $"确定删除“{source.Name}”吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _core.DeleteOpenSourceAsync(source.Id);
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("删除软件源失败", error); }
    }

    private async Task ShowSourceEditorAsync(OpenSourceConfig? source)
    {
        var model = source is null
            ? new OpenSourceConfig { Id = Guid.NewGuid().ToString("N"), Name = "新软件源", Arguments = "\"{directory}\"" }
            : new OpenSourceConfig { Id = source.Id, Name = source.Name, Executable = source.Executable, Arguments = source.Arguments };
        var name = new TextBox { Header = "名称", Text = model.Name };
        var executable = new TextBox { Header = "可执行程序路径或命令", Text = model.Executable, PlaceholderText = "例如 code 或 C:\\Program Files\\...\\Code.exe" };
        var arguments = new TextBox { Header = "参数模板", Text = model.Arguments, PlaceholderText = "使用 {directory} 表示项目目录" };
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(name); form.Children.Add(executable); form.Children.Add(arguments);
        var dialog = new ContentDialog
        {
            Title = source is null ? "新增软件源" : "编辑软件源",
            Content = form,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _core.SaveOpenSourceAsync(new OpenSourceConfig
            {
                Id = model.Id,
                Name = name.Text.Trim(),
                Executable = executable.Text.Trim(),
                Arguments = arguments.Text,
            });
            await LoadWorkspaceAsync();
        }
        catch (Exception error) { await ShowErrorAsync("保存软件源失败", error); }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedRun is null) return;
        await ConfirmStopRunAsync(SelectedRun);
    }

    private async Task<bool> ConfirmStopRunAsync(RuntimeStatus run)
    {
        var dialog = new ContentDialog
        {
            Title = "停止任务",
            Content = $"确定停止“{run.TaskName}”及其子进程吗？",
            PrimaryButtonText = "停止",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        try
        {
            _userStoppedRuns.Add(run.RunId);
            SelectedRun = await _core.StopRunAsync(run.RunId);
            return true;
        }
        catch (Exception error) { await ShowErrorAsync("停止任务失败", error); return false; }
    }

    private async void StopAllButton_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog
        {
            Title = "停止全部任务",
            Content = "将停止所有活动运行实例及其子进程。",
            PrimaryButtonText = "停止",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            foreach (var run in Runs.Where(run => run.State is "starting" or "running" or "stopping"))
                _userStoppedRuns.Add(run.RunId);
            await StopAllForUserAsync();
        }
        catch (Exception error) { await ShowErrorAsync("停止全部任务失败", error); }
    }

    private async void RemoveRunButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is not RuntimeStatus run) return;
        if (run.State is "running" or "stopping")
        {
            await ShowErrorAsync("无法移除运行记录", new InvalidOperationException("活动运行实例必须先停止。"));
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "移除运行记录",
            Content = $"确定移除“{run.TaskName}”的运行记录和日志吗？",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _core.ForgetRun(run.RunId);
        var index = Runs.ToList().FindIndex(item => item.RunId == run.RunId);
        if (index >= 0) Runs.RemoveAt(index);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRunCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRunSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectCountSummary)));
        RefreshVisibleRuns();
        RefreshTaskRuntimeLabels();
        if (SelectedRun?.RunId == run.RunId) SelectedRun = null;
    }

    private async void ClearInactiveRunsButton_Click(object sender, RoutedEventArgs args)
    {
        var inactive = VisibleRuns.Where(run => run.State is not ("starting" or "running" or "stopping")).ToList();
        if (inactive.Count == 0) return;
        var dialog = new ContentDialog
        {
            Title = "清除已结束运行记录",
            Content = $"确定移除 {inactive.Count} 条已结束运行记录及其日志吗？",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var run in inactive) _core.ForgetRun(run.RunId);
        foreach (var run in inactive) Runs.Remove(run);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRunCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRunSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectCountSummary)));
        RefreshVisibleRuns();
        RefreshTaskRuntimeLabels();
        if (SelectedRun is not null && inactive.Any(run => run.RunId == SelectedRun.RunId)) SelectedRun = null;
        Logs.Clear();
        if (SelectedRun is not null)
            foreach (var entry in _core.GetRecentLogs(SelectedRun.RunId, 500)) Logs.Add(entry);
        RefreshVisibleLogs();
    }

    private void ApplyRuntimeStatus(RuntimeStatus status)
    {
        if (status.State == "stopping") _userStoppedRuns.Add(status.RunId);
        if (status.State == "stopped") _userStoppedRuns.Remove(status.RunId);
        var index = Runs.ToList().FindIndex(run => run.RunId == status.RunId);
        if (index >= 0) Runs[index] = status;
        else Runs.Insert(0, status);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRunCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRunSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProjectCountSummary)));
        RefreshVisibleRuns();
        RefreshVisibleProjects();
        RefreshTaskRuntimeLabels();
        if (SelectedRun?.RunId == status.RunId || status.State == "running") SelectedRun = status;
        if (status.State is "succeeded" or "failed")
        {
            var suppressed = _userStoppedRuns.Remove(status.RunId);
            var mainHandle = WindowNative.GetWindowHandle(this);
            var backgrounded = !_windowActive || IsIconic(mainHandle) || !IsWindowVisible(mainHandle);
            if (backgrounded && !suppressed)
                // Completion notices stay in the fixed bottom-right location;
                // the configurable position is reserved for the internal preview.
                _notifications.ShowAt(status.State == "succeeded" ? "任务完成" : "任务失败", status.TaskName, _settings.ReduceMotion, "bottom-right");
        }
    }

    private void ApplyLog(LogEvent log)
    {
        if (SelectedRun?.RunId != log.RunId) return;
        if (log.Mode is "progress" or "finish")
        {
            for (var index = Logs.Count - 1; index >= 0; index--)
            {
                if (Logs[index].Stream != log.Stream) continue;
                if (Logs[index].Mode is "progress" or "finish")
                {
                    Logs[index] = log;
                    RefreshVisibleLogs();
                    return;
                }
                break;
            }
        }
        Logs.Add(log);
        while (Logs.Count > 2_000) Logs.RemoveAt(0);
        RefreshVisibleLogs();
        ScrollLogsToEnd();
    }

    private void ScrollLogsToEnd()
    {
        if (AutoScrollCheckBox?.IsChecked != true || VisibleLogLines.Count == 0) return;
        DispatcherQueue.TryEnqueue(() => LogListView.ScrollIntoView(VisibleLogLines[^1]));
    }

    private void RefreshVisibleLogs()
    {
        VisibleLogs.Clear();
        VisibleLogLines.Clear();
        foreach (var log in Logs)
            if (_logFilter == "all" || log.Stream == _logFilter)
            {
                VisibleLogs.Add(log);
                VisibleLogLines.Add(new LogLineView(log, _logLinks.Split(log.Message)));
            }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LatestLogLink)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenLogLink)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalLogLink)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanOpenLocalLogLink)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogEmptyVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogEmptyText)));
    }

    private void LogFilterButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is string filter)
        {
            _logFilter = filter;
            RefreshVisibleLogs();
        }
    }

    private async void LogSegmentButton_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as Button)?.Tag is not string value || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return;
        try
        {
            if (string.Equals(_settings.LogLinkAction, "copy", StringComparison.OrdinalIgnoreCase))
                CopyText(uri.AbsoluteUri);
            else
                _logLinks.Open(uri);
        }
        catch (Exception error) { await ShowErrorAsync("打开日志链接失败", error); }
    }

    private async void CopyLogsButton_Click(object sender, RoutedEventArgs args)
    {
        if (VisibleLogs.Count == 0) return;
        try
        {
            CopyText(string.Join(Environment.NewLine, VisibleLogs.Select(log => $"[{DateTimeOffset.FromUnixTimeMilliseconds(log.Timestamp):HH:mm:ss}] {log.Stream.ToUpperInvariant()} {log.Message}")));
        }
        catch (Exception error) { await ShowErrorAsync("复制日志失败", error); }
    }

    private async void ClearLogsButton_Click(object sender, RoutedEventArgs args)
    {
        if (Logs.Count == 0) return;
        var dialog = new ContentDialog
        {
            Title = "清空日志",
            Content = "确定清空当前运行实例的日志吗？",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (SelectedRun is not null) _core.ClearLogs(SelectedRun.RunId);
        Logs.Clear();
        RefreshVisibleLogs();
    }

    private async void OpenLogLinkButton_Click(object sender, RoutedEventArgs args)
    {
        if (LatestLogLink is null) return;
        try
        {
            if (string.Equals(_settings.LogLinkAction, "copy", StringComparison.OrdinalIgnoreCase))
                CopyText(LatestLogLink.AbsoluteUri);
            else
                _logLinks.Open(LatestLogLink);
        }
        catch (Exception error) { await ShowErrorAsync("打开日志链接失败", error); }
    }

    private void OpenLocalLogLinkButton_Click(object sender, RoutedEventArgs args)
    {
        if (LocalLogLink is null) return;
        _logFilter = "all";
        AutoScrollCheckBox.IsChecked = false;
        RefreshVisibleLogs();
        var target = VisibleLogLines.LastOrDefault(line =>
            line.Segments.Any(segment => string.Equals(segment.Url, LocalLogLink.AbsoluteUri, StringComparison.OrdinalIgnoreCase)));
        if (target is not null)
            DispatcherQueue.TryEnqueue(() => LogListView.ScrollIntoView(target));
    }

    private async void CopyLogLinkButton_Click(object sender, RoutedEventArgs args)
    {
        if (LatestLogLink is null) return;
        try { CopyText(LatestLogLink.AbsoluteUri); }
        catch (Exception error) { await ShowErrorAsync("复制链接失败", error); }
    }

    private static void CopyText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
    }

    private async Task ShowErrorAsync(string title, Exception error)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = error.Message,
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
