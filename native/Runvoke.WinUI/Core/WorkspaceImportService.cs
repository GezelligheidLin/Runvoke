using System.Text.Json;
using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

public sealed class WorkspaceImportService
{
    private static readonly (string Name, string RelativePath)[] Sources =
    [
        ("VS Code", Path.Combine("Code", "User", "globalStorage", "storage.json")),
        ("Code Insiders", Path.Combine("Code - Insiders", "User", "globalStorage", "storage.json")),
        ("VSCodium", Path.Combine("VSCodium", "User", "globalStorage", "storage.json")),
        ("Cursor", Path.Combine("Cursor", "User", "globalStorage", "storage.json")),
    ];

    public async Task<IReadOnlyList<ImportedProjectCandidate>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var result = new List<ImportedProjectCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var detection = new ProjectDetectionService();
        foreach (var (source, relativePath) in Sources)
        {
            var file = Path.Combine(appData, relativePath);
            if (!File.Exists(file)) continue;
            try
            {
                await using var stream = File.OpenRead(file);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                foreach (var path in ReadPaths(document.RootElement))
                {
                    foreach (var directory in NormalizeMany(path))
                    {
                        if (!Directory.Exists(directory) || !seen.Add(directory)) continue;
                        try
                        {
                            var detected = await detection.CreateProjectAsync(directory, cancellationToken);
                            result.Add(new ImportedProjectCandidate(detected.Name, directory, source, detected.Command));
                        }
                        catch (ArgumentException)
                        {
                            result.Add(new ImportedProjectCandidate(new DirectoryInfo(directory).Name, directory, source));
                        }
                        catch (IOException)
                        {
                            result.Add(new ImportedProjectCandidate(new DirectoryInfo(directory).Name, directory, source));
                        }
                        catch (UnauthorizedAccessException)
                        {
                            result.Add(new ImportedProjectCandidate(new DirectoryInfo(directory).Name, directory, source));
                        }
                    }
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private static IEnumerable<string> ReadPaths(JsonElement root)
    {
        if (root.TryGetProperty("workbench.history", out var history) &&
            history.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                foreach (var key in new[] { "folderUri", "fileUri", "workspace" })
                {
                    if (!entry.TryGetProperty(key, out var value)) continue;
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var path = value.GetString();
                        if (!string.IsNullOrWhiteSpace(path)) yield return path;
                    }
                    else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("configPath", out var configPath) && configPath.ValueKind == JsonValueKind.String)
                    {
                        var path = configPath.GetString();
                        if (!string.IsNullOrWhiteSpace(path)) yield return path;
                    }
                }
            }
        }

        if (root.TryGetProperty("profileAssociations", out var associations) && associations.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "workspaces", "folders" })
            {
                if (!associations.TryGetProperty(key, out var associationEntries) || associationEntries.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in associationEntries.EnumerateObject()) yield return entry.Name;
            }
        }
    }

    private static IReadOnlyList<string> NormalizeMany(string value)
    {
        var result = new List<string>();
        try
        {
            var uriValue = value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(value).LocalPath
                : value;
            var fullPath = Path.GetFullPath(uriValue);
            if (Path.GetExtension(fullPath).Equals(".code-workspace", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
            {
                using var workspace = JsonDocument.Parse(File.ReadAllText(fullPath));
                if (workspace.RootElement.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Array)
                {
                    foreach (var folder in folders.EnumerateArray())
                    {
                        if (folder.ValueKind != JsonValueKind.Object) continue;
                        var candidate = folder.TryGetProperty("uri", out var folderUri) && folderUri.ValueKind == JsonValueKind.String
                            ? folderUri.GetString()
                            : folder.TryGetProperty("path", out var folderPath) && folderPath.ValueKind == JsonValueKind.String
                                ? folderPath.GetString()
                                : null;
                        if (string.IsNullOrWhiteSpace(candidate)) continue;
                        var candidatePath = candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                            ? new Uri(candidate).LocalPath
                            : Path.Combine(Path.GetDirectoryName(fullPath)!, candidate);
                        var directory = PathNormalization.Directory(candidatePath);
                        if (!result.Contains(directory, StringComparer.OrdinalIgnoreCase)) result.Add(directory);
                    }
                    return result;
                }
            }
            if (File.Exists(fullPath)) fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
            result.Add(PathNormalization.Directory(fullPath));
        }
        catch (UriFormatException) { }
        catch (ArgumentException) { }
        catch (IOException) { }
        return result;
    }
}
