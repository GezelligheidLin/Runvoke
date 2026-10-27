using System.Text.Json.Serialization;

namespace Runvoke.WinUI.Models;

public sealed class EnvVariable
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class ProjectTask
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Mode { get; set; } = "service";
    [JsonIgnore]
    public string ModeText => Mode == "once" ? "一次任务" : "常驻服务";
    [JsonIgnore]
    public string RuntimeStateText { get; set; } = "待运行";
    [JsonIgnore]
    public string ActionText => RuntimeStateText.StartsWith("运行中", StringComparison.Ordinal) ||
        RuntimeStateText.StartsWith("启动中", StringComparison.Ordinal) ||
        RuntimeStateText.StartsWith("停止中", StringComparison.Ordinal)
        ? "停止"
        : "启动";
}

public sealed class ProjectConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public string Command { get; set; } = string.Empty;
    public List<EnvVariable> Env { get; set; } = [];
    public ushort? Port { get; set; }
    public List<ProjectTask> Tasks { get; set; } = [];
    [JsonIgnore]
    public string GroupLabel { get; set; } = "未分组";
    [JsonIgnore]
    public bool ShowGroupLabel { get; set; }
    [JsonIgnore]
    public string StatusLabel { get; set; } = string.Empty;
    [JsonIgnore]
    public string PortLabel => Port is { } port ? $":{port}" : string.Empty;
}

public sealed class ProjectGroup
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Collapsed { get; set; }
    [JsonIgnore]
    public string CountLabel { get; set; } = "0";
}

public sealed class OpenSourceConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

public sealed class McpConfig
{
    public bool Enabled { get; set; }
    public ushort Port { get; set; } = 38465;
    public string Token { get; set; } = string.Empty;
}

public sealed class WorkspaceStore
{
    public List<ProjectConfig> Projects { get; set; } = [];
    public List<ProjectGroup> Groups { get; set; } = [];
    public List<OpenSourceConfig> OpenSources { get; set; } = [];
    public McpConfig Mcp { get; set; } = new();
}

public sealed record TemporaryCommandRequest(string ProjectId, string Command);

public sealed class RuntimeStatus : EventArgs
{
    public RuntimeStatus(string runId, string projectId, string taskId, string taskName, string mode, string state, uint? pid, long? startedAt, int? exitCode)
    {
        RunId = runId; ProjectId = projectId; TaskId = taskId; TaskName = taskName; Mode = mode; State = state; Pid = pid; StartedAt = startedAt; ExitCode = exitCode;
    }
    public string RunId { get; }
    public string ProjectId { get; }
    public string TaskId { get; }
    public string TaskName { get; }
    public string Mode { get; }
    public string State { get; }
    public uint? Pid { get; }
    public long? StartedAt { get; }
    public int? ExitCode { get; }
    public string StateText => State switch
    {
        "starting" => "启动中",
        "running" => "运行中",
        "stopping" => "停止中",
        "stopped" => "已停止",
        "succeeded" => "已完成",
        "failed" => "失败",
        _ => State,
    };
    public string ExitCodeText => ExitCode is { } code ? $"退出码 {code}" : Pid is { } processId ? $"PID {processId}" : "—";
    [JsonIgnore]
    public bool CanRemove => State is not ("starting" or "running" or "stopping");
}

public sealed class LogEvent : EventArgs
{
    public LogEvent(string runId, string projectId, string stream, string message, long timestamp, string mode)
    {
        RunId = runId; ProjectId = projectId; Stream = stream; Message = message; Timestamp = timestamp; Mode = mode;
    }
    public string RunId { get; }
    public string ProjectId { get; }
    public string Stream { get; }
    public string Message { get; init; }
    public long Timestamp { get; }
    public string Mode { get; }
}

public sealed class LogLineView
{
    public LogLineView(LogEvent source, IReadOnlyList<LogLineSegment> segments)
    {
        Source = source;
        Segments = segments;
    }

    public LogEvent Source { get; }
    public string Stream => Source.Stream;
    public string TimestampText => DateTimeOffset.FromUnixTimeMilliseconds(Source.Timestamp).ToLocalTime().ToString("HH:mm:ss");
    public IReadOnlyList<LogLineSegment> Segments { get; }
}

public sealed class LogLineSegment
{
    public string Text { get; init; } = string.Empty;
    public string? Url { get; init; }
    public bool IsLink => Url is not null;
}

public sealed record GitProjectStatus(
    string? Branch,
    int StagedChanges,
    int UnstagedChanges);
