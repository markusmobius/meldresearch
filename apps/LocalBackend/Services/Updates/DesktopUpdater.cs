using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Services.Research;

namespace MeldResearch.LocalBackend.Services.Updates;

/// <summary>The launcher's pointer to the current and previous installed versions, in &lt;InstallRoot&gt;/current.json.</summary>
internal sealed record InstallationPointer(string Current, string? Previous, IReadOnlyList<string> Rejected)
{
    public const string FileName = "current.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static InstallationPointer Read(string root)
    {
        var pointer = JsonSerializer.Deserialize<InstallationPointer>(File.ReadAllBytes(Path.Combine(root, FileName)), Json);
        return pointer is { Current.Length: 12 }
            ? pointer with { Previous = string.IsNullOrEmpty(pointer.Previous) ? null : pointer.Previous, Rejected = pointer.Rejected ?? [] }
            : throw new InvalidDataException("The installation pointer is invalid.");
    }

    // Launch scripts read this with plutil on macOS, which rejects JSON null, so "no previous version" is "".
    public void Write(string root)
    {
        string path = Path.Combine(root, FileName), temporary = path + ".partial";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this with { Previous = Previous ?? "" }, Json));
        File.Move(temporary, path, overwrite: true);
    }
}

internal interface INativeHostRegistrar
{
    Task RegisterAsync(string browserHostExecutable, CancellationToken ct);
}

internal sealed class NativeHostRegistrar : INativeHostRegistrar
{
    public async Task RegisterAsync(string browserHostExecutable, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(browserHostExecutable, "--register")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("The native host registration could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw new InvalidOperationException("The native host registration failed.");
    }
}

internal sealed record DesktopVersionResponse(string Version, DateTimeOffset? VersionDate, string? Runtime,
    bool UpdatesEnabled, string? UpdatesDisabledReason, string UpdateStatus, string? PendingVersion, string? LastUpdateError);

/// <summary>
/// Checks the remote server for a signed desktop release, verifies and installs it side by side in a new
/// version folder, repoints the stable launcher, and restarts through it once no research or browser work
/// is running. The running version's files are never modified.
/// </summary>
internal sealed class DesktopUpdater(UpdateOptions options, IHttpClientFactory clients, IResearchCoordinator research,
    IBrowserRelay browser, IHostApplicationLifetime lifetime, INativeHostRegistrar registrar, TimeProvider clock,
    ILogger<DesktopUpdater> logger) : BackgroundService
{
    public const string HttpClientName = "desktop-updates";
    private const int MaxEntries = 20000;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private volatile string _status = options.Enabled ? "waiting" : "disabled";
    private volatile string? _pending;
    // The most recent failure, kept until a later check succeeds; it contains no credentials or research content.
    private volatile string? _lastError;

    public DesktopVersionResponse Status => new(options.Version, options.VersionDate, options.Runtime,
        options.Enabled, options.DisabledReason, _status, _pending, _lastError);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Automatic updates are off: {Reason}", options.DisabledReason);
            return;
        }
        try { Prune(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { logger.LogWarning("Old desktop versions could not be removed ({Type}); they will be retried later.", error.GetType().Name); }
        try
        {
            await Task.Delay(options.InitialDelay, clock, stoppingToken);
            while (true)
            {
                try
                {
                    if (await CheckAsync(stoppingToken))
                    {
                        _lastError = null;
                        await RestartWhenIdleAsync(stoppingToken);
                        return;
                    }
                    _lastError = null;
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    _status = "failed";
                    _lastError = $"{DateTimeOffset.UtcNow:u}: the update check timed out.";
                    logger.LogWarning("Desktop update check timed out. It will be retried.");
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
                    UnauthorizedAccessException or JsonException or InvalidOperationException or CryptographicException)
                {
                    _status = "failed";
                    _lastError = $"{DateTimeOffset.UtcNow:u}: {error.GetType().Name}: {error.Message}";
                    logger.LogWarning("Desktop update check failed ({Type}: {Message}). It will be retried.", error.GetType().Name, error.Message);
                }
                await Task.Delay(options.Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Returns true when a verified newer release is installed and the launcher points to it.</summary>
    internal async Task<bool> CheckAsync(CancellationToken ct)
    {
        _status = "checking";
        string root = options.InstallRoot!;
        var http = clients.CreateClient(HttpClientName);
        DesktopUpdateManifest manifest;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.GetAsync(new Uri(options.RemoteOrigin, $"api/updates/{options.Runtime}"),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { _status = "current"; return false; }
            response.EnsureSuccessStatusCode();
            byte[] envelope = await ReadBoundedAsync(response, DesktopUpdates.MaxEnvelopeBytes, timeout.Token);
            manifest = DesktopUpdates.Verify(DesktopUpdates.ReadEnvelope(envelope), options.PublicKey!);
        }
        if (manifest.Runtime != options.Runtime) throw new InvalidDataException("The signed release is for another platform.");
        var pointer = InstallationPointer.Read(root);
        if (manifest.Version == options.Version || manifest.VersionDate <= options.VersionDate ||
            pointer.Rejected.Contains(manifest.Version))
        {
            // Equal, older (a downgrade or replay) or previously failed releases are never installed.
            _status = "current";
            return false;
        }

        _pending = manifest.Version;
        string folder = DesktopUpdates.FolderName(manifest.Version);
        string target = Path.Combine(root, folder);
        if (!IsComplete(target, manifest))
        {
            _status = "downloading";
            string staging = Path.Combine(root, ".staging");
            Directory.CreateDirectory(staging);
            string archive = Path.Combine(staging, folder + ".zip"), expanded = Path.Combine(staging, folder);
            try
            {
                await DownloadAsync(http, manifest, archive, ct);
                if (Directory.Exists(expanded)) Directory.Delete(expanded, recursive: true);
                Extract(archive, expanded);
                if (!IsComplete(expanded, manifest)) throw new InvalidDataException("The update package is incomplete or names another release.");
                MakeExecutable(expanded);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(expanded, target);
            }
            finally
            {
                File.Delete(archive);
                if (Directory.Exists(expanded)) Directory.Delete(expanded, recursive: true);
            }
        }

        _status = "installing";
        if (options.RegisterNativeHost)
        {
            try { await registrar.RegisterAsync(BrowserHost(target), ct); }
            catch
            {
                try { await registrar.RegisterAsync(BrowserHost(Path.Combine(root, options.CurrentFolder)), CancellationToken.None); }
                catch (Exception restore) when (restore is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                { logger.LogError("The previous native host registration could not be restored ({Type}).", restore.GetType().Name); }
                throw;
            }
        }
        string launcher = Launcher(target), stable = Path.Combine(root, Path.GetFileName(launcher));
        File.Copy(launcher, stable + ".partial", overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stable + ".partial", (UnixFileMode)0b111_000_000);
        File.Move(stable + ".partial", stable, overwrite: true);
        new InstallationPointer(folder, options.CurrentFolder, pointer.Rejected).Write(root);
        _status = "ready";
        logger.LogInformation("Desktop update {Version} is verified and installed. Meld Research restarts when idle.", manifest.Version[..12]);
        return true;
    }

    private async Task RestartWhenIdleAsync(CancellationToken ct)
    {
        while (research.Busy || browser.Status.Busy)
            await Task.Delay(TimeSpan.FromSeconds(5), clock, ct);
        logger.LogInformation("Restarting into the updated desktop version.");
        RestartRequested = true;
        lifetime.StopApplication();
    }

    /// <summary>Set once a verified update is installed; Program exits with <see cref="DesktopUpdates.UpdateExitCode"/>.</summary>
    public bool RestartRequested { get; private set; }

    private async Task DownloadAsync(HttpClient http, DesktopUpdateManifest manifest, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await http.GetAsync(new Uri(options.RemoteOrigin, $"api/updates/{manifest.Runtime}/package/{manifest.Version}"),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } announced && announced != manifest.Length)
            throw new InvalidDataException("The update package size does not match its signed manifest.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += read;
                if (total > manifest.Length) throw new InvalidDataException("The update package is larger than its signed manifest.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            if (total != manifest.Length) throw new InvalidDataException("The update package is shorter than its signed manifest.");
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(manifest.Sha256)))
            throw new InvalidDataException("The update package does not match its signed SHA-256.");
    }

    internal static void Extract(string archive, string destination)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("The update package has too many entries.");
        long expanded = 0;
        Directory.CreateDirectory(root);
        foreach (var entry in zip.Entries)
        {
            expanded += entry.Length;
            string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            const int unixTypeMask = 0xF000 << 16, unixSymlink = 0xA000 << 16;
            if (expanded > MaxExpandedBytes || !path.StartsWith(root, StringComparison.Ordinal) ||
                Path.IsPathRooted(entry.FullName) || (entry.ExternalAttributes & unixTypeMask) == unixSymlink)
                throw new InvalidDataException("The update package contains an unsafe entry.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    private bool IsComplete(string folder, DesktopUpdateManifest manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "installation.json")));
            return document.RootElement.GetProperty("Version").GetString() == manifest.Version &&
                document.RootElement.GetProperty("Runtime").GetString() == manifest.Runtime &&
                File.Exists(Path.Combine(folder, "local", "MeldResearch.LocalBackend" + Executable)) &&
                File.Exists(BrowserHost(folder)) && File.Exists(Launcher(folder)) &&
                File.Exists(Path.Combine(folder, "frontend", "index.html"));
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        { return false; }
    }

    private void Prune()
    {
        Prune(options.InstallRoot!, options.CurrentFolder);
    }

    /// <summary>Removes interrupted downloads and release folders other than the current and previous ones.</summary>
    internal static void Prune(string root, string currentFolder)
    {
        var pointer = InstallationPointer.Read(root);
        if (pointer.Current != currentFolder) return;
        string staging = Path.Combine(root, ".staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (name.Length == 12 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                name != pointer.Current && name != pointer.Previous)
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void MakeExecutable(string folder)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (string path in new[] { Path.Combine(folder, "local", "MeldResearch.LocalBackend"), BrowserHost(folder), Launcher(folder) })
            File.SetUnixFileMode(path, (UnixFileMode)0b111_000_000);
    }

    private static string Executable => OperatingSystem.IsWindows() ? ".exe" : "";
    private static string BrowserHost(string folder) => Path.Combine(folder, "browser-host", "MeldResearch.BrowserHost" + Executable);
    private static string Launcher(string folder) => Path.Combine(folder, OperatingSystem.IsWindows() ? "Start-Meld.ps1" : "Start-Meld.command");

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + read > maximum) throw new InvalidDataException("The update manifest is too large.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
}
