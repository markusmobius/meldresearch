using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Services.Research;

namespace MeldResearch.LocalBackend.Services.Updates;

/// <summary>
/// Automatic updates run only for an installed, release-stamped package that was started by the stable
/// launcher (which passes --InstallRoot). Development, Dummy and uncommitted test builds never self-update.
/// </summary>
internal sealed record UpdateOptions(bool Enabled, string? DisabledReason, string Version, DateTimeOffset? VersionDate,
    string? Runtime, string? InstallRoot, string? PublicKey, Uri RemoteOrigin, bool RegisterNativeHost,
    TimeSpan InitialDelay, TimeSpan Interval)
{
    public string CurrentFolder => DesktopUpdates.FolderName(Version);

    public static UpdateOptions Load(IConfiguration configuration, ResearchOptions research)
    {
        string version = configuration["ClientVersion"] ?? "development";
        DateTimeOffset? date = DateTimeOffset.TryParse(configuration["ClientVersionDate"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
        string? runtime = configuration["ClientRuntime"];
        string? root = configuration["InstallRoot"] is { Length: > 0 } configured ? Path.GetFullPath(configured) : null;
        string? key = configuration["UpdatePublicKey"];
        var initial = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Updates:InitialDelaySeconds", 45), 1, 3600));
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Updates:IntervalSeconds", 120), 5, 86400));
        bool register = configuration.GetValue("Updates:RegisterNativeHost", true);

        string? reason =
            !configuration.GetValue("UpdatesEnabled", false) ? "This build does not update itself (development or uncommitted test build)." :
            research.Dummy ? "Dummy mode never updates itself." :
            !DesktopUpdates.IsVersion(version) || date is null || !DesktopUpdates.IsRuntime(runtime) || string.IsNullOrWhiteSpace(key)
                ? "The package release metadata is incomplete." :
            !RuntimeMatches(runtime!) ? "The package was built for a different platform." :
            root is null || !File.Exists(Path.Combine(root, InstallationPointer.FileName))
                ? "Start Meld Research with its installed launcher to enable automatic updates." :
            !IsRunningFrom(root, DesktopUpdates.FolderName(version))
                ? "This copy is not the launcher's current installation." : null;
        return new(reason is null, reason, version, date, runtime, root, key, research.RemoteOrigin, register, initial, interval);
    }

    private static bool RuntimeMatches(string runtime) => runtime switch
    {
        "win-x64" => OperatingSystem.IsWindows(),
        "osx-arm64" or "osx-x64" => OperatingSystem.IsMacOS(),
        _ => false
    };

    private static bool IsRunningFrom(string root, string folder)
    {
        string expected = Path.TrimEndingDirectorySeparator(Path.Combine(root, folder, "local"));
        string actual = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return string.Equals(expected, actual, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
