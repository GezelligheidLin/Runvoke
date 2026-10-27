using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

internal static class OpenSourceValidation
{
    public static void Validate(OpenSourceConfig source)
    {
        if (source is null) throw new InvalidOperationException("软件源不能为空。");
        if (string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 64)
            throw new InvalidOperationException("软件源标识无效。");
        if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 120)
            throw new InvalidOperationException("软件源名称不能为空且不能超过 120 个字符。");
        if (string.IsNullOrWhiteSpace(source.Executable) || source.Executable.Length > 1_024)
            throw new InvalidOperationException("软件源程序路径不能为空且不能超过 1024 个字符。");
        var arguments = source.Arguments ?? string.Empty;
        if (arguments.Length > 4_096 ||
            source.Id.Any(char.IsControl) || source.Name.Any(char.IsControl) ||
            source.Executable.Any(char.IsControl) || arguments.Any(char.IsControl))
            throw new InvalidOperationException("软件源配置包含无效字符或过长内容。");
        if (!arguments.Contains("{directory}", StringComparison.Ordinal))
            throw new InvalidOperationException("打开目录的参数中必须包含 {directory}。");

        var executable = source.Executable.Trim();
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        if (name is "cmd" or "command" or "powershell" or "pwsh" or "sh" or "bash" or
            "zsh" or "fish" or "wscript" or "cscript" ||
            Path.GetExtension(executable).ToLowerInvariant() is ".bat" or ".cmd" or ".ps1" or ".sh")
            throw new InvalidOperationException("软件源不能直接配置 shell 程序，请填写编辑器或其他直接启动的程序。");
    }
}
