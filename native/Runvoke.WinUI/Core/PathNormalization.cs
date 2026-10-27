namespace Runvoke.WinUI.Core;

internal static class PathNormalization
{
    public static string Directory(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("目录不能为空。", nameof(value));
        var full = Path.GetFullPath(value.Trim());
        var root = Path.GetPathRoot(full);
        return root is not null && string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? root
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
