using System.Text.Json;
using System.Text.Json.Serialization;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>A project's synced research: everything needed to continue it on another computer, including the texts.</summary>
internal sealed record ProjectBundle(int Version, string ProjectId, string Name, ProjectProfile? Profile,
    IReadOnlyDictionary<string, string>? Processed, ProjectManifest? Baseline, ProjectManifest? Pending, IReadOnlyList<string>? PendingOrder,
    DateTimeOffset? PendingSince, bool ImportComplete, DateTimeOffset? LastSweepAt, DateTimeOffset? LastSuccessAt,
    IReadOnlyList<ResearchUpdate> Updates, IReadOnlyDictionary<string, string> Texts, DateTimeOffset SavedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Parent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PaperInfo? Paper = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PaperFor = null);

/// <summary>A research card member that is not an Overleaf project (a Gmail workstream), so other computers can add it.</summary>
internal sealed record WorkstreamRef(string Id, string Source, string? Parent, string Name);

/// <summary>The account's synced choices: include/exclude decisions, research cards, project summaries and the OpenAlex match.</summary>
internal sealed record ProfileBundle(int Version, IReadOnlyList<ProjectDecision> Decisions, IReadOnlyDictionary<string, string> ProjectNames,
    IReadOnlyList<ResearchTheme> Themes, string? ThemesRegroupedFor, IReadOnlyList<ProjectSummaryState> Summaries,
    ResearcherState? Researcher, DateTimeOffset SavedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorkstreamRef>? Workstreams = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GmailAccountState>? GmailAccounts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GmailThreadRecord>? GmailThreads = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ThemesVersion = null);

/// <summary>
/// Builds and applies sync bundles. Fingerprints ignore texts (determined by the hashes) and save times, so they change only
/// when the research itself changed, which is what decides whether to upload.
/// </summary>
internal static class SyncBundles
{
    private static readonly DateTimeOffset Unsaved = DateTimeOffset.UnixEpoch;

    public static ProjectBundle Project(MonitoredProject project, IReadOnlyDictionary<string, string> texts, DateTimeOffset now) =>
        new(1, project.ProjectId, project.Name, project.Profile, project.Processed, project.Baseline, project.Pending, project.PendingOrder,
            project.PendingSince, project.ImportComplete, project.LastSweepAt, project.LastSuccessAt, project.Updates, texts, now,
            project.IsOverleaf ? null : project.Source, project.IsOverleaf ? null : project.Parent, project.Paper, project.PaperFor);

    public static string Fingerprint(MonitoredProject project) => Fingerprint(Project(project, new Dictionary<string, string>(), Unsaved));

    // A check that found nothing new only refreshes check times; that alone is no reason to upload the project again.
    public static string Fingerprint(ProjectBundle bundle) =>
        Hash(bundle with
        {
            Texts = new Dictionary<string, string>(), SavedAt = Unsaved, LastSweepAt = null, LastSuccessAt = null,
            Baseline = bundle.Baseline is { } baseline ? baseline with { ObservedAt = Unsaved } : null,
            Pending = bundle.Pending is { } pending ? pending with { ObservedAt = Unsaved } : null
        });

    /// <summary>The text hashes a bundle carries: what the summary reflects and what the stored or pending file lists name.</summary>
    public static IReadOnlySet<string> TextHashes(MonitoredProject project) =>
        (project.Processed?.Values ?? []).Concat(new[] { project.Baseline, project.Pending }.Where(manifest => manifest is not null)
            .SelectMany(manifest => manifest!.Files.Where(file => file.Analyzed).Select(file => file.Sha256))).ToHashSet(StringComparer.Ordinal);

    public static bool HasResearch(MonitoredProject project) => project.Profile is not null || project.Pending is not null;

    public static ProfileBundle Profile(LocalResearchState state, Guid account, DateTimeOffset now) =>
        new(1, [.. (state.Decisions ?? []).Where(item => item.AccountId == account).OrderBy(item => item.ProjectId, StringComparer.Ordinal)],
            state.Projects.Where(item => item.AccountId == account).GroupBy(item => item.ProjectId)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal),
            [.. ThemePlan.Themes(state, account).OrderBy(item => item.Id, StringComparer.Ordinal)], state.ThemesRegroupedFor,
            [.. (state.Summaries ?? []).Where(item => item.AccountId == account).OrderBy(item => item.ProjectId, StringComparer.Ordinal)],
            state.Researchers?.FirstOrDefault(item => item.AccountId == account), now, Workstreams(state, account), GmailAccounts(state, account),
            GmailThreads(state, account), state.ThemesVersion == 0 ? null : state.ThemesVersion);

    private static IReadOnlyList<GmailThreadRecord>? GmailThreads(LocalResearchState state, Guid account)
    {
        var items = (state.GmailThreads ?? []).Where(item => item.AccountId == account)
            .OrderBy(item => item.Address, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        return items.Length == 0 ? null : items;
    }

    /// <summary>Recorded Gmail threads from two places merged: per thread the later change wins.</summary>
    public static IReadOnlyList<GmailThreadRecord>? MergeThreads(IReadOnlyList<GmailThreadRecord>? local, IEnumerable<GmailThreadRecord>? remote, Guid account)
    {
        if (remote is null) return local;
        var merged = (local ?? []).Where(item => item.AccountId != account).ToList();
        merged.AddRange((local ?? []).Where(item => item.AccountId == account).Concat(remote.Where(item => item.AccountId == account))
            .GroupBy(item => (Address: item.Address.ToLowerInvariant(), item.Id)).Select(group => group.MaxBy(item => item.UpdatedAt)!));
        return merged.Count == 0 ? local : merged;
    }

    private static IReadOnlyList<GmailAccountState>? GmailAccounts(LocalResearchState state, Guid account)
    {
        var items = (state.GmailAccounts ?? []).Where(item => item.AccountId == account).OrderBy(item => item.Address, StringComparer.Ordinal).ToArray();
        return items.Length == 0 ? null : items;
    }

    /// <summary>
    /// Connected Gmail accounts from two places merged per address: the newer connection (or removal) wins; the last import
    /// and its analysis version are the later ones, and the import lease is the synced one, so a computer learns that another
    /// one is importing.
    /// </summary>
    public static IReadOnlyList<GmailAccountState>? MergeGmail(IReadOnlyList<GmailAccountState>? local, IEnumerable<GmailAccountState>? remote, Guid account)
    {
        var merged = (local ?? []).Where(item => item.AccountId != account).ToList();
        var mine = (local ?? []).Where(item => item.AccountId == account).ToDictionary(item => item.Address, StringComparer.OrdinalIgnoreCase);
        foreach (var item in remote ?? [])
        {
            if (item.AccountId != account) continue;
            mine[item.Address] = mine.TryGetValue(item.Address, out var current) ? Merge(current, item) : item;
        }
        merged.AddRange(mine.Values);
        return merged.Count == 0 ? local : merged;
    }

    private static GmailAccountState Merge(GmailAccountState local, GmailAccountState remote)
    {
        var connection = remote.ChangedAt > local.ChangedAt ? remote : local;
        if (connection.Removed) return connection;
        DateTimeOffset? last = local.LastImportAt > remote.LastImportAt || remote.LastImportAt is null ? local.LastImportAt : remote.LastImportAt;
        var (leaseBy, leaseUntil) = remote.LeaseBy is not null && remote.LeaseBy == local.LeaseBy && local.LeaseUntil > remote.LeaseUntil
            ? (local.LeaseBy, local.LeaseUntil) : (remote.LeaseBy, remote.LeaseUntil);
        return connection with
        {
            Processed = null, LastImportAt = last, LeaseBy = leaseBy, LeaseUntil = leaseUntil,
            ImportVersion = local.ImportVersion > remote.ImportVersion || remote.ImportVersion is null ? local.ImportVersion : remote.ImportVersion
        };
    }

    private static IReadOnlyList<WorkstreamRef>? Workstreams(LocalResearchState state, Guid account)
    {
        var items = state.Projects.Where(item => item.AccountId == account && !item.IsOverleaf)
            .OrderBy(item => item.ProjectId, StringComparer.Ordinal)
            .Select(item => new WorkstreamRef(item.ProjectId, item.Source!, item.Parent, item.Name)).ToArray();
        return items.Length == 0 ? null : items;
    }

    public static string Fingerprint(ProfileBundle bundle) => Hash(bundle with { SavedAt = Unsaved });

    /// <summary>
    /// Adopts another computer's profile: decisions merge (the newest choice per project wins). Research cards, project
    /// summaries and the OpenAlex match are taken as synced, unless this computer changed its profile since its last sync
    /// (for example right after unlocking, or after a start over elsewhere uploaded an empty profile): then the remote cards
    /// win for the projects they cover and this computer's other cards, newer summaries and its OpenAlex match are kept, so
    /// nothing made here has to be made again. Projects included elsewhere are added here (the user agreed to processing
    /// there); excluded ones stop here. Without an ETag the bundle comes from an uploaded export: it is merged the same way,
    /// always keeping this computer's own cards, and the sync state is left alone so the result is uploaded.
    /// </summary>
    public static LocalResearchState ApplyProfile(LocalResearchState state, Guid account, ProfileBundle bundle, string? etag, string consentVersion)
    {
        bool localChanged = etag is null || state.Sync?.ProfileSyncedFor is not { } synced || Fingerprint(Profile(state, account, Unsaved)) != synced;
        var remoteThemes = bundle.Themes.Where(item => item.AccountId == account).ToArray();
        IReadOnlyList<ResearchTheme> themes = remoteThemes;
        string? regrouped = bundle.ThemesRegroupedFor;
        var summaries = bundle.Summaries.Where(item => item.AccountId == account).ToArray();
        var researcher = bundle.Researcher is { } synced2 && synced2.AccountId == account ? synced2 : null;
        if (localChanged)
        {
            var covered = remoteThemes.SelectMany(item => item.Members).ToHashSet(StringComparer.Ordinal);
            var kept = (state.Themes ?? []).Where(item => item.AccountId == account).Select(item => item with
            {
                Members = [.. item.Members.Where(member => !covered.Contains(member))],
                MergedFrom = item.MergedFrom?.Where(entry => !covered.Contains(entry.Key)).ToDictionary(StringComparer.Ordinal),
                Pinned = item.Pinned?.Where(member => !covered.Contains(member)).ToArray()
            }).Where(item => item.Members.Count != 0).ToArray();
            if (kept.Length != 0)
            {
                themes = [.. remoteThemes, .. kept];
                regrouped = remoteThemes.Length == 0 ? state.ThemesRegroupedFor : null;
            }
            summaries = [.. summaries.Concat((state.Summaries ?? []).Where(item => item.AccountId == account)).GroupBy(item => item.ProjectId)
                .Select(group => group.MaxBy(item => item.UpdatedAt ?? item.RequestedAt)!)];
            var local = state.Researchers?.FirstOrDefault(item => item.AccountId == account);
            if (local is not null && (researcher is null || (local.UpdatedAt ?? DateTimeOffset.MinValue) > (researcher.UpdatedAt ?? DateTimeOffset.MinValue)))
                researcher = local;
        }
        var decisions = (state.Decisions ?? []).Where(item => item.AccountId == account).Concat(bundle.Decisions.Where(item => item.AccountId == account))
            .GroupBy(item => item.ProjectId).Select(group => group.MaxBy(item => item.DecidedAt)!).ToArray();
        var projects = state.Projects.ToList();
        foreach (var decision in decisions)
        {
            int index = projects.FindIndex(item => item.AccountId == account && item.ProjectId == decision.ProjectId);
            if (decision.Included && index < 0)
                projects.Add(new MonitoredProject(account, decision.ProjectId, bundle.ProjectNames.GetValueOrDefault(decision.ProjectId) ?? "Overleaf project",
                    true, consentVersion, null, null, [], [], null, null, null, null, null, 0, 0));
            else if (!decision.Included && index >= 0 && projects[index].Enabled) projects[index] = projects[index] with { Enabled = false };
        }
        // Workstreams found on another computer (for example from Gmail) are added here; their content follows with the project pull.
        foreach (var workstream in bundle.Workstreams ?? [])
        {
            if (!ResearchSources.IsGmailWorkstreamId(workstream.Id) || workstream.Source != ResearchSources.Gmail ||
                projects.Any(item => item.AccountId == account && item.ProjectId == workstream.Id)) continue;
            // An excluded workstream (for example one replaced by a newer email analysis) is not added again.
            if (decisions.Any(item => item.ProjectId == workstream.Id && !item.Included)) continue;
            projects.Add(new MonitoredProject(account, workstream.Id, workstream.Name, true, consentVersion,
                null, null, [], [], null, null, null, null, null, 0, 0, Source: workstream.Source, Parent: workstream.Parent));
        }
        return state with
        {
            Projects = projects,
            Decisions = [.. (state.Decisions ?? []).Where(item => item.AccountId != account), .. decisions],
            Themes = [.. (state.Themes ?? []).Where(item => item.AccountId != account), .. themes],
            ThemesRegroupedFor = regrouped,
            Summaries = [.. (state.Summaries ?? []).Where(item => item.AccountId != account), .. summaries],
            Researchers = researcher is not null
                ? [.. (state.Researchers ?? []).Where(item => item.AccountId != account), researcher] : state.Researchers,
            GmailAccounts = MergeGmail(state.GmailAccounts, bundle.GmailAccounts, account),
            GmailThreads = MergeThreads(state.GmailThreads, bundle.GmailThreads, account),
            ThemesVersion = Math.Max(state.ThemesVersion, bundle.ThemesVersion ?? 0),
            Sync = etag is null ? state.Sync : state.Sync! with { ProfileETag = etag, ProfileSyncedFor = Fingerprint(bundle) }
        };
    }

    /// <summary>
    /// Adds a project's work from an uploaded export. A project this computer does not have is created (monitored when its
    /// choice says included); existing local work is replaced only when the export folded in more files. Imported work is
    /// left unsynced, so encrypted sync uploads it (or reconciles it with a newer synced copy) on its next check.
    /// </summary>
    public static LocalResearchState ImportProject(LocalResearchState state, Guid account, ProjectBundle bundle, string consentVersion)
    {
        var projects = state.Projects.ToList();
        int index = projects.FindIndex(item => item.AccountId == account && item.ProjectId == bundle.ProjectId);
        var decision = (state.Decisions ?? []).FirstOrDefault(item => item.AccountId == account && item.ProjectId == bundle.ProjectId);
        // An Overleaf project is monitored when its choice says included; a Gmail workstream unless it was excluded.
        bool included = bundle.Source is null ? decision?.Included ?? false : decision?.Included ?? true;
        if (index < 0)
        {
            projects.Add(new MonitoredProject(account, bundle.ProjectId, bundle.Name, included, consentVersion,
                null, null, [], [], null, null, null, null, null, 0, 0, Source: bundle.Source, Parent: bundle.Parent));
            index = projects.Count - 1;
        }
        var local = projects[index];
        if (HasResearch(local) && (local.Processed?.Count ?? 0) >= (bundle.Processed?.Count ?? 0)) return state;
        projects[index] = local with
        {
            Name = bundle.Name, Profile = bundle.Profile, Processed = bundle.Processed, Baseline = bundle.Baseline, Pending = bundle.Pending,
            PendingOrder = bundle.PendingOrder, PendingSince = bundle.PendingSince, ImportComplete = bundle.ImportComplete,
            LastSweepAt = bundle.LastSweepAt, LastSuccessAt = bundle.LastSuccessAt, Updates = bundle.Updates,
            Snapshot = null, Summary = null, Facts = [], ErrorCode = null, Error = null, FailureCount = 0, NextAttemptAt = null,
            SyncedFor = null, Paper = bundle.Paper, PaperFor = bundle.PaperFor
        };
        return state with { Projects = projects };
    }

    /// <summary>
    /// Adopts another computer's work on a project, unless this computer has unsynced work that got further (more files
    /// folded in); then the local work is kept and uploaded over the remote copy on the next push.
    /// </summary>
    public static LocalResearchState ApplyProject(LocalResearchState state, Guid account, ProjectBundle bundle, string etag)
    {
        var local = state.Projects.FirstOrDefault(item => item.AccountId == account && item.ProjectId == bundle.ProjectId);
        if (local is null) return state;
        bool unsynced = HasResearch(local) && Fingerprint(local) != local.SyncedFor;
        var adopted = unsynced && (local.Processed?.Count ?? 0) > (bundle.Processed?.Count ?? 0)
            ? local with { SyncETag = etag }
            : local with
            {
                Name = bundle.Name, Profile = bundle.Profile, Processed = bundle.Processed, Baseline = bundle.Baseline, Pending = bundle.Pending,
                PendingOrder = bundle.PendingOrder, PendingSince = bundle.PendingSince, ImportComplete = bundle.ImportComplete,
                LastSweepAt = bundle.LastSweepAt, LastSuccessAt = bundle.LastSuccessAt, Updates = bundle.Updates,
                Snapshot = null, Summary = null, Facts = [], ErrorCode = null, Error = null, FailureCount = 0, NextAttemptAt = null,
                SyncETag = etag, SyncedFor = Fingerprint(bundle), LastSyncedAt = bundle.SavedAt,
                Source = local.Source ?? bundle.Source, Parent = local.Parent ?? bundle.Parent, Paper = bundle.Paper, PaperFor = bundle.PaperFor
            };
        return state with { Projects = [.. state.Projects.Select(item => item == local ? adopted : item)] };
    }

    private static string Hash<T>(T value) => ResearchEvidence.Hash(JsonSerializer.Serialize(value, ResearchJson.Options));
}
