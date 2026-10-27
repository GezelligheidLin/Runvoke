using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

/// <summary>
/// Native application boundary. The runId based contract remains compatible with the Tauri client.
/// </summary>
public sealed class NativeRunvokeCore : IRunvokeCore
{
    private readonly WorkspaceStoreService _store;
    private readonly ProcessSupervisor _supervisor;
    private readonly LogHistoryStore _logs = new();
    private readonly ProjectIntegrationService _projectIntegration = new();
    private readonly ProjectDetectionService _projectDetection = new();
    private readonly WorkspaceImportService _workspaceImport = new();
    private WorkspaceStore? _workspace;

    public NativeRunvokeCore(WorkspaceStoreService store)
        : this(store, new ProcessSupervisor()) { }

    internal NativeRunvokeCore(WorkspaceStoreService store, ProcessSupervisor supervisor)
    {
        _store = store;
        _supervisor = supervisor;
        _supervisor.LogReceived += (_, log) =>
        {
            _logs.Append(log);
            LogReceived?.Invoke(this, log);
        };
        _supervisor.RuntimeStatusChanged += (_, status) => RuntimeStatusChanged?.Invoke(this, status);
    }

    public event EventHandler<LogEvent>? LogReceived;
    public event EventHandler<RuntimeStatus>? RuntimeStatusChanged;
    public event EventHandler? WorkspaceChanged;

    public async Task<WorkspaceStore> LoadWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        _workspace = await _store.LoadAsync(cancellationToken);
        return _workspace;
    }

    public async Task<IReadOnlyList<ProjectConfig>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        _workspace = await _store.LoadAsync(cancellationToken);
        return _workspace.Projects;
    }

    public Task SaveWorkspaceAsync(WorkspaceStore workspace, CancellationToken cancellationToken = default)
    {
        _workspace = workspace;
        return SaveWorkspaceAndNotifyAsync(workspace, cancellationToken);
    }

    private async Task SaveWorkspaceAndNotifyAsync(WorkspaceStore workspace, CancellationToken cancellationToken)
    {
        await _store.SaveAsync(workspace, cancellationToken);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveProjectAsync(ProjectConfig project, CancellationToken cancellationToken = default)
    {
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(project.Id)) project.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(project.Name)) throw new InvalidOperationException("项目名称不能为空。");
        if (string.IsNullOrWhiteSpace(project.Directory) || !Path.IsPathRooted(project.Directory))
            throw new InvalidOperationException("项目目录必须是绝对路径。");
        var normalizedDirectory = PathNormalization.Directory(project.Directory);
        if (!Directory.Exists(normalizedDirectory)) throw new DirectoryNotFoundException($"项目目录不存在：{normalizedDirectory}");
        project.Directory = normalizedDirectory;
        if (project.GroupId is not null && workspace.Groups.All(group => group.Id != project.GroupId))
            throw new KeyNotFoundException("项目分组不存在。");
        project.Tasks = (project.Tasks ?? [])
            .Where(task => task is not null && !string.IsNullOrWhiteSpace(task.Command))
            .Take(3)
            .Select(task => new ProjectTask
            {
                Id = string.IsNullOrWhiteSpace(task.Id) ? Guid.NewGuid().ToString("N") : task.Id,
                Name = string.IsNullOrWhiteSpace(task.Name) ? "未命名任务" : task.Name.Trim(),
                Command = task.Command.Trim(),
                Mode = string.Equals(task.Mode, "once", StringComparison.OrdinalIgnoreCase) ? "once" : "service",
            }).ToList();
        if (project.Tasks.Count == 0) throw new InvalidOperationException("至少需要一条有效任务。");
        project.Command = project.Tasks[0].Command;
        project.Env = (project.Env ?? [])
            .Where(variable => variable is not null)
            .Select(variable => new EnvVariable { Key = variable.Key?.Trim() ?? string.Empty, Value = variable.Value ?? string.Empty })
            .Where(variable => variable.Key.Length > 0 && variable.Key.Length <= 128 &&
                variable.Key.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.'))
            .Take(64)
            .ToList();
        var index = workspace.Projects.FindIndex(item => item.Id == project.Id);
        if (index < 0) workspace.Projects.Add(project);
        else workspace.Projects[index] = project;
        _workspace = workspace;
        await _store.SaveAsync(workspace, cancellationToken);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await _supervisor.StopProjectAsync(projectId, timeout.Token);
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        workspace.Projects.RemoveAll(project => project.Id == projectId);
        _workspace = workspace;
        await _store.SaveAsync(workspace, cancellationToken);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveGroupAsync(ProjectGroup group, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(group.Name)) throw new InvalidOperationException("分组名称不能为空。");
        if (string.IsNullOrWhiteSpace(group.Id)) group.Id = Guid.NewGuid().ToString("N");
        group.Name = group.Name.Trim();
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        if (group.Name.Length > 30)
            throw new InvalidOperationException("分组名称不能超过 30 个字符。");
        if (workspace.Groups.Any(item => item.Id != group.Id &&
            string.Equals(item.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("已存在同名分组。");
        var index = workspace.Groups.FindIndex(item => item.Id == group.Id);
        if (index < 0) workspace.Groups.Add(group);
        else workspace.Groups[index] = group;
        _workspace = workspace;
        await _store.SaveAsync(workspace, cancellationToken);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        workspace.Groups.RemoveAll(group => group.Id == groupId);
        foreach (var project in workspace.Projects.Where(project => project.GroupId == groupId)) project.GroupId = null;
        _workspace = workspace;
        await _store.SaveAsync(workspace, cancellationToken);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<RuntimeStatus> StartTaskAsync(
        string projectId,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        _workspace = workspace;
        var project = workspace.Projects.FirstOrDefault(item => item.Id == projectId)
            ?? throw new KeyNotFoundException("项目不存在。");
        var task = project.Tasks.FirstOrDefault(item => item.Id == taskId)
            ?? throw new KeyNotFoundException("任务不存在。");
        return await _supervisor.StartAsync(project, task, cancellationToken);
    }

    public async Task<RuntimeStatus> StopRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await _supervisor.StopAsync(runId, timeout.Token);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await _supervisor.StopAllAsync(timeout.Token);
    }

    public void ForgetRun(string runId)
    {
        _logs.Remove(runId);
        _supervisor.ForgetRun(runId);
    }

    public void ClearLogs(string runId) => _logs.Remove(runId);

    public IReadOnlyList<RuntimeStatus> ListRuns() => _supervisor.ListRuns();

    public Task<GitProjectStatus?> GetGitStatusAsync(string directory, CancellationToken cancellationToken = default) =>
        _projectIntegration.GetGitStatusAsync(directory, cancellationToken);

    public void OpenProjectDirectory(string directory) => _projectIntegration.OpenDirectory(directory);

    public void OpenProjectInVisualStudioCode(string directory) => _projectIntegration.OpenInVisualStudioCode(directory);

    public async Task<ProjectConfig> CreateDetectedProjectAsync(string directory, CancellationToken cancellationToken = default)
    {
        var project = await DetectProjectAsync(directory, cancellationToken);
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        if (workspace.Projects.Any(existing =>
            TryNormalizeDirectory(existing.Directory) is { } existingDirectory &&
            string.Equals(existingDirectory, project.Directory, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("项目目录已存在，不能重复接入。");
        await SaveProjectAsync(project, cancellationToken);
        return project;
    }

    public Task<ProjectConfig> DetectProjectAsync(string directory, CancellationToken cancellationToken = default) =>
        _projectDetection.CreateProjectAsync(directory, cancellationToken);

    private static string? TryNormalizeDirectory(string value)
    {
        try { return PathNormalization.Directory(value); }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
    }

    public async Task ReorderProjectsAsync(IReadOnlyList<string> orderedProjectIds, CancellationToken cancellationToken = default)
    {
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        var byId = workspace.Projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var reordered = orderedProjectIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        reordered.AddRange(workspace.Projects.Where(project => !orderedProjectIds.Contains(project.Id)));
        workspace.Projects = reordered;
        _workspace = workspace;
        await _store.SaveAsync(workspace, cancellationToken);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task MoveProjectAsync(string projectId, string? groupId, int targetIndex, CancellationToken cancellationToken = default)
    {
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        var project = workspace.Projects.FirstOrDefault(item => item.Id == projectId)
            ?? throw new KeyNotFoundException("项目不存在。");
        if (groupId is not null && workspace.Groups.All(group => group.Id != groupId))
            throw new KeyNotFoundException("目标分组不存在。");
        project.GroupId = groupId;
        var groupItems = workspace.Projects.Where(item => item.GroupId == groupId && item.Id != projectId).ToList();
        var insertAt = Math.Clamp(targetIndex, 0, groupItems.Count);
        groupItems.Insert(insertAt, project);
        var groupSet = groupItems.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var rebuilt = new List<ProjectConfig>(workspace.Projects.Count);
        var inserted = false;
        foreach (var item in workspace.Projects)
        {
            if (!groupSet.Contains(item.Id))
            {
                rebuilt.Add(item);
                continue;
            }
            if (!inserted)
            {
                rebuilt.AddRange(groupItems);
                inserted = true;
            }
        }
        if (!inserted) rebuilt.AddRange(groupItems);
        workspace.Projects = rebuilt;
        _workspace = workspace;
        await _store.SaveAsync(workspace, cancellationToken);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<RuntimeStatus> RunTemporaryCommandAsync(string projectId, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("临时命令不能为空。");
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        var project = workspace.Projects.FirstOrDefault(item => item.Id == projectId)
            ?? throw new KeyNotFoundException("项目不存在。");
        var task = new ProjectTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "临时命令",
            Command = command.Trim(),
            Mode = "once",
        };
        return await _supervisor.StartAsync(project, task, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectGroup>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        _workspace = workspace;
        return workspace.Groups;
    }

    public async Task<OpenSourceConfig> SaveOpenSourceAsync(OpenSourceConfig source, CancellationToken cancellationToken = default)
    {
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        source.Id = source.Id?.Trim() ?? string.Empty;
        source.Name = source.Name?.Trim() ?? string.Empty;
        source.Executable = source.Executable?.Trim() ?? string.Empty;
        source.Arguments = source.Arguments?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source.Id)) source.Id = Guid.NewGuid().ToString("N");
        OpenSourceValidation.Validate(source);
        var index = workspace.OpenSources.FindIndex(item => item.Id == source.Id);
        if (index < 0) workspace.OpenSources.Add(source); else workspace.OpenSources[index] = source;
        _workspace = workspace;
        await _store.SaveAsync(workspace, cancellationToken);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        return source;
    }

    public async Task DeleteOpenSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        sourceId = sourceId.Trim();
        var workspace = _workspace ?? await _store.LoadAsync(cancellationToken);
        if (string.Equals(sourceId, "vscode", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("默认 VS Code 软件源不能删除。");
        var removed = workspace.OpenSources.RemoveAll(source => source.Id == sourceId);
        if (removed == 0) throw new KeyNotFoundException("软件源不存在。");
        _workspace = workspace;
        await _store.SaveAsync(workspace, cancellationToken);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<OpenSourceConfig> GetOpenSources() => (_workspace?.OpenSources ?? []).AsReadOnly();

    public void OpenProjectWithSource(string directory, OpenSourceConfig source) =>
        _projectIntegration.OpenWithSource(directory, source);

    public Task<IReadOnlyList<ImportedProjectCandidate>> ReadWorkspaceImportCandidatesAsync(CancellationToken cancellationToken = default) =>
        _workspaceImport.ReadAsync(cancellationToken);

    public void OpenConfigDirectory() => _store.OpenConfigDirectory();

    public IReadOnlyList<LogEvent> GetRecentLogs(string runId, int limit = 500) =>
        _logs.Read(runId, limit);
}
