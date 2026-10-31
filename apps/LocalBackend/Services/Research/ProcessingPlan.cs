namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>What a project still needs to fold into its summary, and in which order.</summary>
internal static class ProcessingPlan
{
    /// <summary>Analyzed files that are new or changed since the summary last reflected them, plus analyzed files that disappeared.</summary>
    public static IReadOnlyList<string> Work(IReadOnlyDictionary<string, string>? processed, ProjectManifest manifest)
    {
        processed ??= new Dictionary<string, string>();
        var current = manifest.Files.Where(file => file.Analyzed).ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);
        return current.Where(file => !processed.TryGetValue(file.Key, out var hash) || hash != file.Value).Select(file => file.Key)
            .Concat(processed.Keys.Where(path => !current.ContainsKey(path))).ToArray();
    }

    /// <summary>
    /// Earliest edited first, so the summary grows in the order the research did. Files older than Overleaf's available history
    /// (or every file, when no history is available) come first, main document before deeper files; deletions come last.
    /// </summary>
    public static IReadOnlyList<string> Order(IReadOnlyList<string> paths, ProjectManifest manifest)
    {
        var present = manifest.Files.Where(file => file.Analyzed).Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var times = manifest.EditTimes ?? new Dictionary<string, DateTimeOffset>();
        return paths.OrderBy(path => present.Contains(path) ? 0 : 1)
            .ThenBy(path => times.TryGetValue(path, out var time) ? time : DateTimeOffset.MinValue)
            .ThenBy(path => path.Count(character => character == '/'))
            .ThenBy(path => Path.GetFileNameWithoutExtension(path).Equals("main", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Whether the summary already reflects this path as it is in the pending manifest.</summary>
    public static bool Done(MonitoredProject project, string path)
    {
        string? pending = project.Pending?.Files.FirstOrDefault(file => file.Path == path && file.Analyzed)?.Sha256;
        string? processed = project.Processed is { } map && map.TryGetValue(path, out var hash) ? hash : null;
        return pending == processed;
    }

    public static string? Next(MonitoredProject project) =>
        project.PendingOrder?.FirstOrDefault(path => !Done(project, path));

    public static (int Done, int Total) Progress(MonitoredProject project) =>
        project.PendingOrder is { } order ? (order.Count(path => Done(project, path)), order.Count) : (0, 0);
}
