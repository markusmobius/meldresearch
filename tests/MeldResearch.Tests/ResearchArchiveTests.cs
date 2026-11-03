using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.LocalBackend.Services.Updates;
using MeldResearch.Research;
using static MeldResearch.Tests.ResearchCoordinatorTests;

namespace MeldResearch.Tests;

public sealed class ResearchArchiveTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly MeldAccountResponse OtherAccount = new(Guid.NewGuid(), "Synthetic", "synthetic@example.invalid");
    private static readonly UpdateOptions Updates = new(false, "test", "development", null, null, null, null,
        new Uri("https://meld.example.invalid"), false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    private sealed record Computer(ResearchCoordinator Coordinator, MemoryStore Store, Remote Remote, Source Source, ResearchArchive Archive) : IDisposable
    {
        public void Dispose() => Coordinator.Dispose();
    }

    private static Computer New(LinkedCredential credential, ResearchSnapshot snapshot, BlobBox? box = null, params MonitoredProject[] projects)
    {
        var store = new MemoryStore(new(1, credential, null, null, projects, [],
            Decisions: [.. projects.Select(project => new ProjectDecision(credential.Account.Id, project.ProjectId, true, DateTimeOffset.UtcNow))]));
        var remote = new Remote { Box = box, Identity = credential.Account };
        var source = new Source(snapshot);
        var coordinator = new ResearchCoordinator(store, remote, source, new(new Uri("https://meld.example.invalid"), false, 300),
            NullLogger<ResearchCoordinator>.Instance, TimeProvider.System);
        return new(coordinator, store, remote, source,
            new ResearchArchive(store, coordinator, Updates, TimeProvider.System, NullLogger<ResearchArchive>.Instance));
    }

    private static int ModelCalls(Remote remote) => remote.Calls + remote.Blurbs.Count + remote.Matches.Count + remote.Merges.Count + remote.Regroups.Count + remote.DetailSummaries.Count + remote.DetailMerges.Count;

    [Fact]
    public async Task TheExportIsReadableJsonAndRestoresOnAFreshComputerWithoutModelCalls()
    {
        var snapshot = Files(("main.tex", MainDoc("Main text about scooping.")), ("a.tex", "Section A: Müller & <Schäfer>."));
        using var first = New(Credential, snapshot, null, Included());
        await Drain(first.Coordinator);
        var export = await first.Archive.ExportAsync(None);
        var (zip, name) = (export.Zip, export.FileName);
        Assert.Matches("^meld-research-\\d{4}-\\d{2}-\\d{2}\\.zip$", name);

        using (var archive = new ZipArchive(new MemoryStream(zip)))
        {
            Assert.Equal(["README.txt", "meld-research-export.json", "profile.json", $"projects/{ProjectId}.json"],
                archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
            string project = new StreamReader(archive.GetEntry($"projects/{ProjectId}.json")!.Open()).ReadToEnd();
            Assert.Contains("Main text about scooping.", project);
            Assert.Contains("Section A: Müller & <Schäfer>.", project);
            Assert.Contains("\n  ", project);
            using var manifest = JsonDocument.Parse(archive.GetEntry("meld-research-export.json")!.Open());
            Assert.Equal("synthetic@example.invalid", manifest.RootElement.GetProperty("account").GetProperty("email").GetString());
        }

        // A fresh computer, signed in as a new account (as after leaving and signing up again).
        var credential = new LinkedCredential(OtherAccount, new string('c', 64), DateTimeOffset.UtcNow.AddDays(1));
        using var fresh = New(credential, snapshot);
        var result = await fresh.Archive.ImportAsync(new MemoryStream(zip), None);
        Assert.Equal((1, 1, 1, true), (result.Projects, result.Included, result.Cards, result.OtherAccount));
        var restored = Assert.Single(fresh.Store.State.Projects);
        Assert.Equal((OtherAccount.Id, true, true), (restored.AccountId, restored.Enabled, restored.ImportComplete));
        Assert.Equal(first.Store.State.Projects.Single().Baseline!.Revision, restored.Baseline!.Revision);
        Assert.All(fresh.Store.State.Decisions!, decision => Assert.Equal(OtherAccount.Id, decision.AccountId));
        Assert.Equal(OtherAccount.Id, fresh.Store.State.Themes!.Single().AccountId);

        await Drain(fresh.Coordinator, force: true);
        Assert.Equal(0, ModelCalls(fresh.Remote));
        Assert.Contains("No model calls", fresh.Store.State.Projects.Single().Explanation);

        // A changed file is processed against the imported text.
        fresh.Source.Snapshot = Files(("main.tex", MainDoc("Main text about scooping, now with a field experiment.")), ("a.tex", "Section A: Müller & <Schäfer>."));
        await Drain(fresh.Coordinator, force: true);
        var step = Assert.Single(fresh.Remote.DetailRequests);
        Assert.Equal(["main.tex"], step.Documents.Select(document => document.Path));
        Assert.Contains("field experiment", Assert.Single(step.Documents).Text);
    }

    [Fact]
    public async Task DownloadsArePreparedOneAtATimeAndStayAvailableForFiveMinutes()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var computer = New(Credential, Files(("main.tex", MainDoc("Main text."))), null, Included());
        await Drain(computer.Coordinator);
        var archive = new ResearchArchive(computer.Store, computer.Coordinator, Updates, clock, NullLogger<ResearchArchive>.Instance);
        Assert.Null(archive.CurrentExport(Account.Id));

        await archive.StartExportAsync(None);
        await archive.PreparedAsync();
        var ready = archive.CurrentExport(Account.Id)!;
        Assert.Equal(("ready", 1, $"/api/research/export/{ready.Id}"), (ready.State, ready.Projects, ready.Url));
        Assert.Equal(ready.ReadyAt + ResearchArchive.DownloadAvailable, ready.ExpiresAt);
        var file = archive.Download(ready.Id, Account.Id)!;
        Assert.Equal(ready.Bytes, file.Zip.LongLength);
        Assert.Null(archive.Download(ready.Id, Guid.NewGuid()));
        Assert.Null(archive.CurrentExport(Guid.NewGuid()));
        Assert.Null(archive.Download("other", Account.Id));

        // While a download is being prepared, asking again returns the same one instead of queueing another.
        var gate = new TaskCompletionSource();
        var slow = new ResearchArchive(new SlowStore(computer.Store, gate.Task), computer.Coordinator, Updates, clock, NullLogger<ResearchArchive>.Instance);
        var first = await slow.StartExportAsync(None);
        var second = await slow.StartExportAsync(None);
        Assert.Equal(("preparing", first.Id), (second.State, second.Id));
        gate.SetResult();
        await slow.PreparedAsync();
        Assert.Equal("ready", slow.CurrentExport(Account.Id)!.State);

        clock.UtcNow += ResearchArchive.DownloadAvailable - TimeSpan.FromSeconds(1);
        Assert.NotNull(archive.CurrentExport(Account.Id));
        clock.UtcNow += TimeSpan.FromSeconds(1);
        Assert.Null(archive.CurrentExport(Account.Id));
        Assert.Null(archive.Download(ready.Id, Account.Id));
        Assert.NotEqual(ready.Id, (await archive.StartExportAsync(None)).Id);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    /// <summary>Holds reads of source texts until released, so a download stays in preparation.</summary>
    private sealed class SlowStore(IResearchStore inner, Task release) : IResearchStore
    {
        public Task<LocalResearchState> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
        public Task<LocalResearchState> ChangeAsync(Func<LocalResearchState, LocalResearchState> change, CancellationToken ct) => inner.ChangeAsync(change, ct);
        public Task WriteTextAsync(string sha256, string text, CancellationToken ct) => inner.WriteTextAsync(sha256, text, ct);
        public async Task<string?> ReadTextAsync(string sha256, CancellationToken ct) { await release; return await inner.ReadTextAsync(sha256, ct); }
        public void PruneTexts(IReadOnlySet<string> keep) => inner.PruneTexts(keep);
    }

    [Fact]
    public async Task AnImportIsUploadedByEncryptedSync()
    {
        var snapshot = Files(("main.tex", MainDoc("Main text.")));
        using var first = New(Credential, snapshot, null, Included());
        await Drain(first.Coordinator);
        var zip = (await first.Archive.ExportAsync(None)).Zip;

        var box = new BlobBox();
        using var fresh = New(Credential, snapshot, box);
        await fresh.Coordinator.WithSyncAsync((sync, credential) => sync.CreateAsync(credential, "correct horse battery", None), None);
        await Drain(fresh.Coordinator);
        Assert.DoesNotContain(box.Contents.Keys, key => key.StartsWith("p2/"));

        await fresh.Archive.ImportAsync(new MemoryStream(zip), None);
        await Drain(fresh.Coordinator);
        Assert.Single(box.Contents.Keys, key => key.StartsWith("p2/"));
        Assert.Equal(0, ModelCalls(fresh.Remote));
        Assert.Equal(1, (await fresh.Coordinator.StatusAsync(None)).Sync!.SyncedProjects);
    }

    [Fact]
    public async Task ImportKeepsLocalWorkThatGotFurtherAndNewerChoices()
    {
        var snapshot = Files(("main.tex", MainDoc("Main text.")), ("a.tex", "A."));
        using var first = New(Credential, snapshot, null, Included());
        Assert.True(await first.Coordinator.RunCycleAsync(false, None, maxSteps: 1));
        var partial = (await first.Archive.ExportAsync(None)).Zip;
        await Drain(first.Coordinator);
        var finished = first.Store.State.Projects.Single();
        await first.Coordinator.DecideProjectAsync(ProjectId, new(false, null), None);

        var result = await first.Archive.ImportAsync(new MemoryStream(partial), None);
        Assert.False(result.OtherAccount);
        Assert.Equal(finished.Processed!.Count, first.Store.State.Projects.Single().Processed!.Count);
        Assert.False(first.Store.State.Decisions!.Single().Included);
    }

    [Fact]
    public async Task ChangedOrForeignFilesAreRejectedWithoutChangingAnything()
    {
        var snapshot = Files(("main.tex", MainDoc("Main text.")));
        using var first = New(Credential, snapshot, null, Included());
        await Drain(first.Coordinator);
        var zip = (await first.Archive.ExportAsync(None)).Zip;
        byte[] tampered = Rewrite(zip, $"projects/{ProjectId}.json", text => text.Replace("Main text.", "Forged text."));

        using var fresh = New(Credential, snapshot);
        var changed = await Assert.ThrowsAsync<ResearchFailure>(() => fresh.Archive.ImportAsync(new MemoryStream(tampered), None));
        Assert.Equal("invalid_export", changed.Code);
        var other = await Assert.ThrowsAsync<ResearchFailure>(() => fresh.Archive.ImportAsync(new MemoryStream("not a zip"u8.ToArray()), None));
        Assert.Equal("invalid_export", other.Code);
        Assert.Empty(fresh.Store.State.Projects);
        Assert.Empty(fresh.Store.Texts);
    }

    [Fact]
    public async Task LeavingADevelopmentCopyDeletesTheAccountAndErasesLocalResearchWithoutUninstalling()
    {
        var box = new BlobBox();
        using var computer = New(Credential, Files(("main.tex", MainDoc("Main text."))), box, Included());
        await computer.Coordinator.WithSyncAsync((sync, credential) => sync.CreateAsync(credential, "correct horse battery", None), None);
        await Drain(computer.Coordinator);
        Assert.NotEmpty(computer.Store.Texts);
        var leave = new LeaveService(computer.Coordinator, computer.Store, computer.Remote, Updates, new(new Uri("https://meld.example.invalid"), false, 300),
            new Registrar(), new Lifetime(), NullLogger<LeaveService>.Instance);

        var unconfirmed = await Assert.ThrowsAsync<ResearchFailure>(() => leave.LeaveAsync(new("delete"), None));
        Assert.Equal("confirmation_required", unconfirmed.Code);
        Assert.Equal(0, computer.Remote.AccountDeletions);

        var result = await leave.LeaveAsync(new("DELETE"), None);
        Assert.Equal((false, true, null), (result.Uninstalling, result.AccountDeleted, result.LogPath));
        Assert.Equal(1, computer.Remote.AccountDeletions);
        Assert.Empty(box.Contents);
        Assert.Null(computer.Store.State.Credential);
        Assert.Null(computer.Store.State.Sync);
        Assert.Empty(computer.Store.State.Projects);
        Assert.Empty(computer.Store.Texts);
    }

    [Fact]
    public void TheUninstallerRefusesBroadFolders()
    {
        foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(), Path.GetPathRoot(Path.GetTempPath())! })
            Assert.Equal("uninstall_refused", Assert.Throws<ResearchFailure>(() => Uninstaller.Guard(folder)).Code);
        Uninstaller.Guard(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeldResearch", "App"));
    }

    [Fact]
    public async Task TheWindowsUninstallScriptRemovesTheInstallationAndDataFolders()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryDirectory();
        string parent = Path.Combine(temporary.Path, "MeldResearch");
        string root = Path.Combine(parent, "App"), data = Path.Combine(parent, "OverleafPrototype");
        Directory.CreateDirectory(Path.Combine(root, "abcdef012345", "local"));
        File.WriteAllText(Path.Combine(root, "current.json"), "{}");
        Directory.CreateDirectory(Path.Combine(data, "research", "texts"));
        File.WriteAllText(Path.Combine(data, "research", "research.protected"), "x");
        string shortcut = Path.Combine(temporary.Path, "Meld Research.lnk");
        File.WriteAllText(shortcut, "x");
        // An exited process id, a task name that does not exist, and folders in a temporary directory: nothing real is touched.
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        exited.WaitForExit();
        var plan = new UninstallPlan(exited.Id, root, data, Path.Combine(root, "missing.exe"), Path.Combine(temporary.Path, "uninstall.log"),
            Path.Combine(temporary.Path, "uninstall.ps1"), "MeldResearch-test-" + Guid.NewGuid().ToString("N"), shortcut, null);
        await File.WriteAllTextAsync(plan.ScriptPath, Uninstaller.WindowsScript(plan), new UTF8Encoding(true));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", plan.ScriptPath }
        };
        start.Environment.Remove("PSModulePath");
        using var script = Process.Start(start)!;
        string output = await script.StandardOutput.ReadToEndAsync() + await script.StandardError.ReadToEndAsync();
        await script.WaitForExitAsync();
        Assert.False(Directory.Exists(root), output);
        Assert.False(Directory.Exists(data), output);
        Assert.False(Directory.Exists(parent), output);
        Assert.False(File.Exists(shortcut), output);
        Assert.False(File.Exists(plan.ScriptPath), output);
        Assert.Contains("Meld Research was removed.", await File.ReadAllTextAsync(plan.LogPath));
    }

    private static byte[] Rewrite(byte[] zip, string name, Func<string, string> change)
    {
        using var copy = new MemoryStream();
        copy.Write(zip);
        using (var archive = new ZipArchive(copy, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry(name)!;
            string text;
            using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(change(text));
        }
        return copy.ToArray();
    }

    private sealed class Registrar : INativeHostRegistrar
    {
        public Task RegisterAsync(string browserHostExecutable, CancellationToken ct) => Task.CompletedTask;
        public Task UnregisterAsync(string browserHostExecutable, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Lifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool Stopped { get; private set; }
        public void StopApplication() => Stopped = true;
    }
}
