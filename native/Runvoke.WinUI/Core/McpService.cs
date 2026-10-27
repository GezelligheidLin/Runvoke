using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

public sealed class McpService(IRunvokeCore core)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly UpdateService _updates = new();
    private UpdateInfo? _lastUpdate;
    public event EventHandler<IReadOnlyList<ImportedProjectCandidate>>? AgentImportRequested;
    public event EventHandler<JsonElement>? SettingsUpdateRequested;
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private string _token = string.Empty;
    public bool PreviewUpdatesEnabled { get; set; }

    public bool IsRunning => _listener?.IsListening == true;
    public int Port { get; private set; }
    public event EventHandler<UpdateInfo>? InstallUpdateRequested;

    public static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public async Task StartAsync(int port, string token, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("MCP 令牌不能为空。");
        if (port is < 1024 or > 65535) throw new InvalidOperationException("MCP 端口必须位于 1024-65535。");
        _token = token;
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
        _listener.Start();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => AcceptLoopAsync(_cancellation.Token), _cancellation.Token);
    }

    public async Task StopAsync()
    {
        _cancellation?.Cancel();
        _listener?.Stop();
        if (_loop is not null)
        {
            try { await _loop; } catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
        _loop = null;
        _listener = null;
        _cancellation?.Dispose();
        _cancellation = null;
        _token = string.Empty;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true } listener)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(context), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.Headers["Authorization"], $"Bearer {_token}", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 401;
                await WriteAsync(context, new { error = "unauthorized" });
                return;
            }
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                await WriteAsync(context, new { error = "method_not_allowed" });
                return;
            }
            if (context.Request.ContentLength64 > 1_048_576)
            {
                context.Response.StatusCode = 413;
                await WriteAsync(context, new { error = "request_too_large" });
                return;
            }
            using var document = await JsonDocument.ParseAsync(context.Request.InputStream);
            var root = document.RootElement;
            var method = root.TryGetProperty("method", out var methodValue) ? methodValue.GetString() : null;
            var id = root.TryGetProperty("id", out var idValue) ? idValue.Clone() : JsonValue("1");
            var result = await DispatchAsync(method, root.TryGetProperty("params", out var parameters) ? parameters : default);
            await WriteAsync(context, new { jsonrpc = "2.0", id, result });
        }
        catch (Exception)
        {
            context.Response.StatusCode = 400;
            await WriteAsync(context, new { error = "request_failed" });
        }
        finally { context.Response.Close(); }
    }

    private async Task<object> DispatchAsync(string? method, JsonElement parameters)
    {
        return method switch
        {
            "initialize" => new { protocolVersion = "2025-06-18", capabilities = new { }, serverInfo = new { name = "runvoke", version = "native" } },
            "tools/list" => new { tools = new[]
            {
                new { name = "list_projects", description = "列出已接入项目（不返回环境变量值）", inputSchema = new { type = "object" } },
                new { name = "get_projects", description = "读取项目和任务列表", inputSchema = new { type = "object" } },
                new { name = "list_groups", description = "读取项目分组", inputSchema = new { type = "object" } },
                new { name = "list_runs", description = "读取运行实例和状态", inputSchema = new { type = "object" } },
                new { name = "get_logs", description = "读取运行实例近期日志", inputSchema = new { type = "object" } },
                new { name = "save_project", description = "编辑已有项目，不创建或删除项目", inputSchema = new { type = "object" } },
                new { name = "start_project_task", description = "启动指定任务", inputSchema = new { type = "object" } },
                new { name = "start_task", description = "启动指定任务", inputSchema = new { type = "object" } },
                new { name = "run_project_command", description = "在指定项目目录运行一次性命令", inputSchema = new { type = "object" } },
                new { name = "stop_run", description = "停止指定运行实例", inputSchema = new { type = "object" } },
                new { name = "stop_all", description = "停止全部活动任务", inputSchema = new { type = "object" } },
                new { name = "stop_all_runs", description = "停止全部活动任务", inputSchema = new { type = "object" } },
                new { name = "save_group", description = "新建或编辑项目分组", inputSchema = new { type = "object" } },
                new { name = "move_project", description = "移动项目到分组和位置", inputSchema = new { type = "object" } },
                new { name = "check_updates", description = "请求 Runvoke 检查更新", inputSchema = new { type = "object" } },
                new { name = "request_install_update", description = "请求在 Runvoke 前台确认安装更新", inputSchema = new { type = "object" } },
                new { name = "request_project_import", description = "提交 Agent 候选项目并请求前台确认导入", inputSchema = new { type = "object" } },
                new { name = "update_settings", description = "请求修改受控应用设置", inputSchema = new { type = "object" } },
            } },
            "tools/call" => await CallToolAsync(parameters),
            _ => throw new InvalidOperationException("不支持的 MCP 方法。"),
        };
    }

    private async Task<object> CallToolAsync(JsonElement parameters)
    {
        var name = parameters.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
        return name switch
        {
            "list_projects" => await GetProjectsAsync(),
            "get_projects" => await GetProjectsAsync(),
            "list_groups" => await core.ListGroupsAsync(),
            "list_runs" => core.ListRuns(),
            "get_logs" => await GetLogsAsync(arguments),
            "save_project" => await SaveProjectAsync(arguments),
            "start_project_task" => await StartTaskAsync(arguments),
            "start_task" => await StartTaskAsync(arguments),
            "run_project_command" => await RunProjectCommandAsync(arguments),
            "stop_run" => await StopRunAsync(arguments),
            "stop_all" => await StopAllAsync(),
            "stop_all_runs" => await StopAllAsync(),
            "save_group" => await SaveGroupAsync(arguments),
            "move_project" => await MoveProjectAsync(arguments),
            "check_updates" => await CheckUpdatesAsync(),
            "request_install_update" => RequestInstallUpdate(),
            "request_project_import" => RequestAgentImport(arguments),
            "update_settings" => RequestSettingsUpdate(arguments),
            _ => throw new InvalidOperationException("不存在或不允许的 MCP 工具。"),
        };
    }

    private async Task<object> GetProjectsAsync()
    {
        var workspace = await core.LoadWorkspaceAsync();
        return workspace.Projects.Select(project => new
        {
            project.Id,
            project.Name,
            project.Directory,
            project.GroupId,
            Tasks = project.Tasks.Select(task => new { task.Id, task.Name, task.Command, task.Mode }),
            EnvironmentVariableKeys = project.Env.Select(variable => variable.Key).ToArray(),
        }).ToArray();
    }

    private async Task<object> SaveProjectAsync(JsonElement arguments)
    {
        var id = Required(arguments, "id");
        var workspace = await core.LoadWorkspaceAsync();
        var existing = workspace.Projects.FirstOrDefault(project => project.Id == id)
            ?? throw new KeyNotFoundException("项目不存在；请使用项目导入请求。");
        existing.Name = Required(arguments, "name");
        existing.Directory = Required(arguments, "directory");
        if (arguments.TryGetProperty("groupId", out var group) && group.ValueKind == JsonValueKind.Null) existing.GroupId = null;
        else if (arguments.TryGetProperty("groupId", out group) && group.ValueKind == JsonValueKind.String) existing.GroupId = group.GetString();
        if (arguments.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number && port.TryGetUInt16(out var parsedPort)) existing.Port = parsedPort;
        if (arguments.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            existing.Tasks = JsonSerializer.Deserialize<List<ProjectTask>>(tasks.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Take(3).ToList() ?? [];
        existing.Command = existing.Tasks.FirstOrDefault()?.Command ?? string.Empty;
        await core.SaveProjectAsync(existing);
        return new { existing.Id, existing.Name, existing.Directory, existing.GroupId, existing.Port, existing.Tasks };
    }

    private async Task<object> GetLogsAsync(JsonElement arguments)
    {
        var runId = Required(arguments, "runId");
        var limit = arguments.TryGetProperty("limit", out var limitValue) && limitValue.TryGetInt32(out var parsed) ? parsed : 200;
        var secrets = (await core.LoadWorkspaceAsync()).Projects
            .SelectMany(project => project.Env)
            .Select(variable => variable.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return core.GetRecentLogs(runId, Math.Clamp(limit, 1, 500))
            .Select(entry => new LogEvent(entry.RunId, entry.ProjectId, entry.Stream,
                secrets.Aggregate(entry.Message, (message, secret) => message.Replace(secret, "***", StringComparison.Ordinal)),
                entry.Timestamp, entry.Mode))
            .ToArray();
    }

    private async Task<object> StartTaskAsync(JsonElement arguments) =>
        await core.StartTaskAsync(Required(arguments, "projectId"), Required(arguments, "taskId"));

    private async Task<object> StopRunAsync(JsonElement arguments) =>
        await core.StopRunAsync(Required(arguments, "runId"));

    private async Task<object> StopAllAsync()
    {
        await core.StopAllAsync();
        return new { stopped = true };
    }

    private async Task<object> RunProjectCommandAsync(JsonElement arguments) =>
        await core.RunTemporaryCommandAsync(Required(arguments, "projectId"), Required(arguments, "command"));

    private async Task<object> SaveGroupAsync(JsonElement arguments)
    {
        var group = new ProjectGroup
        {
            Id = arguments.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
            Name = Required(arguments, "name"),
            Collapsed = arguments.TryGetProperty("collapsed", out var collapsed) && collapsed.ValueKind == JsonValueKind.True,
        };
        await core.SaveGroupAsync(group);
        return group;
    }

    private async Task<object> MoveProjectAsync(JsonElement arguments)
    {
        var groupId = arguments.TryGetProperty("groupId", out var group) && group.ValueKind != JsonValueKind.Null ? group.GetString() : null;
        var targetIndex = arguments.TryGetProperty("targetIndex", out var index) && index.TryGetInt32(out var parsed) ? parsed : 0;
        await core.MoveProjectAsync(Required(arguments, "projectId"), groupId, targetIndex);
        return new { moved = true };
    }

    private async Task<object> CheckUpdatesAsync()
    {
        _lastUpdate = await _updates.CheckAsync(PreviewUpdatesEnabled);
        return _lastUpdate is null ? new { update = (object?)null } : _lastUpdate;
    }

    private object RequestInstallUpdate()
    {
        if (_lastUpdate is null) throw new InvalidOperationException("没有可安装的更新，请先检查更新。");
        InstallUpdateRequested?.Invoke(this, _lastUpdate);
        return new { requested = true, version = _lastUpdate.Version, requiresForegroundConfirmation = true };
    }

    private object RequestAgentImport(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("projects 必须是候选项目数组。");
        var candidates = new List<ImportedProjectCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in projects.EnumerateArray().Take(100))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString()?.Trim() : null;
            var directoryValue = item.TryGetProperty("directory", out var directoryProperty) ? directoryProperty.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directoryValue)) continue;
            var rawDirectory = directoryValue.Trim();
            if (!Path.IsPathRooted(rawDirectory)) continue;
            string directory;
            try { directory = PathNormalization.Directory(rawDirectory); }
            catch (ArgumentException) { continue; }
            catch (IOException) { continue; }
            if (!Directory.Exists(directory) || !seen.Add(directory)) continue;
            var suggested = item.TryGetProperty("suggestedCommand", out var commandValue) && commandValue.ValueKind == JsonValueKind.String
                ? commandValue.GetString()?.Trim()
                : null;
            candidates.Add(new ImportedProjectCandidate(name, directory, "Agent", string.IsNullOrWhiteSpace(suggested) ? null : suggested));
        }
        if (candidates.Count == 0) throw new InvalidOperationException("候选项目目录无效或已重复。");
        AgentImportRequested?.Invoke(this, candidates);
        return new { requested = true, count = candidates.Count, requiresForegroundConfirmation = true };
    }

    private object RequestSettingsUpdate(JsonElement arguments)
    {
        SettingsUpdateRequested?.Invoke(this, arguments.Clone());
        return new { accepted = true, requiresForegroundConfirmation = true };
    }

    private static string Required(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidOperationException($"缺少参数：{name}");

    private static JsonElement JsonValue(string value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static async Task WriteAsync(HttpListenerContext context, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }
}
