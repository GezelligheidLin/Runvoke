using System.ComponentModel;
using System.Diagnostics;
using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

public sealed class ProjectIntegrationService
{
    public async Task<GitProjectStatus?> GetGitStatusAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return null;
        var branch = await RunGitAsync(directory, ["branch", "--show-current"], cancellationToken);
        var porcelain = await RunGitAsync(directory, ["status", "--porcelain"], cancellationToken);
        if (branch is null && porcelain is null) return null;
        var lines = porcelain?.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries) ?? [];
        return new GitProjectStatus(
            string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
            lines.Count(line => line.Length > 0 && line[0] != ' '),
            lines.Count(line => line.Length > 1 && line[1] != ' '));
    }

    public void OpenDirectory(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"项目目录不存在：{directory}");
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { directory },
        });
    }

    public void OpenInVisualStudioCode(string directory)
    {
        OpenWithSource(directory, new OpenSourceConfig { Id = "vscode", Name = "Visual Studio Code", Executable = "code", Arguments = "\"{directory}\"" });
    }

    public void OpenWithSource(string directory, OpenSourceConfig source)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"项目目录不存在：{directory}");
        OpenSourceValidation.Validate(source);
        var arguments = (source.Arguments ?? string.Empty).Replace("{directory}", directory, StringComparison.Ordinal);
        var info = new ProcessStartInfo
        {
            FileName = source.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in SplitArguments(arguments)) info.ArgumentList.Add(argument);
        _ = Process.Start(info) ?? throw new InvalidOperationException("无法启动软件源。");
    }

    private static IEnumerable<string> SplitArguments(string value)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in value)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(character);
        }
        if (quoted) throw new InvalidOperationException("软件源参数包含未闭合的引号。");
        if (current.Length > 0) yield return current.ToString();
    }

    private static async Task<string?> RunGitAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return null;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception) { return null; }
    }
}
