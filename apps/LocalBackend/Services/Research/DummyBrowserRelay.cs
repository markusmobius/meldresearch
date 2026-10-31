using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Contracts;

namespace MeldResearch.LocalBackend.Services.Research;

internal sealed class DummyBrowserRelay : IBrowserRelay
{
    public const string ProjectId = "0123456789abcdef01234567";
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly string _statePath = Path.Combine(RuntimeFiles.Root, "dummy-source.json");
    private int _revision;
    private sealed record DummyState(int Revision);

    public DummyBrowserRelay()
    {
        if (!File.Exists(_statePath)) return;
        if (new FileInfo(_statePath).Length > 4096) throw new InvalidDataException("Synthetic source state is invalid.");
        var state = JsonSerializer.Deserialize<DummyState>(File.ReadAllText(_statePath), WireProtocol.Json);
        _revision = state is { Revision: >= 0 } ? state.Revision : throw new InvalidDataException("Synthetic source state is invalid.");
    }

    public BridgeStatusResponse Status => new(true, false, WireProtocol.Version, "dummy-" + Volatile.Read(ref _revision), true);
    public async Task<int> AdvanceAsync(CancellationToken ct)
    {
        await _changes.WaitAsync(ct);
        try
        {
            int revision = checked(_revision + 1);
            await RuntimeFiles.WriteJsonAsync(_statePath, new DummyState(revision), ct);
            Interlocked.Exchange(ref _revision, revision);
            return revision;
        }
        finally { _changes.Release(); }
    }
    public Task ConnectAsync(WebSocket socket, string localUrl, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Dummy evidence does not accept a real browser connection.");

    public async Task<BrowserResponse> FetchAsync(string operation, string? projectId, CancellationToken ct)
    {
        string directory = Path.Combine(RuntimeFiles.Root, "incoming");
        RuntimeFiles.CreatePrivateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        int revision = Volatile.Read(ref _revision);
        try
        {
            await using var stream = RuntimeFiles.CreatePrivateFile(path);
            if (operation == "listProjects")
            {
                // Recent (noon UTC on the first of this month), so Dummy exercises the required include/exclude choice. The date is stable
                // within a month; a month change only re-reads unchanged content, which makes no model calls.
                var now = DateTimeOffset.UtcNow;
                await JsonSerializer.SerializeAsync(stream, new { projects = new[] { new
                {
                    id = ProjectId, name = "Synthetic research project", archived = false, trashed = false,
                    lastUpdated = new DateTimeOffset(now.Year, now.Month, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(revision)
                } } }, WireProtocol.Json, ct);
                return new(path, "application/json");
            }
            if (operation == "projectHistory" && projectId == ProjectId)
            {
                await JsonSerializer.SerializeAsync(stream, new
                {
                    paths = new Dictionary<string, long> { ["main.tex"] = DateTimeOffset.UtcNow.AddMinutes(-revision).ToUnixTimeMilliseconds() },
                    complete = true
                }, WireProtocol.Json, ct);
                return new(path, "application/json");
            }
            if (operation != "downloadProject" || projectId != ProjectId)
                throw new InvalidOperationException("Unknown synthetic Overleaf operation.");
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("main.tex").Open(), new UTF8Encoding(false));
                await writer.WriteAsync(("""
                    \documentclass{article}
                    \title{Synthetic study of research collaboration}
                    \begin{document}
                    This is synthetic test evidence, not a real research result.
                    We study whether structured weekly feedback improves collaboration.
                    """ + (revision == 0 ? """

                    The planned pilot will recruit 40 participants. Data collection has not started.
                    """ : """

                    The pilot now includes 80 participants. Data collection is complete.
                    The analysis reports a 15 percent improvement in task completion in the feedback group.
                    The previous plan of 40 participants is superseded by the completed 80-participant pilot.
                    """) + "\n\\end{document}\n").AsMemory(), ct);
            }
            return new(path, "application/zip");
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }
}
