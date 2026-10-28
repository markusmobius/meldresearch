using System.Text.Json.Serialization;
using Microsoft.Win32;
using MeldResearch.BrowserProtocol;

namespace MeldResearch.BrowserHost;

internal static class HostRegistration
{
    private static readonly string[] WindowsKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + WireProtocol.HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + WireProtocol.HostName
    ];

    private static string ManifestPath => Path.Combine(RuntimeFiles.Root, "native-host.json");

    public static async Task<int> RegisterAsync(string extensionId)
    {
        if (extensionId.Length != 32 || extensionId.Any(character => character is < 'a' or > 'p'))
            throw new ArgumentException("The extension ID must contain 32 letters from a to p.");

        string executable = Path.Combine(AppContext.BaseDirectory,
            "MeldResearch.BrowserHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (!File.Exists(executable))
            throw new FileNotFoundException("Build the native messaging host before registering it.");

        var manifest = new NativeHostManifest(WireProtocol.HostName, "Meld Research read-only Overleaf bridge",
            executable, "stdio", [$"chrome-extension://{extensionId}/"]);
        RuntimeFiles.CreatePrivateDirectory(RuntimeFiles.Root);

        if (OperatingSystem.IsWindows())
        {
            foreach (string keyPath in WindowsKeys)
            {
                using var existing = Registry.CurrentUser.OpenSubKey(keyPath);
                if (existing?.GetValue("") is string value && !string.Equals(value, ManifestPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different native host registration already exists; it was not changed.");
            }
            await RuntimeFiles.WriteJsonAsync(ManifestPath, manifest, CancellationToken.None);
            foreach (string keyPath in WindowsKeys)
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                key.SetValue("", ManifestPath);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (string path in MacManifestPaths())
            {
                RuntimeFiles.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
                await RuntimeFiles.WriteJsonAsync(path, manifest, CancellationToken.None);
            }
        }
        else
        {
            throw new PlatformNotSupportedException("This prototype supports Chrome/Edge on Windows and macOS.");
        }

        Console.WriteLine($"Registered the per-user Chrome/Edge bridge for extension {extensionId}.");
        return 0;
    }

    public static int Unregister()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string keyPath in WindowsKeys)
            {
                using (var existing = Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    if (existing is null)
                        continue;
                    if (!string.Equals(existing.GetValue("") as string, ManifestPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A different registration was not removed.");
                }
                Registry.CurrentUser.DeleteSubKey(keyPath);
            }
            File.Delete(ManifestPath);
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (string path in MacManifestPaths())
                File.Delete(path);
        }
        else
        {
            throw new PlatformNotSupportedException("This prototype supports Windows and macOS.");
        }
        Console.WriteLine("Removed the Meld Overleaf native host registrations. Downloaded projects were retained.");
        return 0;
    }

    private static IEnumerable<string> MacManifestPaths()
    {
        string support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        yield return Path.Combine(support, "Google", "Chrome", "NativeMessagingHosts", WireProtocol.HostName + ".json");
        yield return Path.Combine(support, "Microsoft Edge", "NativeMessagingHosts", WireProtocol.HostName + ".json");
    }

    private sealed record NativeHostManifest(string Name, string Description, string Path, string Type,
        [property: JsonPropertyName("allowed_origins")] string[] AllowedOrigins);
}
