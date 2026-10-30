using System.Diagnostics;
using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Middleware;

namespace MeldResearch.LocalBackend.Services;

internal interface IExtensionSetupService
{
    ExtensionSetupResponse GetSetup();
    /// <summary>Opens the browser's extensions page, which web pages cannot open themselves. False if the browser is not found.</summary>
    bool OpenExtensionsPage(string browser);
}

internal sealed class ExtensionSetupService(IWebHostEnvironment environment, IConfiguration configuration) : IExtensionSetupService
{
    public ExtensionSetupResponse GetSetup()
    {
        string directory = Path.GetFullPath(configuration["ConnectorDirectory"] ??
            Path.Combine(environment.ContentRootPath, "browser", "overleaf"));
        return new(ExtensionIdentity.Id, ExtensionIdentity.Version, WireProtocol.Version,
            File.Exists(Path.Combine(directory, "manifest.json")) ? directory : null,
            "https://chromewebstore.google.com/detail/" + ExtensionIdentity.Id,
            configuration.GetValue<bool>("ExtensionStorePublished"));
    }

    public bool OpenExtensionsPage(string browser)
    {
        string page = browser switch
        {
            "chrome" => "chrome://extensions",
            "edge" => "edge://extensions",
            _ => throw new ApiFailure(400, "invalid_browser", "Choose Chrome or Edge.")
        };
        // Starting the browser with the page opens it in the running browser's last-used profile.
        string? executable = BrowserExecutables(browser).FirstOrDefault(File.Exists);
        if (executable is null) return false;
        using var process = Process.Start(new ProcessStartInfo(executable, page) { UseShellExecute = false });
        return process is not null;
    }

    internal static IEnumerable<string> BrowserExecutables(string browser)
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return browser == "edge" ? "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
                : "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield break;
        }
        if (!OperatingSystem.IsWindows()) yield break;
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)];
        string relative = browser == "edge" ? @"Microsoft\Edge\Application\msedge.exe" : @"Google\Chrome\Application\chrome.exe";
        foreach (string root in roots.Where(root => root.Length != 0)) yield return Path.Combine(root, relative);
    }
}
