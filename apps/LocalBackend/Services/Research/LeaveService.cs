using System.Diagnostics;
using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Services.Updates;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Account → Meld account → Leave Meld Research. Deletes the Meld account and its encrypted synced data on Meld's server,
/// erases this computer's research, and, for an installed app, removes the installation: the sign-in task (or LaunchAgent),
/// the browser bridge registration, the Start menu entry, the application and its data folder. The removal finishes in a
/// small script that runs after this process has exited, because a running program cannot delete itself.
/// </summary>
internal sealed class LeaveService(ResearchCoordinator coordinator, IResearchStore store, IResearchRemote remote, UpdateOptions updates,
    ResearchOptions options, INativeHostRegistrar registrar, IHostApplicationLifetime lifetime, ILogger<LeaveService> logger,
    ResearchArchive? archive = null)
{
    public const string Confirmation = "DELETE";

    public async Task<LeaveResponse> LeaveAsync(LeaveRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.Confirmation?.Trim(), Confirmation, StringComparison.Ordinal))
            throw new ResearchFailure(400, "confirmation_required", $"Type {Confirmation} to confirm.");
        // Everything that could refuse is checked before anything is deleted.
        var plan = Uninstaller.Plan(updates, options);
        return await coordinator.HaltAsync(async state =>
        {
            bool deleted = false;
            if (state.Credential is { } credential)
            {
                if (credential.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new ResearchFailure(401, "meld_account_required",
                        "This computer's sign-in has expired. Sign in again so your account and synced data can be deleted too.");
                try { await remote.DeleteAccountAsync(credential.Token, ct); }
                catch (ResearchFailure failure) when (failure.Code == "meld_account_required")
                {
                    throw new ResearchFailure(401, "meld_account_required",
                        "Meld's server no longer accepts this computer's sign-in. Disconnect, sign in again, then leave.");
                }
                deleted = true;
                logger.LogInformation("Deleted the Meld account and its encrypted synced data on Meld's server.");
            }
            await EraseAsync(ct);
            archive?.Clear();
            if (plan is null)
            {
                logger.LogInformation("Erased this computer's research. This is not an installed copy, so nothing was uninstalled.");
                coordinator.Resume();
                return new LeaveResponse(false, deleted, null);
            }
            if (updates.RegisterNativeHost && File.Exists(plan.BrowserHost))
                try { await registrar.UnregisterAsync(plan.BrowserHost, ct); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                { logger.LogWarning("The browser bridge registration could not be removed ({Type}).", error.GetType().Name); }
            Uninstaller.Start(plan);
            logger.LogInformation("Uninstalling Meld Research; the application stops now.");
            // The answer reaches the page first; then the app exits (not with the update code, so the launcher does not restart it).
            _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(1.5)); lifetime.StopApplication(); }, CancellationToken.None);
            return new LeaveResponse(true, deleted, plan.LogPath);
        }, ct);
    }

    /// <summary>The research store becomes empty (no account); stored texts and downloaded archives are deleted.</summary>
    private async Task EraseAsync(CancellationToken ct)
    {
        await store.ChangeAsync(current => new LocalResearchState(1, null, null, null, [], [], ResetEpoch: current.ResetEpoch), ct);
        store.PruneTexts(new HashSet<string>(StringComparer.Ordinal));
        foreach (string folder in new[] { "downloads", "incoming" }.Select(name => Path.Combine(RuntimeFiles.Root, name)).Where(Directory.Exists))
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
                try { if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Removed with the data folder. */ }
    }
}

/// <summary>What the uninstall script removes; paths are checked so the script can never delete a broad folder.</summary>
internal sealed record UninstallPlan(int ProcessId, string InstallRoot, string DataDirectory, string BrowserHost, string LogPath,
    string ScriptPath, string? TaskName, string? Shortcut, string? KeychainAccount);

internal static class Uninstaller
{
    public const string WindowsTask = "MeldResearch", MacAgent = "org.meldresearch.desktop", KeychainService = "org.meldresearch.research-key";

    /// <summary>The removal plan for an installed app started by its launcher; null for development and test builds.</summary>
    public static UninstallPlan? Plan(UpdateOptions updates, ResearchOptions options)
    {
        if (options.Dummy || updates.InstallRoot is not { } root || !File.Exists(Path.Combine(root, InstallationPointer.FileName)) ||
            !AppContext.BaseDirectory.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        string data = RuntimeFiles.Root;
        Guard(root);
        Guard(data);
        string id = Guid.NewGuid().ToString("N");
        string temp = Path.GetTempPath();
        string host = Path.Combine(root, updates.CurrentFolder, "browser-host", "MeldResearch.BrowserHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (OperatingSystem.IsWindows())
            return new(Environment.ProcessId, Path.GetFullPath(root), Path.GetFullPath(data), host,
                Path.Combine(temp, "meldresearch-uninstall.log"), Path.Combine(temp, $"meldresearch-uninstall-{id}.ps1"), WindowsTask,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu",
                    "Programs", "Meld Research.lnk"), null);
        if (OperatingSystem.IsMacOS())
            return new(Environment.ProcessId, Path.GetFullPath(root), Path.GetFullPath(data), host,
                Path.Combine(temp, "meldresearch-uninstall.log"), Path.Combine(temp, $"meldresearch-uninstall-{id}.sh"), MacAgent, null,
                ResearchEvidence.Hash(Path.GetFullPath(Path.Combine(data, "research"))));
        return null;
    }

    /// <summary>Refuses roots, home and well-known folders: only a dedicated Meld folder may be removed recursively.</summary>
    internal static void Guard(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string[] forbidden = [.. new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.Windows
        }.Select(Environment.GetFolderPath).Where(item => item.Length != 0).Select(Path.TrimEndingDirectorySeparator),
            Path.TrimEndingDirectorySeparator(Path.GetTempPath())];
        int depth = full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 3 || Path.GetPathRoot(full) is { } drive && string.Equals(Path.TrimEndingDirectorySeparator(drive), full, comparison) ||
            forbidden.Any(item => string.Equals(item, full, comparison) || item.StartsWith(full + Path.DirectorySeparatorChar, comparison)))
            throw new ResearchFailure(409, "uninstall_refused", "Meld Research will not remove this folder automatically: " + full);
    }

    public static string WindowsScript(UninstallPlan plan) => $$"""
        # Removes Meld Research after the application has stopped (written by "Leave Meld Research").
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        $processId = {{plan.ProcessId}}
        $root = {{Quote(plan.InstallRoot)}}
        $data = {{Quote(plan.DataDirectory)}}
        $taskName = {{Quote(plan.TaskName ?? "")}}
        $shortcut = {{Quote(plan.Shortcut ?? "")}}
        $null = Start-Transcript -LiteralPath {{Quote(plan.LogPath)}} -Force
        Write-Output "Removing Meld Research from $root and $data"
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($process) { $null = $process.WaitForExit(60000) }
        if ($taskName -and (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
            Write-Output "Removed the sign-in task $taskName."
        }
        if ($shortcut -and (Test-Path -LiteralPath $shortcut)) { Remove-Item -LiteralPath $shortcut -Force; Write-Output 'Removed the Start menu entry.' }
        # Only this installation's own launcher, application and browser bridge are stopped.
        $launcher = Join-Path $root 'Start-Meld.ps1'
        Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe' OR Name = 'pwsh.exe' OR Name = 'conhost.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.IndexOf($launcher, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Get-Process -Name 'MeldResearch.LocalBackend', 'MeldResearch.BrowserHost' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force -ErrorAction SilentlyContinue
        foreach ($directory in @($data, $root)) {
            for ($attempt = 0; $attempt -lt 30 -and (Test-Path -LiteralPath $directory); $attempt++) {
                Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $directory) { Start-Sleep -Seconds 2 }
            }
            if (Test-Path -LiteralPath $directory) { Write-Output "Could not remove $directory." } else { Write-Output "Removed $directory." }
        }
        foreach ($parent in @((Split-Path -Parent $data), (Split-Path -Parent $root)) | Select-Object -Unique) {
            if ((Test-Path -LiteralPath $parent) -and -not (Get-ChildItem -LiteralPath $parent -Force)) { Remove-Item -LiteralPath $parent -Force }
        }
        Write-Output 'Meld Research was removed.'
        $null = Stop-Transcript
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

    public static string MacScript(UninstallPlan plan) => $$"""
        #!/bin/sh
        # Removes Meld Research after the application has stopped (written by "Leave Meld Research").
        pid={{plan.ProcessId}}
        root={{ShellQuote(plan.InstallRoot)}}
        data={{ShellQuote(plan.DataDirectory)}}
        label={{ShellQuote(plan.TaskName ?? MacAgent)}}
        exec >{{ShellQuote(plan.LogPath)}} 2>&1
        echo "Removing Meld Research from $root and $data"
        attempt=0
        while kill -0 "$pid" 2>/dev/null && [ "$attempt" -lt 120 ]; do sleep 0.5; attempt=$((attempt + 1)); done
        rm -f "$HOME/Library/LaunchAgents/$label.plist"
        /usr/bin/pkill -f "$root/Start-Meld.command" 2>/dev/null || true
        /usr/bin/pkill -f "MeldResearch.LocalBackend .*--InstallRoot $root" 2>/dev/null || true
        /usr/bin/pkill -f "$root/.*/MeldResearch.BrowserHost" 2>/dev/null || true
        /usr/bin/security delete-generic-password -s {{ShellQuote(KeychainService)}} -a {{ShellQuote(plan.KeychainAccount ?? "")}} >/dev/null 2>&1 || true
        rm -rf "$data" "$root" "$HOME/Library/Logs/MeldResearch"
        rmdir "$(dirname "$data")" "$(dirname "$root")" 2>/dev/null || true
        echo "Meld Research was removed."
        rm -f "$0"
        # Last, because unloading the login item can end this script with it.
        /bin/launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true
        """;

    /// <summary>
    /// Starts the script outside this process tree: on Windows through WMI, so it is not part of the sign-in task's job and
    /// survives the application, its launcher and the task ending.
    /// </summary>
    public static void Start(UninstallPlan plan)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(plan.ScriptPath, WindowsScript(plan), new System.Text.UTF8Encoding(true));
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            string command = $"\"{powershell}\" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{plan.ScriptPath}\"";
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                    "$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $env:MELD_UNINSTALL }; exit $r.ReturnValue" }
            };
            // Windows PowerShell must not see PowerShell 7's module path, which hides its built-in modules.
            start.Environment.Remove("PSModulePath");
            start.Environment["MELD_UNINSTALL"] = command;
            using var launcher = Process.Start(start) ?? throw new InvalidOperationException("The uninstaller could not start.");
            launcher.WaitForExit(30_000);
            if (!launcher.HasExited || launcher.ExitCode != 0)
            {
                // Fallback: a direct child; it normally survives the application exiting too.
                start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true,
                    ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", plan.ScriptPath } };
                start.Environment.Remove("PSModulePath");
                Process.Start(start)?.Dispose();
            }
            return;
        }
        File.WriteAllText(plan.ScriptPath, MacScript(plan));
        File.SetUnixFileMode(plan.ScriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var process = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false, ArgumentList = { "-c", "/usr/bin/nohup /bin/sh \"$0\" >/dev/null 2>&1 &", plan.ScriptPath }
        });
        process?.WaitForExit(10_000);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
