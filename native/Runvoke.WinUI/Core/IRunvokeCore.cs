using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

public interface IRunvokeCore
{
    Task<WorkspaceStore> LoadWorkspaceAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectConfig>> ListProjectsAsync(CancellationToken cancellationToken = default);

    Task SaveWorkspaceAsync(WorkspaceStore workspace, CancellationToken cancellationToken = default);

    Task SaveProjectAsync(ProjectConfig project, CancellationToken cancellationToken = default);

    Task DeleteProjectAsync(string projectId, CancellationToken cancellationToken = default);

    Task SaveGroupAsync(ProjectGroup group, CancellationToken cancellationToken = default);

    Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default);

    Task<RuntimeStatus> StartTaskAsync(
        string projectId,
        string taskId,
        CancellationToken cancellationToken = default);

    Task<RuntimeStatus> StopRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task StopAllAsync(CancellationToken cancellationToken = default);

    void ForgetRun(string runId);

    void ClearLogs(string runId);

    IReadOnlyList<RuntimeStatus> ListRuns();

    Task<GitProjectStatus?> GetGitStatusAsync(string directory, CancellationToken cancellationToken = default);

    void OpenProjectDirectory(string directory);

    void OpenProjectInVisualStudioCode(string directory);

    Task<ProjectConfig> CreateDetectedProjectAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>Reads common project manifests without saving or running the project.</summary>
    Task<ProjectConfig> DetectProjectAsync(string directory, CancellationToken cancellationToken = default);

    Task ReorderProjectsAsync(IReadOnlyList<string> orderedProjectIds, CancellationToken cancellationToken = default);

    Task MoveProjectAsync(string projectId, string? groupId, int targetIndex, CancellationToken cancellationToken = default);

    Task<RuntimeStatus> RunTemporaryCommandAsync(string projectId, string command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectGroup>> ListGroupsAsync(CancellationToken cancellationToken = default);

    Task<OpenSourceConfig> SaveOpenSourceAsync(OpenSourceConfig source, CancellationToken cancellationToken = default);

    Task DeleteOpenSourceAsync(string sourceId, CancellationToken cancellationToken = default);

    IReadOnlyList<OpenSourceConfig> GetOpenSources();

    void OpenProjectWithSource(string directory, OpenSourceConfig source);

    Task<IReadOnlyList<ImportedProjectCandidate>> ReadWorkspaceImportCandidatesAsync(CancellationToken cancellationToken = default);

    void OpenConfigDirectory();

    IReadOnlyList<LogEvent> GetRecentLogs(string runId, int limit = 500);

    event EventHandler<LogEvent>? LogReceived;
    event EventHandler<RuntimeStatus>? RuntimeStatusChanged;
    event EventHandler? WorkspaceChanged;
}
