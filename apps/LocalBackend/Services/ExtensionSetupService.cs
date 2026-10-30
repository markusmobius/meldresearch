using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Contracts;

namespace MeldResearch.LocalBackend.Services;

internal interface IExtensionSetupService
{
    ExtensionSetupResponse GetSetup();
}

internal sealed class ExtensionSetupService(IWebHostEnvironment environment, IConfiguration configuration,
    ILogger<ExtensionSetupService> logger) : IExtensionSetupService
{
    private readonly Lock _gate = new();
    private string? _directory;

    public ExtensionSetupResponse GetSetup()
    {
        string? directory;
        lock (_gate) directory = _directory ??= ResolveDirectory();
        return new(ExtensionIdentity.Id, ExtensionIdentity.Version, WireProtocol.Version,
            directory is not null && File.Exists(Path.Combine(directory, "manifest.json")) ? directory : null,
            "https://chromewebstore.google.com/detail/" + ExtensionIdentity.Id,
            configuration.GetValue<bool>("ExtensionStorePublished"));
    }

    /// <summary>
    /// Installed copies live in a release folder that each update replaces, but Chrome keeps loading an unpacked extension
    /// from the folder it was given. The connector is therefore mirrored to one stable folder; an update then only needs
    /// the extension to reload itself.
    /// </summary>
    private string? ResolveDirectory()
    {
        string release = Path.GetFullPath(configuration["ConnectorDirectory"] ??
            Path.Combine(environment.ContentRootPath, "browser", "overleaf"));
        if (configuration["InstallRoot"] is not { Length: > 0 } root || !File.Exists(Path.Combine(release, "manifest.json")))
            return release;
        string stable = StableDirectory(root);
        try
        {
            ConnectorFolder.Mirror(release, stable);
            return stable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The browser connector could not be copied to its stable folder ({Type}).", exception.GetType().Name);
            return File.Exists(Path.Combine(stable, "manifest.json")) ? stable : release;
        }
    }

    internal static string StableDirectory(string installRoot) => Path.Combine(Path.GetFullPath(installRoot), "connector");
}

internal static class ConnectorFolder
{
    /// <summary>Makes <paramref name="target"/> an exact copy of <paramref name="source"/>, writing the manifest last.</summary>
    public static void Mirror(string source, string target)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        Directory.CreateDirectory(target);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path))
            .OrderBy(path => path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expected = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (string relative in files)
        {
            string from = Path.Combine(source, relative), to = Path.Combine(target, relative);
            byte[] content = File.ReadAllBytes(from);
            if (File.Exists(to) && File.ReadAllBytes(to).AsSpan().SequenceEqual(content)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllBytes(to + ".partial", content);
            File.Move(to + ".partial", to, overwrite: true);
        }
        foreach (string path in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToArray())
        {
            if (!expected.Contains(Path.GetRelativePath(target, path))) File.Delete(path);
        }
        foreach (string directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length).ToArray())
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
