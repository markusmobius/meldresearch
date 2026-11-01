using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>The first file of an export: what it is, when and for whom it was made.</summary>
internal sealed record ExportManifest(string Format, int Version, DateTimeOffset ExportedAt, string AppVersion, ExportAccount Account,
    int Projects, string Notice);
internal sealed record ExportAccount(Guid Id, string Name, string Email);
internal sealed record ExportFile(byte[] Zip, string FileName, int Projects, Guid Account);

/// <summary>
/// Account → Meld account → Your data. Downloads the signed-in account's research as a ZIP of readable JSON (not encrypted):
/// the same project and profile records that encrypted sync uses, with the source texts each project's summary was made
/// from. Uploading such a ZIP merges it into this computer's research; encrypted sync then uploads it. Imported work is not
/// processed again: unchanged Overleaf content makes no model calls.
/// </summary>
internal sealed partial class ResearchArchive(IResearchStore store, ResearchCoordinator coordinator, Updates.UpdateOptions updates,
    TimeProvider clock, ILogger<ResearchArchive> logger, IHostApplicationLifetime? lifetime = null)
{
    public static readonly TimeSpan DownloadAvailable = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private ExportJob? _job;

    private sealed class ExportJob(string id, Guid account, DateTimeOffset startedAt)
    {
        public string Id { get; } = id;
        public Guid Account { get; } = account;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public ExportFile? File { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string? Error { get; set; }
        public Task Work { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// Queues the download: while one is being prepared, asking again returns it instead of starting another. The finished
    /// ZIP stays in memory (it is not encrypted, so it is never written to disk) for five minutes.
    /// </summary>
    public async Task<ExportStatusResponse> StartExportAsync(CancellationToken ct)
    {
        var account = (await store.ReadAsync(ct)).Credential?.Account.Id ??
            throw new ResearchFailure(401, "meld_account_required", "Sign in with Google first.");
        ExportJob job;
        lock (_gate)
        {
            if (_job is { FinishedAt: null } running && running.Account == account) return Status(running);
            job = _job = new ExportJob(Guid.NewGuid().ToString("N"), account, clock.GetUtcNow());
            job.Work = Task.Run(() => PrepareAsync(job));
        }
        return Status(job);
    }

    /// <summary>The account's current or recent download (null when there is none, or it expired).</summary>
    public ExportStatusResponse? CurrentExport(Guid? account)
    {
        lock (_gate)
        {
            Expire();
            return _job is { } job && job.Account == account ? Status(job) : null;
        }
    }

    public ExportFile? Download(string id, Guid? account)
    {
        lock (_gate)
        {
            Expire();
            return _job is { File: { } file } job && job.Id == id && job.Account == account ? file : null;
        }
    }

    /// <summary>Forgets any prepared download (leaving Meld Research).</summary>
    public void Clear() { lock (_gate) _job = null; }

    /// <summary>For tests: waits until the current download is prepared.</summary>
    internal Task PreparedAsync() { lock (_gate) return _job?.Work ?? Task.CompletedTask; }

    private async Task PrepareAsync(ExportJob job)
    {
        ExportFile? file = null;
        string? error = null;
        try
        {
            file = await ExportAsync(lifetime?.ApplicationStopping ?? CancellationToken.None);
            if (file.Account != job.Account) { file = null; error = "The account changed while the download was prepared."; }
        }
        catch (ResearchFailure failure) { error = failure.Message; }
        catch (Exception exception) when (exception is IOException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
        {
            error = "The download could not be prepared. Try again.";
            logger.LogWarning("Preparing the data download failed ({Type}).", exception.GetType().Name);
        }
        lock (_gate)
        {
            job.File = file;
            job.Error = error;
            job.FinishedAt = clock.GetUtcNow();
        }
    }

    private void Expire()
    {
        if (_job is { FinishedAt: { } finished } && clock.GetUtcNow() - finished >= DownloadAvailable) _job = null;
    }

    private static ExportStatusResponse Status(ExportJob job) => job switch
    {
        { FinishedAt: null } => new(job.Id, "preparing", job.StartedAt, null, null, null, null, 0, null, null),
        { File: { } file, FinishedAt: { } ready } => new(job.Id, "ready", job.StartedAt, ready, ready + DownloadAvailable, file.FileName,
            file.Zip.LongLength, file.Projects, $"/api/research/export/{job.Id}", null),
        _ => new(job.Id, "failed", job.StartedAt, job.FinishedAt, job.FinishedAt + DownloadAvailable, null, null, 0, null,
            job.Error ?? "The download could not be prepared. Try again.")
    };
    public const string Format = "meld-research-export", ManifestName = "meld-research-export.json", ProfileName = "profile.json";
    public const long MaxUploadBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private const int MaxEntries = 10_000;
    // Readable as written: accented letters, quotes and symbols in Overleaf text are not escaped (this is a file, not HTML).
    private static readonly JsonSerializerOptions Readable = new(ResearchJson.Options)
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ProjectIdPattern();

    public async Task<ExportFile> ExportAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        var credential = state.Credential ?? throw new ResearchFailure(401, "meld_account_required", "Sign in with Google first.");
        var account = credential.Account.Id;
        var now = clock.GetUtcNow();
        var projects = state.Projects.Where(item => item.AccountId == account && SyncBundles.HasResearch(item) &&
            ProjectIdPattern().IsMatch(item.ProjectId)).ToArray();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, ManifestName, new ExportManifest(Format, 1, now, updates.Version,
                new(account, credential.Account.Name, credential.Account.Email), projects.Length,
                "Your Meld Research data in readable JSON. It is not encrypted: keep it private. Upload it in Meld Research under " +
                "Account > Meld account > Your data to restore it."));
            Write(zip, ProfileName, SyncBundles.Profile(state, account, now));
            foreach (var project in projects)
            {
                var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var hash in SyncBundles.TextHashes(project))
                    if (await store.ReadTextAsync(hash, ct) is { } text) texts[hash] = text;
                Write(zip, $"projects/{project.ProjectId}.json", SyncBundles.Project(project, texts, now));
            }
            var readme = zip.CreateEntry("README.txt", CompressionLevel.Optimal);
            await using (var stream = readme.Open())
                await stream.WriteAsync(Encoding.UTF8.GetBytes(Readme), ct);
        }
        logger.LogInformation("Prepared the account's research ({Projects} project(s)) as a download.", projects.Length);
        return new(buffer.ToArray(), $"meld-research-{now:yyyy-MM-dd}.zip", projects.Length, account);
    }

    public async Task<ImportResponse> ImportAsync(Stream upload, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(upload, buffer, MaxUploadBytes, ct);
        buffer.Position = 0;
        ExportManifest manifest;
        ProfileBundle? profile = null;
        var projects = new List<ProjectBundle>();
        try
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) throw Invalid("It has too many files.");
            long expanded = 0;
            foreach (var entry in zip.Entries)
                if ((expanded += entry.Length) > MaxExpandedBytes) throw Invalid("It is too large when unpacked.");
            manifest = Read<ExportManifest>(zip.GetEntry(ManifestName) ?? throw Invalid($"It has no {ManifestName}."));
            if (manifest.Format != Format || manifest.Version != 1) throw Invalid("It is not a Meld Research export this version can read.");
            if (zip.GetEntry(ProfileName) is { } profileEntry) profile = Read<ProfileBundle>(profileEntry);
            foreach (var entry in zip.Entries.Where(item => item.FullName.StartsWith("projects/", StringComparison.Ordinal) && item.Name.Length != 0))
            {
                var bundle = Read<ProjectBundle>(entry);
                if (!ProjectIdPattern().IsMatch(bundle.ProjectId) || entry.FullName != $"projects/{bundle.ProjectId}.json")
                    throw Invalid($"{entry.FullName} does not match its project.");
                foreach (var (hash, text) in bundle.Texts)
                    if (ResearchEvidence.Hash(text) != hash) throw Invalid($"A source text in {entry.FullName} was changed.");
                projects.Add(bundle);
            }
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or NotSupportedException)
        {
            throw Invalid("It is not a readable Meld Research export.");
        }
        if (projects.Select(item => item.ProjectId).Distinct(StringComparer.Ordinal).Count() != projects.Count)
            throw Invalid("It lists a project twice.");

        return await coordinator.WithAccountAsync(async credential =>
        {
            var account = credential.Account.Id;
            // An export from another account (for example before leaving and signing up again) becomes this account's research.
            var mine = profile is null ? null : profile with
            {
                Decisions = [.. profile.Decisions.Select(item => item with { AccountId = account })],
                Themes = [.. profile.Themes.Select(item => item with { AccountId = account })],
                Summaries = [.. profile.Summaries.Select(item => item with { AccountId = account })],
                Researcher = profile.Researcher is { } researcher ? researcher with { AccountId = account } : null
            };
            foreach (var bundle in projects)
                foreach (var (hash, text) in bundle.Texts)
                    await store.WriteTextAsync(hash, text, ct);
            await store.ChangeAsync(current =>
            {
                if (current.Credential?.Account.Id != account)
                    throw new ResearchFailure(409, "research_state_changed", "The account changed. Reload and try again.");
                var state = mine is null ? current : SyncBundles.ApplyProfile(current, account, mine, null, ResearchEvidence.ConsentVersion);
                foreach (var bundle in projects) state = SyncBundles.ImportProject(state, account, bundle, ResearchEvidence.ConsentVersion);
                return state;
            }, ct);
            coordinator.Sync.RequestPull();
            logger.LogInformation("Imported an uploaded export ({Projects} project(s)); it is merged with synced research on the next check.",
                projects.Count);
            return new ImportResponse(projects.Count, mine?.Decisions.Count(item => item.Included) ?? 0, mine?.Themes.Count ?? 0,
                mine?.Summaries.Count ?? 0, mine?.Researcher?.Researcher is not null, manifest.Account.Email,
                manifest.Account.Id != account, manifest.ExportedAt);
        }, ct);
    }

    private static void Write<T>(ZipArchive zip, string name, T value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, Readable);
    }

    private static T Read<T>(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, ResearchJson.Options) ?? throw Invalid($"{entry.FullName} is empty.");
    }

    private static async Task CopyLimitedAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if ((total += read) > limit) throw new ResearchFailure(413, "export_too_large", "The file is larger than 256 MB.");
            await target.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        if (total == 0) throw Invalid("The file is empty.");
    }

    private static ResearchFailure Invalid(string reason) =>
        new(400, "invalid_export", "This file cannot be imported. " + reason);

    private const string Readme = """
        Meld Research data export
        =========================

        This ZIP holds your Meld Research data as readable JSON. It is NOT encrypted:
        it contains text from your Overleaf projects, so keep it private.

        meld-research-export.json  When and for which account it was made.
        profile.json               Your include/exclude choices, research cards,
                                   project summaries and confirmed OpenAlex author
                                   with their publications.
        projects/<id>.json         One file per Overleaf project: its summary
                                   (profile), Updates with citations, which file
                                   versions were folded in, and the source texts
                                   (keyed by SHA-256) the summary was made from.

        To restore it, open Meld Research, sign in, and choose
        Account > Meld account > Your data > Upload. Projects in the file are not
        processed again; only later changes on Overleaf are.
        """;
}
