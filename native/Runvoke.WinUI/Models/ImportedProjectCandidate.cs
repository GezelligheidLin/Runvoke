namespace Runvoke.WinUI.Models;

public sealed record ImportedProjectCandidate(
    string Name,
    string Directory,
    string Source,
    string? SuggestedCommand = null);
