using System.Text.Json;
using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

public sealed class ProjectDetectionService
{
    public async Task<ProjectConfig> CreateProjectAsync(string directory, CancellationToken cancellationToken = default)
    {
        var normalized = PathNormalization.Directory(directory);
        if (!Directory.Exists(normalized)) throw new DirectoryNotFoundException($"项目目录不存在：{normalized}");
        var name = await DetectNameAsync(normalized, cancellationToken)
            ?? new DirectoryInfo(normalized).Name;
        var command = await DetectCommandAsync(normalized, cancellationToken);
        return new ProjectConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Directory = normalized,
            Command = command,
            Tasks =
            [
                new ProjectTask { Id = "default", Name = "开发服务器", Command = command, Mode = "service" },
            ],
        };
    }

    private static async Task<string> DetectCommandAsync(string directory, CancellationToken cancellationToken)
    {
        var packageJson = Path.Combine(directory, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                await using var stream = File.OpenRead(packageJson);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("scripts", out var scripts))
                {
                    foreach (var script in new[] { "dev", "start", "serve", "watch" })
                        if (scripts.TryGetProperty(script, out _)) return $"pnpm {script}";
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return "pnpm dev";
        }
        if (File.Exists(Path.Combine(directory, "Cargo.toml"))) return "cargo run";
        if (File.Exists(Path.Combine(directory, "main.py"))) return "python main.py";
        if (File.Exists(Path.Combine(directory, "go.mod"))) return "go run .";
        if (Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any()) return "dotnet run";
        if (File.Exists(Path.Combine(directory, "pubspec.yaml"))) return "flutter run";
        var pom = Path.Combine(directory, "pom.xml");
        if (File.Exists(pom))
        {
            try
            {
                var content = await File.ReadAllTextAsync(pom, cancellationToken);
                return content.Contains("spring-boot", StringComparison.OrdinalIgnoreCase)
                    ? "mvn spring-boot:run"
                    : "mvn package";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return "pnpm dev";
    }

    private static async Task<string?> DetectNameAsync(string directory, CancellationToken cancellationToken)
    {
        var packageJson = Path.Combine(directory, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                await using var stream = File.OpenRead(packageJson);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                    return name.GetString()!.Trim().Split('/').Last();
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var composerJson = Path.Combine(directory, "composer.json");
        if (File.Exists(composerJson))
        {
            try
            {
                await using var stream = File.OpenRead(composerJson);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                    return name.GetString()!.Trim().Split('/').Last();
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var (file, marker) in new[]
        {
            ("Cargo.toml", "name"),
            ("pyproject.toml", "name"),
            ("pubspec.yaml", "name:"),
            ("go.mod", "module "),
        })
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith(marker, StringComparison.Ordinal)) continue;
                    var value = trimmed[marker.Length..].Trim();
                    if (marker == "name" && value.StartsWith('=')) value = value[1..].Trim();
                    value = value.Split('#', 2)[0].Trim().Trim('"', '\'');
                    if (value.Length > 0) return value.Split('/').Last();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var csproj = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (csproj is not null)
        {
            try
            {
                var content = await File.ReadAllTextAsync(csproj, cancellationToken);
                var assemblyStart = content.IndexOf("<AssemblyName>", StringComparison.OrdinalIgnoreCase);
                if (assemblyStart >= 0)
                {
                    assemblyStart += "<AssemblyName>".Length;
                    var assemblyEnd = content.IndexOf("</AssemblyName>", assemblyStart, StringComparison.OrdinalIgnoreCase);
                    if (assemblyEnd > assemblyStart)
                    {
                        var value = content[assemblyStart..assemblyEnd].Trim();
                        if (value.Length > 0) return value;
                    }
                }
                return Path.GetFileNameWithoutExtension(csproj);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var pom = Path.Combine(directory, "pom.xml");
        if (File.Exists(pom))
        {
            try
            {
                var content = await File.ReadAllTextAsync(pom, cancellationToken);
                var start = content.IndexOf("<artifactId>", StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    start += "<artifactId>".Length;
                    var end = content.IndexOf("</artifactId>", start, StringComparison.OrdinalIgnoreCase);
                    if (end > start)
                    {
                        var value = content[start..end].Trim();
                        if (value.Length > 0) return value;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}
