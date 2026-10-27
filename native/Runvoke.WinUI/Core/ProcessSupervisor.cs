using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

public sealed class ProcessSupervisor
{
    private readonly ConcurrentDictionary<string, ManagedRun> _runs = [];
    private readonly ConcurrentDictionary<string, RuntimeStatus> _history = [];
    private readonly object _startGate = new();
    private int _stopAllInProgress;

    public event EventHandler<LogEvent>? LogReceived;
    public event EventHandler<RuntimeStatus>? RuntimeStatusChanged;

    public bool HasActiveRuns(string projectId) =>
        _runs.Values.Any(run => run.Project.Id == projectId);

    public async Task StopProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var runIds = _runs.Values
            .Where(run => string.Equals(run.Project.Id, projectId, StringComparison.Ordinal))
            .Select(run => run.RunId)
            .ToArray();
        if (runIds.Length == 0) return;
        await Task.WhenAll(runIds.Select(async runId =>
        {
            try { await StopAsync(runId, cancellationToken); }
            catch (KeyNotFoundException) { }
        }));
    }

    public Task<RuntimeStatus> StartAsync(
        ProjectConfig project,
        ProjectTask task,
        CancellationToken cancellationToken = default)
    {
        lock (_startGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopAllInProgress) != 0)
                throw new InvalidOperationException("正在停止全部运行实例，请稍后重试。");
            if (!Directory.Exists(project.Directory))
                throw new DirectoryNotFoundException($"项目目录不存在：{project.Directory}");
            if (string.IsNullOrWhiteSpace(task.Command))
                throw new InvalidOperationException("任务命令不能为空。");
            if (string.Equals(task.Mode, "service", StringComparison.OrdinalIgnoreCase) &&
                _runs.Values.Any(run => run.Project.Id == project.Id &&
                    run.Task.Id == task.Id &&
                    !run.Completion.Task.IsCompleted))
                throw new InvalidOperationException("该常驻任务已经在运行。");

        var runId = Guid.NewGuid().ToString("N");
        var process = new Process
        {
            StartInfo = CreateStartInfo(project, task),
            EnableRaisingEvents = true,
        };
        var run = new ManagedRun(runId, project, task, process);
        if (!process.Start()) throw new InvalidOperationException("无法启动任务进程。");
        try
        {
            run.Job = WindowsJob.Attach(process);
        }
        catch
        {
            // A very short-lived command may finish before Windows lets us
            // assign its handle to a job. It no longer has descendants to
            // reclaim, so keep the completed process and publish its result.
            if (process.HasExited)
            {
                run.Job = null;
            }
            else
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
                throw;
            }
        }
        _runs[runId] = run;
        var status = run.CreateStatus("running", process.Id, null);
        PublishStatus(status);
        LogReceived?.Invoke(this, new LogEvent(
            run.RunId,
            run.Project.Id,
            "system",
            $"已启动「{run.Task.Name}」· PID {process.Id}",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "append"));

        // Create both readers before wiring the exit callback. A very short-lived
        // command can exit between Process.Start and the callback registration;
        // the completion path must still wait for the final stdout/stderr bytes.
        run.StreamTasks.Add(ReadStreamAsync(run, process.StandardOutput, "stdout"));
        run.StreamTasks.Add(ReadStreamAsync(run, process.StandardError, "stderr"));
        process.Exited += (_, _) => _ = CompleteAsync(run);
        if (process.HasExited) _ = CompleteAsync(run);
            return Task.FromResult(status);
        }
    }

    public async Task<RuntimeStatus> StopAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var run))
            throw new KeyNotFoundException($"运行实例不存在：{runId}");

        run.StopRequested = true;
        PublishStatus(run.CreateStatus("stopping", run.Pid, null));
        run.Job?.Terminate();
        try
        {
            if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        try { await run.Process.WaitForExitAsync(cancellationToken); }
        catch (InvalidOperationException) { }
        try { return await run.Completion.Task.WaitAsync(cancellationToken); }
        catch (InvalidOperationException)
        {
            int? exitCode = null;
            try { if (run.Process.HasExited) exitCode = run.Process.ExitCode; } catch (InvalidOperationException) { }
            var stopped = run.CreateStatus("stopped", run.Pid, exitCode);
            PublishStatus(stopped);
            return stopped;
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopAllInProgress, 1) != 0)
            throw new InvalidOperationException("已经有一个停止全部任务的操作正在进行。");
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activeRunIds = _runs.Keys.ToArray();
                if (activeRunIds.Length == 0) return;
                await Task.WhenAll(activeRunIds.Select(async runId =>
                {
                    try { await StopAsync(runId, cancellationToken); }
                    catch (KeyNotFoundException) { /* completed between snapshot and stop */ }
                }));
            }
        }
        finally { Volatile.Write(ref _stopAllInProgress, 0); }
    }

    private async Task CompleteAsync(ManagedRun run)
    {
        try { await run.Process.WaitForExitAsync(); } catch { return; }
        try { await Task.WhenAll(run.StreamTasks); } catch { }
        if (!_runs.TryRemove(run.RunId, out _)) return;
        var exitCode = run.Process.ExitCode;
        var state = run.StopRequested ? "stopped" : exitCode == 0 ? "succeeded" : "failed";
        var status = run.CreateStatus(state, run.Process.Id, exitCode);
        PublishStatus(status);
        run.Completion.TrySetResult(status);
        run.Dispose();
    }

    public IReadOnlyList<RuntimeStatus> ListRuns() =>
        _history.Values.OrderByDescending(status => status.StartedAt ?? 0).ToArray();

    public void ForgetRun(string runId) => _history.TryRemove(runId, out _);

    private void PublishStatus(RuntimeStatus status)
    {
        _history[status.RunId] = status;
        RuntimeStatusChanged?.Invoke(this, status);
    }

    private async Task ReadStreamAsync(ManagedRun run, StreamReader reader, string stream)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var length = await reader.ReadAsync(buffer.AsMemory());
                if (length == 0) break;
                EmitParserUpdates(run, stream, run.Parser(stream).Feed(buffer.AsSpan(0, length)));
            }
        }
        catch (ObjectDisposedException) { }
        finally
        {
            EmitParserUpdates(run, stream, run.Parser(stream).Finish());
        }
    }

    private void EmitParserUpdates(
        ManagedRun run,
        string stream,
        IReadOnlyList<TerminalLogUpdate> updates)
    {
        foreach (var update in updates)
        {
            var message = run.Redact(update.Message);
            var mode = update.Mode == "append" && IsWebpackProgress(update.Message)
                ? "progress"
                : update.Mode;
            LogReceived?.Invoke(this, new LogEvent(
                run.RunId,
                run.Project.Id,
                stream,
                message,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                mode));
        }
    }

    private static bool IsWebpackProgress(string message)
    {
        var lower = message.ToLowerInvariant();
        var marker = lower.IndexOf("[webpack.progress]", StringComparison.Ordinal);
        if (marker < 0) return false;
        var rest = lower[(marker + "[webpack.progress]".Length)..];
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[0])) return false;
        rest = rest.TrimStart();
        var digits = rest.TakeWhile(char.IsDigit).Count();
        return digits is >= 1 and <= 3 && rest.Length > digits && rest[digits] == '%' &&
            (rest.Length == digits + 1 || char.IsWhiteSpace(rest[digits + 1]));
    }

    private static ProcessStartInfo CreateStartInfo(ProjectConfig project, ProjectTask task)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WindowsShellResolver.ResolvePowerShell(),
            WorkingDirectory = project.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(WindowsShellResolver.BuildScript(task.Command));
        foreach (var variable in project.Env)
        {
            if (string.IsNullOrWhiteSpace(variable.Key) || variable.Key.Length > 128 ||
                variable.Key.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.'))) continue;
            startInfo.Environment[variable.Key] = variable.Value;
        }
        return startInfo;
    }

    private sealed class ManagedRun : IDisposable
    {
        public ManagedRun(string runId, ProjectConfig project, ProjectTask task, Process process)
        {
            RunId = runId;
            Project = project;
            Task = task;
            Process = process;
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _secrets = project.Env.Select(variable => variable.Value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        }

        public string RunId { get; }
        public ProjectConfig Project { get; }
        public ProjectTask Task { get; }
        public Process Process { get; }
        public long StartedAt { get; }
        public int Pid => Process.Id;
        public WindowsJob? Job { get; set; }
        public bool StopRequested { get; set; }
        public TaskCompletionSource<RuntimeStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TerminalLogParser> _parsers = [];
        private readonly string[] _secrets;
        public List<Task> StreamTasks { get; } = [];

        public TerminalLogParser Parser(string stream)
        {
            if (!_parsers.TryGetValue(stream, out var parser))
            {
                parser = new TerminalLogParser();
                _parsers[stream] = parser;
            }
            return parser;
        }

        public string Redact(string message)
        {
            foreach (var secret in _secrets) message = message.Replace(secret, "***", StringComparison.Ordinal);
            return message;
        }

        public RuntimeStatus CreateStatus(string state, int? pid, int? exitCode) => new(
            RunId, Project.Id, Task.Id, Task.Name, Task.Mode, state,
            pid is null ? null : (uint)pid, StartedAt, exitCode);

        public void Dispose()
        {
            Job?.Dispose();
            Process.Dispose();
        }
    }

    private sealed class WindowsJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private IntPtr _handle;

        private WindowsJob(IntPtr handle) => _handle = handle;

        public static WindowsJob Attach(Process process)
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) ThrowLastError("无法创建 Windows Job Object");
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, memory, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, memory, (uint)size))
                    ThrowLastError("无法配置 Windows Job Object");
                if (!AssignProcessToJobObject(handle, process.Handle))
                    ThrowLastError("无法将任务进程加入 Windows Job Object");
                return new WindowsJob(handle);
            }
            catch
            {
                CloseHandle(handle);
                throw;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        public void Terminate()
        {
            if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        private static void ThrowLastError(string message) =>
            throw new InvalidOperationException($"{message}：{Marshal.GetLastWin32Error()}");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
