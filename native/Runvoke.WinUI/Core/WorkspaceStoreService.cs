using System.Text.Json;
using System.Diagnostics;
using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

public sealed class WorkspaceStoreService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Runvoke");

    public string StorePath => Path.Combine(ConfigDirectory, "projects.json");

    private IEnumerable<string> LegacyStorePaths
    {
        get
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(roaming, "dev.loopdeck.launcher", "projects.json");
            yield return Path.Combine(local, "dev.loopdeck.launcher", "projects.json");
        }
    }

    public void OpenConfigDirectory()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { ConfigDirectory },
        });
    }

    public async Task<WorkspaceStore> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ConfigDirectory);
        if (!File.Exists(StorePath))
        {
            var legacy = LegacyStorePaths.FirstOrDefault(File.Exists);
            if (legacy is not null)
            {
                File.Copy(legacy, StorePath, true);
                await using var migratedStream = File.OpenRead(StorePath);
                var migrated = await JsonSerializer.DeserializeAsync<WorkspaceStore>(migratedStream, JsonOptions, cancellationToken)
                    ?? CreateDefault();
                Normalize(migrated);
                await SaveAsync(migrated, cancellationToken);
                return migrated;
            }
            var empty = CreateDefault();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(StorePath);
        var workspace = await JsonSerializer.DeserializeAsync<WorkspaceStore>(stream, JsonOptions, cancellationToken)
            ?? CreateDefault();
        Normalize(workspace);
        return workspace;
    }

    public async Task SaveAsync(WorkspaceStore workspace, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var temporaryPath = StorePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, workspace, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, StorePath, true);
        }
        finally { _saveGate.Release(); }
    }

    private static WorkspaceStore CreateDefault() => new()
    {
        OpenSources =
        [
            new OpenSourceConfig
            {
                Id = "vscode",
                Name = "Visual Studio Code",
                Executable = "code",
                Arguments = "\"{directory}\"",
            },
        ],
    };

    private static void Normalize(WorkspaceStore workspace)
    {
        workspace.Projects = (workspace.Projects ?? []).Where(project => project is not null).ToList();
        workspace.Groups ??= [];
        workspace.OpenSources ??= [];
        workspace.Groups = (workspace.Groups ?? []).Where(group => group is not null).ToList();
        workspace.OpenSources = (workspace.OpenSources ?? []).Where(source => source is not null).ToList();
        foreach (var group in workspace.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id)) group.Id = Guid.NewGuid().ToString("N");
            group.Name = group.Name?.Trim() ?? string.Empty;
        }
        foreach (var source in workspace.OpenSources)
        {
            if (string.IsNullOrWhiteSpace(source.Id)) source.Id = Guid.NewGuid().ToString("N");
            source.Name = source.Name?.Trim() ?? string.Empty;
            source.Executable ??= string.Empty;
            source.Arguments ??= string.Empty;
        }
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        workspace.OpenSources = workspace.OpenSources
            .Where(source => !string.Equals(source.Id, "zed", StringComparison.OrdinalIgnoreCase) &&
                IsValidOpenSource(source) && sourceIds.Add(source.Id))
            .ToList();
        if (workspace.OpenSources.Count == 0)
            workspace.OpenSources.Add(new OpenSourceConfig
            {
                Id = "vscode",
                Name = "Visual Studio Code",
                Executable = "code",
                Arguments = "\"{directory}\"",
            });
        workspace.Mcp ??= new McpConfig();
        foreach (var project in workspace.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.Id)) project.Id = Guid.NewGuid().ToString("N");
            project.Name ??= string.Empty;
            project.Directory ??= string.Empty;
            project.Command ??= string.Empty;
            if (!string.IsNullOrWhiteSpace(project.Directory))
            {
                try { project.Directory = PathNormalization.Directory(project.Directory); }
                catch (ArgumentException) { }
                catch (IOException) { }
            }
            project.Env = (project.Env ?? []).Where(variable => variable is not null).ToList();
            foreach (var variable in project.Env)
            {
                variable.Key ??= string.Empty;
                variable.Value ??= string.Empty;
            }
            project.Tasks = (project.Tasks ?? []).Where(task => task is not null).ToList();
            if (project.Tasks.Count == 0 && !string.IsNullOrWhiteSpace(project.Command))
            {
                project.Tasks.Add(new ProjectTask
                {
                    Id = "default",
                    Name = "开发服务器",
                    Command = project.Command,
                    Mode = "service",
                });
            }
            if (project.Tasks.Count > 3) project.Tasks = project.Tasks.Take(3).ToList();
            foreach (var task in project.Tasks)
            {
                if (string.IsNullOrWhiteSpace(task.Id)) task.Id = Guid.NewGuid().ToString("N");
                task.Name = string.IsNullOrWhiteSpace(task.Name) ? "未命名任务" : task.Name.Trim();
                task.Command = task.Command?.Trim() ?? string.Empty;
                task.Mode = string.Equals(task.Mode, "once", StringComparison.OrdinalIgnoreCase) ? "once" : "service";
            }
            project.Command = project.Tasks.FirstOrDefault()?.Command ?? project.Command ?? string.Empty;
            project.GroupId = project.GroupId is not null && workspace.Groups.Any(group => group.Id == project.GroupId)
                ? project.GroupId
                : null;
        }
    }

    private static bool IsValidOpenSource(OpenSourceConfig source)
    {
        try
        {
            OpenSourceValidation.Validate(source);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }
}
