using System.Diagnostics;
using System.Text.RegularExpressions;
using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

public sealed class LogLinkService
{
    private static readonly Regex LinkPattern = new("https?://[^\\s<>()\\[\\]{}\\\"']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<Uri> FindLinks(IEnumerable<string> messages)
    {
        var links = new List<Uri>();
        foreach (var message in messages)
            foreach (Match match in LinkPattern.Matches(message))
                if (Uri.TryCreate(match.Value.TrimEnd('.', ',', ';'), UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) links.Add(uri);
        return links;
    }

    public IReadOnlyList<LogLineSegment> Split(string message)
    {
        var segments = new List<LogLineSegment>();
        var cursor = 0;
        foreach (Match match in LinkPattern.Matches(message))
        {
            if (match.Index > cursor)
                segments.Add(new LogLineSegment { Text = message[cursor..match.Index] });

            var candidate = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                segments.Add(new LogLineSegment { Text = candidate, Url = uri.AbsoluteUri });
                if (candidate.Length < match.Length)
                    segments.Add(new LogLineSegment { Text = match.Value[candidate.Length..] });
                cursor = match.Index + match.Length;
            }
            else
            {
                segments.Add(new LogLineSegment { Text = match.Value });
                cursor = match.Index + match.Length;
            }
        }
        if (cursor < message.Length) segments.Add(new LogLineSegment { Text = message[cursor..] });
        if (segments.Count == 0) segments.Add(new LogLineSegment { Text = message });
        return segments;
    }

    public void Open(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("只允许打开 HTTP 或 HTTPS 链接。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
