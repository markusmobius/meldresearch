using Microsoft.Extensions.Logging;
using MeldResearch.LocalBackend.Services;

namespace MeldResearch.Tests;

public sealed class AppLogTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Fact]
    public void RepeatsAreCountedOnOneLineAndTheLogSurvivesARestart()
    {
        var log = new AppLogStore(_temporary.Path);
        log.Write("info", "research", "Queued 3 changed file(s) for summary processing.", projectId: "0123456789abcdef01234567");
        log.Write("warn", "server", "Meld's server is not answering (remote_unavailable); retrying.");
        log.Write("warn", "server", "Meld's server is not answering (remote_unavailable); retrying.");
        var entries = log.Read(null, null, 100).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal(("server", 2), (entries[0].Source, entries[0].Repeats));
        Assert.Equal(1, log.Status().RecentIssues);
        Assert.Equal(["research", "server"], log.Read(null, null, 100).Sources);
        Assert.Single(log.Read("warn", null, 100).Entries);

        var restarted = new AppLogStore(_temporary.Path);
        var reloaded = restarted.Read(null, "research", 100).Entries;
        Assert.Equal("0123456789abcdef01234567", Assert.Single(reloaded).ProjectId);
        Assert.Equal(2, restarted.Read(null, "server", 100).Entries.Single().Repeats);
        restarted.Write("info", "update", "Restarting into the updated desktop version.");
        Assert.True(restarted.Read(null, "update", 1).Entries.Single().Id > reloaded[0].Id);
    }

    [Fact]
    public void OnlyMeldMessagesAreLoggedAndProjectScopesTagThem()
    {
        var log = new AppLogStore(null);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(new AppLogProvider(log)).SetMinimumLevel(LogLevel.Trace));
        var research = factory.CreateLogger("MeldResearch.LocalBackend.Services.Research.ResearchCoordinator");
        using (research.BeginScope(new Dictionary<string, object?> { ["ProjectId"] = "0123456789abcdef01234567" }))
            research.LogWarning("Processing failed ({Code}): {Detail}", "foundry_busy", "Foundry is busy.");
        research.LogDebug("Not shown.");
        factory.CreateLogger("Microsoft.AspNetCore.Hosting").LogWarning("Framework message.");
        var entry = Assert.Single(log.Read(null, null, 10).Entries);
        Assert.Equal(("warn", "research", "0123456789abcdef01234567"), (entry.Level, entry.Source, entry.ProjectId));
        Assert.Equal("Processing failed (foundry_busy): Foundry is busy.", entry.Message);
        Assert.Equal("update", AppLogProvider.Source("MeldResearch.LocalBackend.Services.Updates.DesktopUpdater"));
    }

    public void Dispose() => _temporary.Dispose();
}
