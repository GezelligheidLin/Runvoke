using System.Collections.Concurrent;
using Runvoke.WinUI.Models;

namespace Runvoke.WinUI.Core;

internal sealed class LogHistoryStore
{
    private const int Limit = 500;
    private readonly ConcurrentDictionary<string, List<LogEvent>> _entries = [];

    public void Append(LogEvent entry)
    {
        var history = _entries.GetOrAdd(entry.RunId, _ => []);
        lock (history)
        {
            if (entry.Mode is "progress" or "finish")
            {
                for (var index = history.Count - 1; index >= 0; index--)
                {
                    if (history[index].Stream != entry.Stream) continue;
                    if (history[index].Mode is "progress" or "finish")
                    {
                        history[index] = entry;
                        return;
                    }
                    break;
                }
            }
            history.Add(entry);
            if (history.Count > Limit) history.RemoveRange(0, history.Count - Limit);
        }
    }

    public IReadOnlyList<LogEvent> Read(string runId, int limit)
    {
        if (!_entries.TryGetValue(runId, out var history)) return [];
        lock (history)
        {
            var count = Math.Clamp(limit, 1, Limit);
            return history.Skip(Math.Max(0, history.Count - count)).ToArray();
        }
    }

    public void Remove(string runId) => _entries.TryRemove(runId, out _);
}
