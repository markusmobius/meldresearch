using MeldResearch.LocalBackend.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeldResearch.Tests;

public sealed class ConnectorFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meld-connector-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Release(string name, string version, params (string Path, string Content)[] files)
    {
        string folder = Path.Combine(_root, name, "browser", "overleaf");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), $$"""{"version":"{{version}}"}""");
        foreach (var (path, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, path))!);
            File.WriteAllText(Path.Combine(folder, path), content);
        }
        return folder;
    }

    private ExtensionSetupService Service(string release) => new(null!, new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectorDirectory"] = release, ["InstallRoot"] = _root })
        .Build(), NullLogger<ExtensionSetupService>.Instance);

    [Fact]
    public void InstalledConnectorsAreOfferedFromOneStableFolderThatFollowsEachRelease()
    {
        string first = Release("aaaaaaaaaaaa", "0.3.0", ("popup.js", "old"), ("icons/old.png", "x"));
        string stable = Path.Combine(_root, "connector");
        Assert.Equal(stable, Service(first).GetSetup().DevelopmentDirectory);
        Assert.Equal("old", File.ReadAllText(Path.Combine(stable, "popup.js")));

        string second = Release("bbbbbbbbbbbb", "0.4.0", ("popup.js", "new"), ("icons/new.png", "y"));
        Assert.Equal(stable, Service(second).GetSetup().DevelopmentDirectory);
        Assert.Equal("new", File.ReadAllText(Path.Combine(stable, "popup.js")));
        Assert.Contains("0.4.0", File.ReadAllText(Path.Combine(stable, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(stable, "icons", "new.png")));
        Assert.False(File.Exists(Path.Combine(stable, "icons", "old.png")), "Files removed from a release are removed.");
        Assert.Empty(Directory.EnumerateFiles(stable, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public void DevelopmentRunsUseTheConnectorSourceFolderDirectly()
    {
        string release = Release("dev", "0.4.0");
        var service = new ExtensionSetupService(null!, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectorDirectory"] = release }).Build(),
            NullLogger<ExtensionSetupService>.Instance);
        Assert.Equal(release, service.GetSetup().DevelopmentDirectory);
        Assert.False(Directory.Exists(Path.Combine(_root, "connector")));
    }
}
