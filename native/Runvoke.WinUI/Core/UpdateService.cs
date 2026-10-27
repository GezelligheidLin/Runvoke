using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Diagnostics;

namespace Runvoke.WinUI.Core;

public sealed record UpdateInfo(string Version, string Notes, Uri DownloadUri, bool Preview, string? Sha256 = null, string? Signature = null);
public sealed record UpdateDownloadProgress(long Received, long? Total);

public sealed class UpdateService
{
    private const string StableEndpoint = "https://runvoke-updates.oss-cn-shanghai.aliyuncs.com/runvoke/latest.json";
    private const string PreviewEndpoint = "https://runvoke-updates.oss-cn-shanghai.aliyuncs.com/runvoke/latest-prerelease.json";
    private const string AllowedDownloadHost = "runvoke-updates.oss-cn-shanghai.aliyuncs.com";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<UpdateInfo?> CheckAsync(bool preview, CancellationToken cancellationToken = default)
    {
        var stable = await CheckEndpointAsync(StableEndpoint, false, cancellationToken);
        if (!preview) return stable;
        var prerelease = await CheckEndpointAsync(PreviewEndpoint, true, cancellationToken);
        if (stable is null) return prerelease;
        if (prerelease is null) return stable;
        return CompareVersions(prerelease.Version, stable.Version) > 0 ? prerelease : stable;
    }

    private static async Task<UpdateInfo?> CheckEndpointAsync(string endpoint, bool preview, CancellationToken cancellationToken)
    {
        using var document = await Client.GetFromJsonAsync<JsonDocument>(endpoint, cancellationToken)
            ?? throw new InvalidOperationException("更新清单为空。");
        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("更新清单缺少版本号。");
        var notes = root.TryGetProperty("notes", out var notesValue) ? notesValue.GetString() ?? string.Empty : string.Empty;
        var platform = ReadPlatform(root);
        var url = platform.Url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var download) ||
            download.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(download.Host, AllowedDownloadHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新包地址不是受支持的 HTTPS 地址。");
        var sha256 = root.TryGetProperty("sha256", out var hashValue) ? hashValue.GetString() : platform.Sha256;
        var signature = root.TryGetProperty("signature", out var signatureValue)
            ? signatureValue.GetString()
            : platform.Signature;
        if (CompareVersions(version, CurrentVersion) <= 0) return null;
        return new UpdateInfo(version, notes, download, preview, sha256, signature);
    }

    private static int CompareVersions(string left, string right)
    {
        static (int[] Core, string[] Pre) Parse(string value)
        {
            var normalized = value.Trim().TrimStart('v', 'V');
            var split = normalized.Split('-', 2, StringSplitOptions.None);
            var core = split[0].Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, out var parsed) ? parsed : 0)
                .Concat(new[] { 0, 0, 0 })
                .Take(3)
                .ToArray();
            var pre = split.Length == 1
                ? Array.Empty<string>()
                : split[1].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (core, pre);
        }

        var a = Parse(left);
        var b = Parse(right);
        for (var index = 0; index < 3; index++)
        {
            var comparison = a.Core[index].CompareTo(b.Core[index]);
            if (comparison != 0) return comparison;
        }
        if (a.Pre.Length == 0 && b.Pre.Length > 0) return 1;
        if (a.Pre.Length > 0 && b.Pre.Length == 0) return -1;
        for (var index = 0; index < Math.Max(a.Pre.Length, b.Pre.Length); index++)
        {
            if (index >= a.Pre.Length) return -1;
            if (index >= b.Pre.Length) return 1;
            var leftPart = a.Pre[index];
            var rightPart = b.Pre[index];
            if (int.TryParse(leftPart, out var leftNumber) && int.TryParse(rightPart, out var rightNumber))
            {
                var comparison = leftNumber.CompareTo(rightNumber);
                if (comparison != 0) return comparison;
            }
            else
            {
                var comparison = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
                if (comparison != 0) return comparison;
            }
        }
        return 0;
    }

    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public Task<string> DownloadAndPrepareAsync(UpdateInfo update, CancellationToken cancellationToken = default) =>
        DownloadAndPrepareAsync(update, null, cancellationToken);

    public async Task<string> DownloadAndPrepareAsync(UpdateInfo update, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (update.DownloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(update.DownloadUri.Host, AllowedDownloadHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新包地址不是受支持的 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(update.Sha256))
            throw new InvalidOperationException("更新清单缺少签名校验摘要，已阻止安装。");
        if (string.IsNullOrWhiteSpace(update.Signature))
            throw new InvalidOperationException("更新清单缺少安装包签名，已阻止安装。");
        var signatureUri = new Uri(update.DownloadUri.AbsoluteUri + ".sig");
        var publishedSignature = (await Client.GetStringAsync(signatureUri, cancellationToken)).Trim();
        if (!string.Equals(publishedSignature, update.Signature.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("更新包签名与更新清单不一致，已阻止安装。");
        var extension = Path.GetExtension(update.DownloadUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8) extension = ".msixbundle";
        var directory = Path.Combine(Path.GetTempPath(), "Runvoke", "updates");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Runvoke-{update.Version}{extension}");
        await using (var source = await Client.GetStreamAsync(update.DownloadUri, cancellationToken))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[64 * 1024];
            long received = 0;
            long? total = source.CanSeek ? source.Length : (long?)null;
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                received += count;
                progress?.Report(new UpdateDownloadProgress(received, total));
            }
        }
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        var expectedHash = update.Sha256.Trim();
        if (expectedHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) expectedHash = expectedHash[7..].Trim();
        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新包校验失败，已阻止安装。");
        if (string.Equals(extension, ".msix", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".msixbundle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".appx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".appxbundle", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var archive = ZipFile.OpenRead(path);
                if (!archive.Entries.Any(entry => string.Equals(entry.FullName, "AppxSignature.p7x", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("更新包缺少 Windows AppX 签名，已阻止安装。");
            }
            catch (InvalidDataException error)
            {
                throw new InvalidOperationException("更新包格式无效或缺少 Windows AppX 签名，已阻止安装。", error);
            }
        }
        return path;
    }

    public void LaunchInstaller(string packagePath)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("更新包不存在。", packagePath);
        Process.Start(new ProcessStartInfo
        {
            FileName = packagePath,
            UseShellExecute = true,
            Verb = "open",
        });
    }

    private static (string? Url, string? Signature, string? Sha256) ReadPlatform(JsonElement root)
    {
        if (root.TryGetProperty("url", out var direct))
            return (direct.GetString(), root.TryGetProperty("signature", out var signature) ? signature.GetString() : null,
                root.TryGetProperty("sha256", out var hash) ? hash.GetString() : null);
        if (!root.TryGetProperty("platforms", out var platforms)) return (null, null, null);
        foreach (var key in new[] { "windows-x86_64", "windows-x64", "x86_64-pc-windows-msvc", "windows" })
            if (platforms.TryGetProperty(key, out var preferred) && preferred.TryGetProperty("url", out var preferredUrl))
                return (preferredUrl.GetString(), preferred.TryGetProperty("signature", out var signature) ? signature.GetString() : null,
                    preferred.TryGetProperty("sha256", out var hash) ? hash.GetString() : null);
        foreach (var property in platforms.EnumerateObject())
            if (property.Value.TryGetProperty("url", out var value))
                return (value.GetString(), property.Value.TryGetProperty("signature", out var signature) ? signature.GetString() : null,
                    property.Value.TryGetProperty("sha256", out var hash) ? hash.GetString() : null);
        return (null, null, null);
    }
}
