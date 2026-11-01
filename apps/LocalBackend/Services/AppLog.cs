using System.Text.Json;
using MeldResearch.BrowserProtocol;

namespace MeldResearch.LocalBackend.Services;

/// <summary>
/// One line of the local activity log. ProjectId names the Overleaf project the work belonged to (the page shows its name
/// from the app's own status); messages never contain research text, names or credentials.
/// </summary>
internal sealed record AppLogEntry(long Id, DateTimeOffset Timestamp, string Level, string Source, string Message, string? Detail,
    string? ProjectId, int Repeats = 1);

internal sealed record AppLogResponse(IReadOnlyList<AppLogEntry> Entries, IReadOnlyList<string> Sources);
internal sealed record AppLogStatusResponse(long LastId, int RecentIssues);

/// <summary>
/// The activity log shown under More → Log (as in Project Meld). Keeps the newest entries in memory and in a small capped
/// file, so the history survives restarts and updates. The same message repeated within five minutes is counted on one
/// line instead of filling the log. Writing never throws: logging must not break the work it describes.
/// </summary>
internal sealed class AppLogStore
{
    public const int Capacity = 2000;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly List<AppLogEntry> _entries = [];
    private readonly string? _path;
    private readonly TimeProvider _clock;
    private long _nextId = 1;

    public AppLogStore(string? directory, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        if (directory is null) return;
        try
        {
            RuntimeFiles.CreatePrivateDirectory(directory);
            _path = Path.Combine(directory, "app-log.jsonl");
            if (!File.Exists(_path)) return;
            var byId = new Dictionary<long, AppLogEntry>();
            foreach (string line in File.ReadLines(_path))
            {
                try { if (JsonSerializer.Deserialize<AppLogEntry>(line, Json) is { } entry) byId[entry.Id] = entry; }
                catch (JsonException) { /* A line cut off by a crash is skipped. */ }
            }
            _entries.AddRange(byId.Values.OrderBy(entry => entry.Id).TakeLast(Capacity));
            _nextId = _entries.Count == 0 ? 1 : _entries[^1].Id + 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _path = null; }
    }

    public void Write(string level, string source, string message, string? detail = null, string? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (message.Length > 2000) message = message[..2000];
        if (detail is { Length: > 2000 }) detail = detail[..2000];
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            AppLogEntry entry;
            if (_entries.Count != 0 && _entries[^1] is var last && last.Level == level && last.Source == source && last.Message == message &&
                last.ProjectId == projectId && now - last.Timestamp < RepeatWindow)
            {
                entry = last with { Timestamp = now, Detail = detail ?? last.Detail, Repeats = last.Repeats + 1 };
                _entries[^1] = entry;
            }
            else
            {
                entry = new(_nextId++, now, level, source, message, detail, projectId);
                _entries.Add(entry);
                if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
            }
            Persist(entry);
        }
    }

    /// <summary>Newest first, optionally filtered by level (info, warn, error) and source.</summary>
    public AppLogResponse Read(string? level, string? source, int limit)
    {
        lock (_gate)
        {
            var sources = _entries.Select(entry => entry.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var entries = _entries.AsEnumerable().Reverse()
                .Where(entry => string.IsNullOrEmpty(level) || level == "all" || entry.Level == level)
                .Where(entry => string.IsNullOrEmpty(source) || source == "all" || entry.Source == source)
                .Take(Math.Clamp(limit, 1, 1000)).ToArray();
            return new(entries, sources);
        }
    }

    /// <summary>For the More menu badge: warnings and errors in the last ten minutes.</summary>
    public AppLogStatusResponse Status()
    {
        lock (_gate)
        {
            var since = _clock.GetUtcNow().AddMinutes(-10);
            return new(_entries.Count == 0 ? 0 : _entries[^1].Id,
                _entries.Count(entry => entry.Timestamp >= since && entry.Level is "warn" or "error"));
        }
    }

    private void Persist(AppLogEntry entry)
    {
        if (_path is null) return;
        try
        {
            if (new FileInfo(_path) is { Exists: true, Length: > MaxFileBytes })
            {
                // Rewrite with what is kept in memory, so the file stays small.
                string temporary = _path + ".tmp";
                File.WriteAllLines(temporary, _entries.Select(item => JsonSerializer.Serialize(item, Json)));
                File.Move(temporary, _path, overwrite: true);
                return;
            }
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* The in-memory log still works. */ }
    }
}

/// <summary>
/// Feeds Meld's own log messages (categories under MeldResearch, information and above) into <see cref="AppLogStore"/>.
/// Framework categories are left out. A "ProjectId" scope or message value tags the entry with its project.
/// </summary>
internal sealed class AppLogProvider(AppLogStore store) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopes;

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => categoryName.StartsWith("MeldResearch.", StringComparison.Ordinal)
        ? new AppLogger(store, Source(categoryName), () => _scopes) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() { }

    internal static string Source(string category)
    {
        string name = category[(category.LastIndexOf('.') + 1)..];
        return name switch
        {
            "ResearchCoordinator" => "research",
            "ResearchSync" => "sync",
            "ResearchArchive" or "LeaveService" => "account",
            "GmailConnector" or "GmailGoogle" => "gmail",
            "ResearcherService" => "openalex",
            "DesktopUpdater" => "update",
            "BrowserRelay" or "OverleafService" or "OverleafEvidenceSource" or "ExtensionSetupService" or "DummyBrowserRelay" => "overleaf",
            "ResearchRemote" or "RemoteConnectivity" => "server",
            _ => "app"
        };
    }

    private sealed class AppLogger(AppLogStore store, string source, Func<IExternalScopeProvider?> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes()?.Push(state);
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string? project = ProjectId(state);
            scopes()?.ForEachScope((scope, _) => project ??= ProjectId(scope), (object?)null);
            store.Write(logLevel >= LogLevel.Error ? "error" : logLevel == LogLevel.Warning ? "warn" : "info", source,
                formatter(state, exception), exception?.GetType().Name, project);
        }

        private static string? ProjectId(object? state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.FirstOrDefault(pair => pair.Key == "ProjectId").Value as string : null;
    }
}
