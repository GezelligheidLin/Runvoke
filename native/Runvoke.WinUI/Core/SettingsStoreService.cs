using System.Text.Json;
using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

public sealed class SettingsStoreService
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Runvoke",
        "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        if (!File.Exists(SettingsPath))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken);
            return defaults;
        }

        await using var stream = File.OpenRead(SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken)
            ?? new AppSettings();
        Normalize(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporaryPath = SettingsPath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
            File.Move(temporaryPath, SettingsPath, true);
        }
        finally { _saveGate.Release(); }
    }

    private static void Normalize(AppSettings settings)
    {
        var theme = settings.Theme ?? string.Empty;
        var logLinkAction = settings.LogLinkAction ?? string.Empty;
        var notificationPosition = settings.NotificationPosition ?? string.Empty;
        settings.LastOpenedVersion ??= string.Empty;
        settings.McpToken ??= string.Empty;
        settings.Theme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
        settings.LogLinkAction = string.Equals(logLinkAction, "copy", StringComparison.OrdinalIgnoreCase) ? "copy" : "open";
        var normalizedPosition = notificationPosition.ToLowerInvariant();
        settings.NotificationPosition = normalizedPosition switch
        {
            "top-left" or "top-center" or "top-right" or "bottom-left" or "bottom-center" or "bottom-right"
                => normalizedPosition,
            _ => "bottom-right",
        };
        if (settings.McpPort < 1024) settings.McpPort = 38465;
    }
}
