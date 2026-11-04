using System.Security.Cryptography;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Middleware;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal interface IResearchCoordinator
{
    Task<ResearchStatusResponse> StatusAsync(CancellationToken ct);
    /// <summary>Starts a browser sign-in on the website that returns to <paramref name="callbackUri"/> (loopback + PKCE).</summary>
    Task<AccountLinkResponse> BeginLinkAsync(string callbackUri, CancellationToken ct);
    /// <summary>Completes a browser sign-in from the loopback callback. Failures are reported in the status, not thrown.</summary>
    Task CompleteLinkAsync(string? code, string? state, CancellationToken ct);
    Task UnlinkAsync(CancellationToken ct);
    Task SetMonitoringAsync(string projectId, SetProjectMonitoringRequest request, CancellationToken ct);
    /// <summary>Records an active include/exclude choice. Including starts monitoring and requires current consent.</summary>
    Task DecideProjectAsync(string projectId, ProjectDecisionRequest request, CancellationToken ct);
    Task ForgetAsync(string projectId, CancellationToken ct);
    /// <summary>Queues a one-paragraph summary of a recent project from its main .tex file; choosing it is the consent.</summary>
    Task RequestSummaryAsync(string projectId, CancellationToken ct);
    /// <summary>Moves a project to another research card, or to a card of its own when themeId is null.</summary>
    Task MoveToThemeAsync(string projectId, string? themeId, CancellationToken ct);
    /// <summary>Combines one research card into another; all its projects are pinned there.</summary>
    Task MergeThemeIntoAsync(string themeId, string intoThemeId, CancellationToken ct);
    /// <summary>Chooses the project a research card is described from (its main paper).</summary>
    Task SetAnchorAsync(string themeId, string projectId, CancellationToken ct);
    /// <summary>Skips a research card as not the user's own research, or brings it back into the profile.</summary>
    Task SkipThemeAsync(string themeId, bool skipped, CancellationToken ct);
    void RequestSync();
    /// <summary>True while a research cycle is reading sources or calling the remote models.</summary>
    bool Busy { get; }
}

internal sealed class ResearchCoordinator(IResearchStore store, IResearchRemote remote, IOverleafEvidenceSource source,
    ResearchOptions options, ILogger<ResearchCoordinator> logger, TimeProvider clock, ResearchSync? sync = null, GmailConnector? gmail = null,
    GmailImporter? importer = null)
    : BackgroundService, IResearchCoordinator
{
    private readonly ResearchSync _sync = sync ?? new(store, remote, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResearchSync>.Instance);
    internal ResearchSync Sync => _sync;
    private sealed record PendingSignIn(string State, string Verifier, string SignInUrl, DateTimeOffset ExpiresAt);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private readonly object _cycleGate = new();
    private CancellationTokenSource? _cycle;
    // The project the cycle is processing now, so pausing or excluding another project does not interrupt it.
    // Projects being processed right now (several at once), each with its own cancellation.
    private readonly Dictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private static readonly TimeSpan ProjectDeadline = TimeSpan.FromMinutes(10);
    // Held in memory only: a sign-in is a short browser round trip; after a restart the user simply signs in again.
    private PendingSignIn? _signIn;
    private int _busy;
    private int _force;
    // Set when the user includes or resumes a project: the worker stops after its current file step and reads that project.
    private int _urgent;
    /// <summary>
    /// Raise to start every installation's research over once, on its first start of a release (see <see cref="Fresh"/>).
    /// Epoch 2 introduced the details format; epoch 3 rebuilds it once more with dated citations and reviews, together with
    /// sync format 3 (SyncCrypto), so research synced by the earlier build cannot come back.
    /// </summary>
    internal const int ResetEpoch = 3;
    /// <summary>
    /// The one recency window (OVERLEAF.md §2): projects updated within it need an include/exclude choice and can be summarized,
    /// and only document trees edited within it are read.
    /// </summary>
    internal static readonly TimeSpan SummaryWindow = TimeSpan.FromDays(RecentDays);
    internal const int RecentDays = 183;
    /// <summary>A research project's details are reviewed for overlaps and overtaken plans at most this often (OVERLEAF.md §8a).</summary>
    internal static readonly TimeSpan ReviewInterval = TimeSpan.FromDays(30);
    private volatile string? _summarizing;
    // After a failed grouping call, card work waits instead of retrying in a tight loop.
    private DateTimeOffset _themeRetryAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ThemeBackoff = TimeSpan.FromMinutes(15);
    /// <summary>
    /// Failures of the connection to Meld's server (dropped, timed out, behind its proxy, busy) or of the browser's Overleaf
    /// reader tab (closed, moved, asleep). They are retried like other failures but shown as waiting rather than as errors
    /// until they persist.
    /// </summary>
    internal static bool Transient(string? code) =>
        code is "remote_unavailable" or "remote_timeout" or "remote_gateway_error" or "foundry_busy" or "browser_unavailable" or
            "overleaf_tab_closed" or "overleaf_tab_navigated" or "overleaf_tab_asleep" or "overleaf_tab_unavailable";
    /// <summary>A project's server problem is shown as an error after this many consecutive failures (about 15 minutes).</summary>
    internal const int TransientFailuresShown = 4;
    private static readonly TimeSpan ServerProblemShownAfter = TimeSpan.FromMinutes(5);
    private DateTimeOffset? _serverFailingSince;
    private const string RevocationPending = "Local access has stopped. Remote credential revocation is pending and will retry automatically.";
    public bool Busy => Volatile.Read(ref _busy) != 0;

    public async Task<ResearchStatusResponse> StatusAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        Guid? account = state.Credential?.Account.Id;
        var projects = state.Projects.Where(project => project.AccountId == account).ToArray();
        // Work skipped as not the user's own research stays out of the feed.
        var skippedMembers = account is { } owner0 ? ThemePlan.SkippedMembers(state, owner0) : new HashSet<string>();
        var updates = projects.Where(project => !skippedMembers.Contains(project.ProjectId)).SelectMany(project => project.Updates)
            .OrderByDescending(update => update.ObservedAt).ToArray();
        // The Updates feed: changelog entries that changed the details of projects in the profile (OVERLEAF.md §8).
        var feedThemes = account is { } owner1 ? ThemePlan.Active(state, owner1) : [];
        var feedIds = feedThemes.Select(theme => theme.Id).ToHashSet(StringComparer.Ordinal);
        // A review only tidies the details (no new research), so it is in the changelog but not in the feed.
        var changes = (state.Changelog ?? []).Where(entry => entry.AccountId == account && entry.Changes.Count != 0 && feedIds.Contains(entry.CardId) &&
                entry.Kind != ChangelogEntry.Review)
            .OrderByDescending(entry => entry.At).ThenByDescending(entry => entry.RecordedAt).Select(entry => Change(entry, feedThemes, state)).ToArray();
        var signIn = _signIn is { } pending && pending.ExpiresAt > clock.GetUtcNow() && state.Credential is null ? pending : null;
        var researcher = state.Researchers?.FirstOrDefault(item => item.AccountId == account);
        HashSet<string> active;
        lock (_cycleGate) active = [.. _active.Keys];
        bool busy = Volatile.Read(ref _busy) != 0;
        var queue = QueueOrder(projects.Where(project => project.Enabled && project.Pending is not null)).ToArray();
        var running = queue.Where(project => active.Contains(project.ProjectId)).ToArray();
        if (running.Length == 0 && busy && queue.Length != 0) running = [queue[0]];
        ProcessingStatusResponse? processing = null;
        if (running.Length != 0)
        {
            var first = running[0];
            var (done, total) = DetailPlan.Progress(first);
            processing = new(first.ProjectId, first.Name, first.ImportComplete ? "update" : "import", done, total, queue.Length - running.Length,
                first.PendingTrees?.SelectMany(tree => tree.Send).FirstOrDefault(path => first.Processed?.GetValueOrDefault(path) is not { } hash ||
                    first.Pending?.Files.FirstOrDefault(file => file.Path == path)?.Sha256 != hash),
                running.Skip(1).Select(project => project.Name).ToArray());
        }
        string Phase(MonitoredProject project) =>
            project.ErrorCode is not null && Transient(project.ErrorCode) && project.FailureCount < TransientFailuresShown ? "retrying"
            : project.ErrorCode is not null ? "error"
            : !project.IsOverleaf ? "ready"
            : project.Pending is not null ? (running.Contains(project) ? (project.ImportComplete ? "updating" : "importing") : "queued")
            : active.Contains(project.ProjectId) ? "checking"
            : !project.ImportComplete ? "waiting" : "ready";
        return new(state.Credential?.Account, state.Credential?.ExpiresAt,
            signIn is null ? null : new(signIn.SignInUrl, signIn.ExpiresAt),
            state.AccountError, Volatile.Read(ref _busy) != 0, options.Dummy,
            new Uri(options.RemoteOrigin, "privacy.html").AbsoluteUri, ResearchEvidence.ConsentVersion,
            options.PollSeconds, state.PendingRevocations.Count,
            projects.Select(project =>
            {
                var (done, total) = DetailPlan.Progress(project);
                return new MonitoredProjectResponse(project.ProjectId, project.Name, project.Enabled,
                    project.ConsentVersion != ResearchEvidence.ConsentVersion, project.Baseline?.Revision ?? project.Snapshot?.Revision,
                    project.Profile?.Description ?? project.Summary, project.Profile?.Contributions.Count ?? project.Facts.Count,
                    project.LastAttemptAt, project.LastSuccessAt, project.ErrorCode, project.Error, project.Explanation, project.UnprocessedFiles,
                    project.Profile, Phase(project), done, total, project.LastSweepAt,
                    project.Enabled && project.IsOverleaf && project.LastSweepAt is { } swept ? swept + options.SweepInterval : null,
                    project.Source, project.Parent);
            }).ToArray(), updates.Take(100).ToArray(), updates.Length,
            (state.Decisions ?? []).Where(decision => decision.AccountId == account)
                .Select(decision => new ProjectDecisionResponse(decision.ProjectId, decision.Included, decision.DecidedAt)).ToArray(),
            account is null ? null : source.Latest,
            researcher is null ? null : new(researcher.Version, researcher.Status, researcher.SearchName, researcher.Candidates,
                researcher.Researcher, researcher.Works.Count, researcher.UpdatedAt,
                researcher.Status == "ready" ? researcher.DueAt : null, researcher.LastError), processing,
            (state.Summaries ?? []).Where(item => item.AccountId == account).Select(item => new ProjectSummaryResponseItem(item.ProjectId,
                item.Status == "queued" && item.ProjectId == _summarizing ? "running" : item.Status, item.Summary, item.MainPath,
                item.UpdatedAt, item.SourceUpdatedAt, item.Error)).ToArray(),
            account is not { } owner ? [] : ThemePlan.Themes(state, owner).Select(theme =>
            {
                var anchor = ThemePlan.AnchorOf(state, theme);
                bool updating = theme.Details is { Count: > 0 } details && theme.SummaryFor != DetailPlan.Fingerprint(details) || theme.MatchPending == true ||
                    theme.Members.Any(member => projects.FirstOrDefault(project => project.ProjectId == member) is { } project && !ThemePlan.Settled(project));
                return new ResearchThemeResponse(theme.Id, theme.Profile, theme.Members, updating, theme.Profile?.Description,
                    anchor?.ProjectId, theme.Anchor is not null && theme.Anchor == anchor?.ProjectId, theme.Reason, theme.Skipped == true,
                    [.. (theme.Details ?? []).Select(detail => new DetailResponse(detail.Id, detail.Text, [.. detail.Citations.Select(citation =>
                        new DetailSourceResponse(citation.Source.Key, citation.Source.Kind, citation.Source.Label, citation.Source.Version, citation.Source.At,
                            citation.Quote, citation.Source.Kind == DetailSource.Overleaf
                                ? projects.FirstOrDefault(project => project.ProjectId == citation.Source.Container)?.Name : citation.Source.Container))]))]);
            }).ToArray(),
            _sync.Status(state), gmail?.Status(state, importer), changes.Take(100).ToArray(), changes.Length,
            [.. (state.PendingFiles ?? []).Where(file => file.AccountId == account && file.TargetCardId is null).OrderByDescending(file => file.RemovedAt)
                .Select(file => new PendingFileResponse(file.ProjectId, file.ProjectName, file.Path, file.FromCardId,
                    state.Themes?.FirstOrDefault(theme => theme.Id == file.FromCardId)?.Profile?.Title, file.RemovedAt))], RecentDays);
    }

    public async Task<AccountLinkResponse> BeginLinkAsync(string callbackUri, CancellationToken ct)
    {
        await _accountGate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Credential is { } credential && credential.ExpiresAt > clock.GetUtcNow())
                throw new ResearchFailure(409, "account_already_linked", "Disconnect the current Meld account before signing in with a different account.");
            string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            string verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            string device = OperatingSystem.IsMacOS() ? "Meld Research on macOS" : "Meld Research on Windows";
            string url = new Uri(options.RemoteOrigin, $"connect?state={nonce}&challenge={ResearchEvidence.Hash(verifier)}" +
                $"&redirect_uri={Uri.EscapeDataString(callbackUri)}&device={Uri.EscapeDataString(device)}").AbsoluteUri;
            var expires = clock.GetUtcNow().AddMinutes(10);
            _signIn = new(nonce, verifier, url, expires);
            await store.ChangeAsync(current => current with { PendingLink = null, AccountError = null }, ct);
            return new(url, expires);
        }
        finally { _accountGate.Release(); }
    }

    public async Task CompleteLinkAsync(string? code, string? state, CancellationToken ct)
    {
        await _accountGate.WaitAsync(ct);
        try
        {
            var pending = _signIn;
            // The state proves this callback answers the sign-in started here, so a crafted link cannot sign this app in.
            if (pending is null || pending.ExpiresAt <= clock.GetUtcNow() || state is not { Length: 64 } ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(state), System.Text.Encoding.ASCII.GetBytes(pending.State)))
                throw new ResearchFailure(400, "invalid_sign_in_callback", "This sign-in was not started here or has expired. Choose Sign in with Google again.");
            _signIn = null;
            if (code is not { Length: 64 } || !code.All(Uri.IsHexDigit))
                throw new ResearchFailure(400, "invalid_sign_in_callback", "The website returned an invalid sign-in. Choose Sign in with Google again.");
            var result = await remote.RedeemSignInAsync(new(code, pending.Verifier), ct);
            if (result.AccessToken is not { Length: 64 } token || !token.All(Uri.IsHexDigit) || result.ExpiresAt <= clock.GetUtcNow() ||
                result.Account is not { } account || account.Id == Guid.Empty)
                throw new ResearchFailure(502, "invalid_account_link", "The remote service returned invalid installation credentials.");
            var verified = await remote.AccountAsync(token, ct);
            if (verified.Id != account.Id) throw new ResearchFailure(502, "account_link_mismatch", "The installation identity did not match the signed-in account.");
            CancelCycle();
            await store.ChangeAsync(current => current with
            {
                Credential = new(verified, token, result.ExpiresAt), PendingLink = null, AccountError = null
            }, ct);
            Wake();
        }
        catch (ResearchFailure failure)
        {
            logger.LogWarning("Account sign-in failed: {Code}. Credentials omitted.", failure.Code);
            await store.ChangeAsync(current => current with { AccountError = failure.Message }, ct);
        }
        finally { _accountGate.Release(); }
    }

    public async Task UnlinkAsync(CancellationToken ct)
    {
        await _accountGate.WaitAsync(ct);
        try
        {
            CancelCycle();
            _signIn = null;
            await store.ChangeAsync(current => current with
            {
                PendingRevocations = current.Credential is { } credential
                    ? [.. current.PendingRevocations, credential] : current.PendingRevocations,
                Credential = null, PendingLink = null, AccountError = null, Sync = null
            }, ct);
            await FlushRevocationsAsync(ct);
        }
        finally { _accountGate.Release(); }
    }

    /// <summary>
    /// Runs a passphrase operation with the worker stopped, so no upload made with an older key can race it, then wakes the
    /// worker to pull and push with the new key.
    /// </summary>
    internal Task<T> WithSyncAsync<T>(Func<ResearchSync, LinkedCredential, Task<T>> action, CancellationToken ct) =>
        WithAccountAsync(credential => action(_sync, credential), ct);

    /// <summary>
    /// Runs an operation on the signed-in account's research (passphrases, data import) with the current cycle stopped, so
    /// no older result can race it, then wakes the worker to continue (and sync) with the new state.
    /// </summary>
    internal async Task<T> WithAccountAsync<T>(Func<LinkedCredential, Task<T>> action, CancellationToken ct)
    {
        await _accountGate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Credential is null || state.Credential.ExpiresAt <= clock.GetUtcNow())
                throw new ResearchFailure(401, "meld_account_required", "Sign in with Google first.");
            CancelCycle();
            var result = await action(state.Credential);
            RequestSync();
            return result;
        }
        finally { _accountGate.Release(); }
    }

    /// <summary>
    /// Leaving Meld Research: stops the worker for good (until <paramref name="erase"/> fails and it is resumed), then
    /// runs the erase with the account gate held so no sign-in or passphrase change interleaves.
    /// </summary>
    internal async Task<T> HaltAsync<T>(Func<LocalResearchState, Task<T>> erase, CancellationToken ct)
    {
        await _accountGate.WaitAsync(ct);
        Volatile.Write(ref _halted, 1);
        try
        {
            CancelCycle();
            _signIn = null;
            return await erase(await store.ReadAsync(ct));
        }
        catch { Volatile.Write(ref _halted, 0); throw; }
        finally { _accountGate.Release(); }
    }

    /// <summary>After erasing in place (a development build), the worker may run again for a new sign-in.</summary>
    internal void Resume() => Volatile.Write(ref _halted, 0);
    private int _halted;

    public Task SetMonitoringAsync(string projectId, SetProjectMonitoringRequest request, CancellationToken ct) =>
        ChangeMonitoringAsync(projectId, request.Enabled, request.ConsentVersion, null, ct);

    public async Task DecideProjectAsync(string projectId, ProjectDecisionRequest request, CancellationToken ct)
    {
        if (request.Included && !ResearchSources.IsGmailWorkstreamId(projectId))
        {
            await ChangeMonitoringAsync(projectId, true, request.ConsentVersion, true, ct);
            return;
        }
        if (!ResearchSources.IsMemberId(projectId)) throw new ResearchFailure(400, "invalid_project_id", "Select a valid project.");
        var account = RequireCredential(await store.ReadAsync(ct));
        CancelProject(projectId);
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account.Account.Id) throw AccountChanged();
            var existing = current.Projects.FirstOrDefault(project => project.AccountId == account.Account.Id && project.ProjectId == projectId);
            // A Gmail workstream is included again simply by lifting its exclusion (its Gmail account's consent covers it).
            if (request.Included && existing is null)
                throw new ResearchFailure(404, "project_not_monitored", "This Gmail workstream no longer exists.");
            return current with
            {
                Projects = existing is not null && existing.Enabled != request.Included
                    ? Replace(current.Projects, existing with { Enabled = request.Included }) : current.Projects,
                Decisions = Decide(current.Decisions, account.Account.Id, projectId, request.Included)
            };
        }, ct);
        // An excluded project leaves its research card; an included workstream joins one again.
        Wake();
    }

    private IReadOnlyList<ProjectDecision> Decide(IReadOnlyList<ProjectDecision>? decisions, Guid account, string projectId, bool included) =>
    [
        .. (decisions ?? []).Where(decision => decision.AccountId != account || decision.ProjectId != projectId),
        new ProjectDecision(account, projectId, included, clock.GetUtcNow())
    ];

    private async Task ChangeMonitoringAsync(string projectId, bool enabled, string? consentVersion, bool? decision, CancellationToken ct)
    {
        if (!ResearchEvidence.IsProjectId(projectId)) throw new ResearchFailure(400, "invalid_project_id", "Select a valid Overleaf project.");
        if (enabled && consentVersion != ResearchEvidence.ConsentVersion)
            throw new ResearchFailure(400, "research_consent_required", "Approve processing of selected project text through Meld and Azure AI Foundry first.");
        var state = await store.ReadAsync(ct);
        var account = RequireCredential(state);
        string name;
        if (enabled)
        {
            // The list the user chose from is already known; read Overleaf again only if it is not (queued, never refused).
            var monitored = state.Projects.FirstOrDefault(project => project.AccountId == account.Account.Id && project.ProjectId == projectId);
            var known = source.Latest?.Projects.FirstOrDefault(project => project.Id == projectId) ??
                (monitored is null ? null : new OverleafProjectResponse(monitored.ProjectId, monitored.Name, null));
            name = (known ?? (await source.ProjectsAsync(ct)).FirstOrDefault(project => project.Id == projectId))?.Name ??
                throw new ResearchFailure(404, "project_not_accessible", "This Overleaf project is not currently accessible.");
        }
        else
        {
            name = state.Projects.FirstOrDefault(project => project.AccountId == account.Account.Id && project.ProjectId == projectId)?.Name ??
                throw new ResearchFailure(404, "project_not_monitored", "This project is not being monitored.");
            CancelProject(projectId);
        }
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account.Account.Id) throw AccountChanged();
            var existing = current.Projects.FirstOrDefault(project => project.AccountId == account.Account.Id && project.ProjectId == projectId);
            var updated = existing is null
                ? new MonitoredProject(account.Account.Id, projectId, name, true, ResearchEvidence.ConsentVersion,
                    null, null, [], [], null, null, null, null, null, 0, 0)
                : existing with { Enabled = enabled, Name = name, ConsentVersion = enabled ? ResearchEvidence.ConsentVersion : existing.ConsentVersion,
                    FailureCount = 0, ErrorCode = null, Error = null, NextAttemptAt = enabled ? null : existing.NextAttemptAt };
            if (enabled) updated = updated with { PrioritizedAt = clock.GetUtcNow() };
            return current with
            {
                Projects = Replace(current.Projects, updated),
                Decisions = decision is { } included ? Decide(current.Decisions, account.Account.Id, projectId, included) : current.Decisions
            };
        }, ct);
        if (enabled)
        {
            Interlocked.Exchange(ref _urgent, 1);
            Wake();
        }
    }

    public async Task ForgetAsync(string projectId, CancellationToken ct)
    {
        var account = RequireCredential(await store.ReadAsync(ct));
        CancelProject(projectId);
        await store.ChangeAsync(current => current with
        {
            Projects = current.Projects.Where(project => project.AccountId != account.Account.Id || project.ProjectId != projectId).ToArray(),
            // Forgetting removes the project from the private profile, so it stays excluded rather than asking again.
            Decisions = current.Credential?.Account.Id == account.Account.Id
                ? Decide(current.Decisions, account.Account.Id, projectId, false) : current.Decisions
        }, ct);
        Wake();
    }

    public async Task RequestSummaryAsync(string projectId, CancellationToken ct)
    {
        if (!ResearchEvidence.IsProjectId(projectId)) throw new ResearchFailure(400, "invalid_project_id", "Select a valid Overleaf project.");
        var account = RequireCredential(await store.ReadAsync(ct));
        var known = source.Latest?.Projects.FirstOrDefault(project => project.Id == projectId) ??
            (await source.ProjectsAsync(ct)).FirstOrDefault(project => project.Id == projectId) ??
            throw new ResearchFailure(404, "project_not_accessible", "This Overleaf project is not currently accessible.");
        var now = clock.GetUtcNow();
        if (known.UpdatedAt is not { } updated || now - updated > SummaryWindow)
            throw new ResearchFailure(400, "project_not_recent", "Summaries are available for projects updated in the last six months.");
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account.Account.Id) throw AccountChanged();
            var existing = current.Summaries?.FirstOrDefault(item => item.AccountId == account.Account.Id && item.ProjectId == projectId);
            var next = (existing ?? new ProjectSummaryState(account.Account.Id, projectId, known.Name, "queued", now)) with
            { Name = known.Name, Status = "queued", RequestedAt = now, Error = null };
            return current with { Summaries = [.. (current.Summaries ?? []).Where(item => item != existing), next] };
        }, ct);
        Interlocked.Exchange(ref _urgent, 1);
        Wake();
    }

    public async Task MoveToThemeAsync(string projectId, string? themeId, CancellationToken ct)
    {
        if (!ResearchEvidence.IsProjectId(projectId) || (themeId is not null && !ResearchThemes.IsThemeId(themeId)))
            throw new ResearchFailure(400, "invalid_theme_move", "Choose a project and a research card.");
        var state = await store.ReadAsync(ct);
        var account = RequireCredential(state).Account.Id;
        var project = state.Projects.FirstOrDefault(item => item.AccountId == account && item.ProjectId == projectId);
        if (project is null || !ThemePlan.Member(state, account, project) || !ThemePlan.Settled(project) || project.Baseline is not { } baseline)
            throw new ResearchFailure(409, "project_not_ready", "This project can be moved once its import has finished.");
        if (themeId is not null && !ThemePlan.Themes(state, account).Any(theme => theme.Id == themeId))
            throw new ResearchFailure(404, "theme_not_found", "That research card no longer exists. Reload and try again.");
        CancelProject(projectId);
        // Its sources leave the old card, and its documents are read again into the new one (OVERLEAF.md §11).
        var texts = await DetailPlan.TextsAsync(store, project, baseline, ct);
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account) throw AccountChanged();
            var item = current.Projects.FirstOrDefault(entry => entry.AccountId == account && entry.ProjectId == projectId) ?? throw AccountChanged();
            var now = clock.GetUtcNow();
            if (ThemePlan.CardOf(current, account, projectId) is { } previous)
            {
                var (details, changes) = DetailPlan.Without(previous.Details ?? [], source => source.Kind == DetailSource.Overleaf && source.Container == projectId);
                current = current with
                {
                    Themes = [.. current.Themes!.Select(theme => theme == previous ? previous with { Details = details } : theme)],
                    Changelog = changes.Count == 0 ? current.Changelog : DetailPlan.Log(current.Changelog, new(Guid.NewGuid(), account, previous.Id, now,
                        ChangelogEntry.User, item.Name + " moved to another project", projectId, [], changes, null, now))
                };
            }
            current = ThemePlan.Place(current, account, projectId, themeId, now, pin: true);
            var moved = item with { Processed = null, Partial = null, Pending = baseline, PendingSince = now };
            moved = moved with { PendingTrees = DetailPlan.Trees(moved, baseline, texts, now, SummaryWindow) };
            return current with { Projects = Replace(current.Projects, moved), ThemesRegroupedFor = null };
        }, ct);
        _themeRetryAt = DateTimeOffset.MinValue;
        Wake();
    }

    public async Task MergeThemeIntoAsync(string themeId, string intoThemeId, CancellationToken ct)
    {
        if (!ResearchThemes.IsThemeId(themeId) || !ResearchThemes.IsThemeId(intoThemeId) || themeId == intoThemeId)
            throw new ResearchFailure(400, "invalid_theme_move", "Choose two different research cards.");
        var state = await store.ReadAsync(ct);
        var credential = RequireCredential(state);
        var themes = ThemePlan.Themes(state, credential.Account.Id);
        if (!themes.Any(theme => theme.Id == themeId) || !themes.Any(theme => theme.Id == intoThemeId))
            throw new ResearchFailure(404, "theme_not_found", "That research card no longer exists. Reload and try again.");
        await CombineAsync(credential, intoThemeId, [themeId], pin: true, null, ct);
        _themeRetryAt = DateTimeOffset.MinValue;
        Wake();
    }

    /// <summary>
    /// Combines cards into one (OVERLEAF.md §11): members, email threads and changelogs move to <paramref name="intoId"/>, and
    /// their details are merged by one Foundry call (keeping the sources of the details each merged one came from). Nothing is
    /// changed if a card's details changed meanwhile.
    /// </summary>
    private async Task CombineAsync(LinkedCredential credential, string intoId, IReadOnlyList<string> otherIds, bool pin, string? reason, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        var cards = ThemePlan.Themes(state, account).Where(theme => theme.Id == intoId || otherIds.Contains(theme.Id))
            .OrderBy(theme => theme.Id == intoId ? 0 : 1).ToArray();
        if (cards.Length < 2 || cards[0].Id != intoId) return;
        var filled = cards.Where(card => card.Details is { Count: > 0 }).ToArray();
        IReadOnlyList<DetailBullet> details = filled.FirstOrDefault()?.Details ?? [];
        if (filled.Length > 1)
        {
            var request = new DetailMergeRequest([.. filled.Select(card => new DetailMergeCard(Trim(card.Profile?.Title ?? "Research project",
                ProfileEvidence.HardMaxTitle), DetailPlan.Inputs(card.Details!)))]);
            var merged = await remote.MergeDetailsAsync(credential.Token, request, ct);
            DetailRules.ValidateMerged(request, merged);
            var byId = filled.SelectMany(card => card.Details!).ToDictionary(detail => detail.Id, StringComparer.Ordinal);
            details = [.. merged.Details.Select(item => new DetailBullet(Guid.NewGuid().ToString("N")[..12], item.Text,
                [.. item.From.SelectMany(id => byId[id].Citations).DistinctBy(citation => (citation.Source.Key, citation.Source.Version, citation.Quote)).Take(8)]))];
        }
        var prints = cards.ToDictionary(card => card.Id, card => DetailPlan.Fingerprint(card.Details), StringComparer.Ordinal);
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account) return current;
            var now = ThemePlan.Themes(current, account).Where(theme => prints.ContainsKey(theme.Id)).ToArray();
            if (now.Length != cards.Length || now.Any(theme => DetailPlan.Fingerprint(theme.Details) != prints[theme.Id])) return current;
            var target = now.Single(theme => theme.Id == intoId);
            var others = now.Where(theme => theme.Id != intoId).ToArray();
            var ids = others.Select(theme => theme.Id).ToHashSet(StringComparer.Ordinal);
            var moved = others.SelectMany(theme => theme.Members).ToArray();
            var combined = target with
            {
                Members = [.. target.Members.Concat(moved).Distinct(StringComparer.Ordinal)],
                Pinned = [.. (target.Pinned ?? []).Concat(pin ? moved : others.SelectMany(theme => theme.Pinned ?? [])).Distinct(StringComparer.Ordinal)],
                Details = details, SummaryFor = null, MatchPending = null, Reason = reason ?? target.Reason
            };
            var at = clock.GetUtcNow();
            var log = (current.Changelog ?? []).Select(entry => ids.Contains(entry.CardId) ? entry with { CardId = intoId } : entry).ToArray();
            return current with
            {
                Themes = [.. current.Themes!.Where(theme => theme.AccountId != account || !ids.Contains(theme.Id))
                    .Select(theme => theme.AccountId == account && theme.Id == intoId ? combined : theme)],
                GmailThreads = current.GmailThreads?.Select(thread => thread.CardId is { } card && ids.Contains(card) ? thread with { CardId = intoId } : thread).ToArray(),
                PendingFiles = current.PendingFiles?.Select(file => file.TargetCardId is { } card && ids.Contains(card) ? file with { TargetCardId = intoId } : file).ToArray(),
                Changelog = DetailPlan.Log(log, new(Guid.NewGuid(), account, intoId, at, ChangelogEntry.Merge,
                    "Combined with " + string.Join(", ", others.Select(theme => theme.Profile?.Title ?? "another project")), null, [], [], null, at))
            };
        }, ct);
        logger.LogInformation("Combined {Count} research card(s) into one.", cards.Length);
    }

    public async Task SetAnchorAsync(string themeId, string projectId, CancellationToken ct)
    {
        if (!ResearchThemes.IsThemeId(themeId) || !ResearchEvidence.IsProjectId(projectId))
            throw new ResearchFailure(400, "invalid_theme_anchor", "Choose a research card and one of its projects.");
        var account = RequireCredential(await store.ReadAsync(ct)).Account.Id;
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account) throw AccountChanged();
            var theme = ThemePlan.Themes(current, account).FirstOrDefault(item => item.Id == themeId)
                ?? throw new ResearchFailure(404, "theme_not_found", "That research card no longer exists. Reload and try again.");
            if (!theme.Members.Contains(projectId)) throw new ResearchFailure(409, "project_not_ready", "Choose a project of this card.");
            // The card's title follows its main paper: the summary is written again.
            return current with { Themes = [.. current.Themes!.Select(item => item == theme ? theme with { Anchor = projectId, SummaryFor = null } : item)] };
        }, ct);
        _themeRetryAt = DateTimeOffset.MinValue;
        Wake();
    }

    public async Task SkipThemeAsync(string themeId, bool skipped, CancellationToken ct)
    {
        if (!ResearchThemes.IsThemeId(themeId)) throw new ResearchFailure(400, "invalid_theme_skip", "Choose a research card.");
        var account = RequireCredential(await store.ReadAsync(ct)).Account.Id;
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account) throw AccountChanged();
            var theme = ThemePlan.Themes(current, account).FirstOrDefault(item => item.Id == themeId)
                ?? throw new ResearchFailure(404, "theme_not_found", "That research card no longer exists. Reload and try again.");
            var changed = skipped ? theme with { Skipped = true, SkipGrouped = null, MatchPending = null } : theme with { Skipped = null, SkipGrouped = null };
            // Back in the profile, the card is compared with the others again.
            return current with
            {
                Themes = [.. current.Themes!.Select(item => item == theme ? changed : item)],
                ThemesRegroupedFor = skipped ? current.ThemesRegroupedFor : null
            };
        }, ct);
        logger.LogInformation(skipped ? "A research card was skipped as not the user's own research." : "A skipped research card was brought back.");
        _themeRetryAt = DateTimeOffset.MinValue;
        if (skipped) Wake(); else RequestSync();
    }

    /// <summary>
    /// The user removes a source from a research project (OVERLEAF.md §10): every citation of it leaves the card's details
    /// (details left without one are removed). An Overleaf file waits in Housekeeping and its project ignores it; an email
    /// thread returns to Housekeeping.
    /// </summary>
    public async Task RemoveSourceAsync(string themeId, string key, CancellationToken ct)
    {
        if (!ResearchThemes.IsThemeId(themeId) || string.IsNullOrWhiteSpace(key)) throw new ResearchFailure(400, "invalid_source", "Choose a source to remove.");
        var account = RequireCredential(await store.ReadAsync(ct)).Account.Id;
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account) throw AccountChanged();
            var theme = ThemePlan.Themes(current, account).FirstOrDefault(item => item.Id == themeId)
                ?? throw new ResearchFailure(404, "theme_not_found", "That research card no longer exists. Reload and try again.");
            var source = (theme.Details ?? []).SelectMany(detail => detail.Citations).Select(citation => citation.Source).FirstOrDefault(item => item.Key == key)
                ?? throw new ResearchFailure(404, "source_not_found", "This source is no longer part of the project.");
            var now = clock.GetUtcNow();
            var (details, changes) = DetailPlan.Without(theme.Details ?? [], item => item.Key == key);
            current = current with
            {
                Themes = [.. current.Themes!.Select(item => item == theme ? theme with { Details = details } : item)],
                Changelog = DetailPlan.Log(current.Changelog, new(Guid.NewGuid(), account, theme.Id, now, ChangelogEntry.User,
                    "Removed: " + source.Label, source.Kind == DetailSource.Overleaf ? source.Container : null, [source.Label], changes, null, now))
            };
            if (source.Kind == DetailSource.Overleaf)
            {
                var project = current.Projects.FirstOrDefault(item => item.AccountId == account && item.ProjectId == source.Container);
                if (project is not null)
                {
                    var processed = (project.Processed ?? new Dictionary<string, string>()).Where(entry => entry.Key != source.Item).ToDictionary(StringComparer.Ordinal);
                    current = current with
                    {
                        Projects = Replace(current.Projects, project with
                        {
                            ExcludedDocs = [.. (project.ExcludedDocs ?? []).Append(source.Item).Distinct(StringComparer.Ordinal)], Processed = processed
                        }),
                        PendingFiles = [.. (current.PendingFiles ?? []).Where(file => file.AccountId != account || file.ProjectId != source.Container || file.Path != source.Item),
                            new PendingFile(account, source.Container, project.Name, source.Item, source.Version, theme.Id, now)]
                    };
                }
            }
            else
                current = current with
                {
                    GmailThreads = current.GmailThreads?.Select(thread => thread.AccountId == account && thread.Id == source.Item &&
                        string.Equals(thread.Address, source.Container, StringComparison.OrdinalIgnoreCase)
                            ? thread with { Status = GmailThreadRecord.Pending, CardId = null, FoldedMessageId = null, FoldedSentAt = null, UpdatedAt = now } : thread).ToArray()
                };
            return current;
        }, ct);
        logger.LogInformation("A source was removed from a research project by the user.");
        Wake();
    }

    /// <summary>A file removed as a source: add it to another research project (folded there once), or skip it.</summary>
    public async Task DecidePendingFileAsync(PendingFileDecisionRequest request, CancellationToken ct)
    {
        if (request is null || !ResearchEvidence.IsProjectId(request.ProjectId) || string.IsNullOrWhiteSpace(request.Path) ||
            request.Action is not ("project" or "dismiss") || request.Action == "project" && !ResearchThemes.IsThemeId(request.CardId))
            throw new ResearchFailure(400, "invalid_file_decision", "Choose a file and what to do with it.");
        var account = RequireCredential(await store.ReadAsync(ct)).Account.Id;
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account) throw AccountChanged();
            var file = current.PendingFiles?.FirstOrDefault(item => item.AccountId == account && item.ProjectId == request.ProjectId && item.Path == request.Path)
                ?? throw new ResearchFailure(404, "file_not_found", "This file is no longer waiting for a decision.");
            if (request.Action == "dismiss") return current with { PendingFiles = [.. current.PendingFiles!.Where(item => item != file)] };
            if (!ThemePlan.Themes(current, account).Any(theme => theme.Id == request.CardId))
                throw new ResearchFailure(404, "theme_not_found", "That research project no longer exists.");
            return current with { PendingFiles = [.. current.PendingFiles!.Select(item => item == file ? file with { TargetCardId = request.CardId } : item)] };
        }, ct);
        Wake();
    }

    /// <summary>A page of a research project's changelog, newest first (entries before <paramref name="before"/>).</summary>
    /// <summary>
    /// A page of a card's changelog, newest first: the entries after entry <paramref name="after"/> (the last one shown), or,
    /// when that entry no longer exists, those older than <paramref name="before"/>.
    /// </summary>
    public async Task<ChangelogPageResponse> ChangelogAsync(string themeId, DateTimeOffset? before, Guid? after, int limit, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        var account = RequireCredential(state).Account.Id;
        int size = Math.Clamp(limit, 1, 100);
        var ordered = (state.Changelog ?? []).Where(entry => entry.AccountId == account && entry.CardId == themeId)
            .OrderByDescending(entry => entry.At).ThenByDescending(entry => entry.RecordedAt).ThenByDescending(entry => entry.Id).ToList();
        int shown = after is { } id ? ordered.FindIndex(entry => entry.Id == id) : -1;
        var rest = shown >= 0 ? ordered.Skip(shown + 1) : ordered.Where(entry => before is null || entry.At < before);
        var entries = rest.Take(size + 1).ToArray();
        var themes = ThemePlan.Themes(state, account);
        return new([.. entries.Take(size).Select(entry => Change(entry, themes, state))], entries.Length > size);
    }

    private static ChangeResponse Change(ChangelogEntry entry, IReadOnlyList<ResearchTheme> themes, LocalResearchState state) =>
        new(entry.Id, entry.CardId, themes.FirstOrDefault(theme => theme.Id == entry.CardId)?.Profile?.Title, entry.At, entry.Kind, entry.Title,
            entry.ProjectId is { } project ? state.Projects.FirstOrDefault(item => item.ProjectId == project)?.Name : null, entry.Documents,
            [.. entry.Changes.Select(change => new DetailChangeResponse(change.Op, change.DetailId, change.Before, change.After))]);

    private static ThemeCandidate Candidate(ResearchTheme theme)
    {
        string description = string.Join(' ', new[] { theme.Profile!.Description }.Concat((theme.Details ?? []).Take(6).Select(detail => detail.Text)));
        return new(theme.Id, Trim(theme.Profile.Title, ProfileEvidence.HardMaxTitle), Trim(description, ProfileEvidence.HardMaxDescription), []);
    }

    private static ProjectProfile CardProfile(ResearchTheme theme) =>
        new(Trim(theme.Profile!.Title, ProfileEvidence.HardMaxTitle), Trim(theme.Profile.Description, ProfileEvidence.HardMaxDescription),
            [.. (theme.Details ?? []).Take(15).Select(detail => Trim(detail.Text, ProfileEvidence.HardMaxContribution))], []);

    /// <summary>Writes a card's title and two-sentence summary from its details (OVERLEAF.md §8).</summary>
    private async Task WriteSummaryAsync(LinkedCredential credential, ResearchTheme card, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var anchor = ThemePlan.AnchorOf(await store.ReadAsync(ct), card);
        var paper = anchor?.Paper is { Abstract: not null } read ? read : null;
        string print = DetailPlan.Fingerprint(card.Details);
        // The title is the main paper's, else the name the user gave the project, else Foundry's working title.
        string? title = paper?.Title ?? card.Name;
        var response = await remote.SummarizeDetailsAsync(credential.Token, new(title is null ? null : Trim(title, Papers.MaxTitle),
            paper?.Abstract, [.. card.Details!.Select(detail => detail.Text)]), ct);
        if (!DetailRules.ValidSummary(response))
            throw new ResearchFailure(502, "invalid_detail_summary", "Foundry returned an unsupported project summary. Nothing was changed.");
        var profile = new ProjectProfile(response.Title, response.Summary, [], []);
        await store.ChangeAsync(current =>
        {
            var theme = current.Themes?.FirstOrDefault(item => item.AccountId == account && item.Id == card.Id);
            // The details changed meanwhile: the summary is written again next.
            if (current.Credential?.Account.Id != account || theme is null || DetailPlan.Fingerprint(theme.Details) != print) return current;
            return current with
            {
                Themes = [.. current.Themes!.Select(item => item == theme
                    ? theme with { Profile = profile, Blurb = profile.Description, BlurbFor = ThemePlan.Fingerprint(profile), SummaryFor = print } : item)]
            };
        }, ct);
        logger.LogInformation("A research project's summary was written from its {Count} details.", card.Details!.Count);
    }

    /// <summary>
    /// Reviews a card's details without new evidence (OVERLEAF.md §8a): one Foundry call merges overlapping details and retires
    /// plans and statuses that newer details overtook. The result applies only if the details did not change meanwhile; the
    /// changes go to the changelog (not the feed). A review the server rejects is not retried until the details change again,
    /// so it never holds up the summary; a server or connection problem is retried like other card work.
    /// </summary>
    private async Task ReviewAsync(LinkedCredential credential, ResearchTheme card, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var anchor = ThemePlan.AnchorOf(await store.ReadAsync(ct), card);
        var paper = anchor?.Paper is { Abstract: not null } read ? read : null;
        string print = DetailPlan.Fingerprint(card.Details);
        IReadOnlyList<DetailBullet>? reviewed = null;
        IReadOnlyList<DetailChange> changes = [];
        try
        {
            var request = new DetailReviewRequest(Trim(card.Profile?.Title ?? card.Name ?? "Research project", ProfileEvidence.HardMaxTitle),
                paper?.Title is { } title ? Trim(title, Papers.MaxTitle) : null, paper?.Abstract, DetailPlan.Inputs(card.Details!));
            var response = await remote.ReviewDetailsAsync(credential.Token, request, ct);
            DetailRules.ValidateReviewed(request, response);
            (reviewed, changes) = DetailPlan.Review(card.Details!, response);
        }
        catch (ResearchFailure failure) when (!Transient(failure.Code))
        {
            logger.LogWarning("A research project's review was not applied: {Code}.", failure.Code);
        }
        var at = clock.GetUtcNow();
        await store.ChangeAsync(current =>
        {
            var theme = current.Themes?.FirstOrDefault(item => item.AccountId == account && item.Id == card.Id);
            // The details changed meanwhile: they are reviewed again later.
            if (current.Credential?.Account.Id != account || theme is null || DetailPlan.Fingerprint(theme.Details) != print) return current;
            var details = changes.Count != 0 ? reviewed! : theme.Details!;
            var updated = theme with { Details = details, ReviewedFor = DetailPlan.Fingerprint(details), ReviewedAt = at };
            return current with
            {
                Themes = [.. current.Themes!.Select(item => item == theme ? updated : item)],
                Changelog = changes.Count == 0 ? current.Changelog : DetailPlan.Log(current.Changelog, new(Guid.NewGuid(), account, card.Id, at,
                    ChangelogEntry.Review, "Review: overlapping details merged, overtaken plans retired", null, [], changes, null, at))
            };
        }, ct);
        logger.LogInformation("A research project's {Count} details were reviewed: {Changes} change(s).", card.Details!.Count, changes.Count);
    }

    /// <summary>
    /// One unit of research card work: drop departed members and their sources, write a summary whose details changed, match a
    /// new card (strictly), compare all cards once more, or group a skipped card with related skipped work (loosely).
    /// </summary>
    private async Task<bool> ThemeStepAsync(LinkedCredential credential, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        if (state.Credential?.Account.Id != account) return false;
        if (ThemePlan.Cleaned(state, account) is not null)
        {
            await store.ChangeAsync(current => current.Credential?.Account.Id == account && ThemePlan.Cleaned(current, account) is { } cleaned ? cleaned : current, ct);
            return true;
        }
        if (clock.GetUtcNow() < _themeRetryAt) return false;
        try
        {
            // A review comes before the summary, so the summary is written once from the reviewed details.
            if (ThemePlan.ReviewDue(state, account, clock.GetUtcNow(), ReviewInterval) is { } stale)
            {
                await ReviewAsync(credential, stale, ct);
                return true;
            }
            if (ThemePlan.SummaryDue(state, account) is { } due)
            {
                await WriteSummaryAsync(credential, due, ct);
                return true;
            }
            if (ThemePlan.MatchDue(state, account) is { } fresh)
            {
                var candidates = ThemePlan.Active(state, account).Where(theme => theme.Id != fresh.Id && theme.Profile is not null && theme.MatchPending != true)
                    .Take(ResearchThemes.MaxCandidates).Select(Candidate).ToArray();
                var match = candidates.Length == 0 ? null : await remote.MatchThemeAsync(credential.Token, new(fresh.Profile!.Title, CardProfile(fresh), candidates), ct);
                if (match?.ThemeId is { } target) await CombineAsync(credential, target, [fresh.Id], pin: false, match.Explanation, ct);
                else await store.ChangeAsync(current => current with
                {
                    Themes = [.. (current.Themes ?? []).Select(theme => theme.AccountId == account && theme.Id == fresh.Id ? theme with { MatchPending = null } : theme)]
                }, ct);
                logger.LogInformation(match?.ThemeId is null ? "A new research project stays on its own card." : "A new research project joined an existing one.");
                return true;
            }
            if (ThemePlan.RegroupDue(state, account))
            {
                // Cards were matched one at a time; compare them all once so cards of one piece of research are combined.
                var cards = ThemePlan.Active(state, account).Take(ResearchThemes.MaxCandidates).Select(Candidate).ToArray();
                var groups = (await remote.RegroupThemesAsync(credential.Token, new(cards), ct)).Groups;
                foreach (var group in groups)
                {
                    var members = ThemePlan.Themes(await store.ReadAsync(ct), account).Where(theme => group.CardIds.Contains(theme.Id)).ToArray();
                    if (members.Length < 2) continue;
                    var target = members.OrderByDescending(theme => theme.Members.Count).ThenBy(theme => theme.CreatedAt).First();
                    // A card with projects the user placed by hand is never combined away automatically.
                    var movable = members.Where(theme => theme != target && theme.Pinned is not { Count: > 0 }).Select(theme => theme.Id).ToArray();
                    if (movable.Length != 0) await CombineAsync(credential, target.Id, movable, pin: false, group.Explanation, ct);
                }
                await store.ChangeAsync(current => current.Credential?.Account.Id == account
                    ? current with { ThemesRegroupedFor = ThemePlan.Signature(current, account) } : current, ct);
                logger.LogInformation("Regrouped research cards: {Count} group(s) found.", groups.Count);
                return true;
            }
            if (ThemePlan.SkipUngrouped(state, account) is { } skippedCard)
            {
                // Skipped work is grouped generously and without asking: mistakes matter less there, and bringing it back undoes it.
                var others = ThemePlan.Themes(state, account).Where(theme => theme.Skipped == true && theme.Id != skippedCard.Id && theme.Profile is not null)
                    .Take(ResearchThemes.MaxCandidates).Select(Candidate).ToArray();
                var match = others.Length == 0 ? null : await remote.MatchThemeAsync(credential.Token,
                    new(skippedCard.Profile!.Title, CardProfile(skippedCard), others, Loose: true), ct);
                if (match?.ThemeId is { } target) await CombineAsync(credential, target, [skippedCard.Id], pin: false, match.Explanation, ct);
                else await store.ChangeAsync(current => current with
                {
                    Themes = [.. (current.Themes ?? []).Select(theme => theme.AccountId == account && theme.Id == skippedCard.Id ? theme with { SkipGrouped = true } : theme)]
                }, ct);
                logger.LogInformation(match?.ThemeId is null ? "A skipped card stays on its own." : "A skipped card was grouped with related skipped work.");
                return true;
            }
        }
        catch (ResearchFailure failure)
        {
            logger.LogWarning("Research card work failed: {Code}. It is retried in {Minutes} minutes.", failure.Code, ThemeBackoff.TotalMinutes);
            _themeRetryAt = clock.GetUtcNow() + ThemeBackoff;
        }
        return false;
    }

    /// <summary>Folds a file the user added to another research project into that project's details, once (OVERLEAF.md §10).</summary>
    private async Task<bool> PendingFileStepAsync(LinkedCredential credential, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        var file = state.PendingFiles?.FirstOrDefault(item => item.AccountId == account && item.TargetCardId is not null);
        if (file is null) return false;
        var card = ThemePlan.Themes(state, account).FirstOrDefault(theme => theme.Id == file.TargetCardId);
        string? text = await store.ReadTextAsync(file.Sha256, ct);
        if (card is not null && text is not null)
        {
            var segment = LatexTree.Pack([(file.Path, text)])[0][0];
            var request = new DetailStepRequest(DetailSource.Overleaf, Trim(card.Profile?.Title ?? "Research project", ProfileEvidence.HardMaxTitle), null, null,
                DetailPlan.Inputs(card.Details ?? []), [file.Path + " [sent]"], [segment]);
            var response = await remote.DetailStepAsync(credential.Token, request, ct);
            DetailRules.ValidateResponse(request, response);
            await store.ChangeAsync(current =>
            {
                var theme = current.Themes?.FirstOrDefault(item => item.AccountId == account && item.Id == card.Id);
                if (current.Credential?.Account.Id != account || theme is null) return current;
                var now = clock.GetUtcNow();
                var (details, changes) = DetailPlan.Apply(theme.Details ?? [], response,
                    quote => new DetailSource(DetailSource.Overleaf, file.ProjectId, quote.Item, file.Sha256, quote.Item, file.RemovedAt));
                return current with
                {
                    Themes = [.. current.Themes!.Select(item => item == theme ? theme with { Details = details } : item)],
                    PendingFiles = [.. current.PendingFiles!.Where(item => item != file)],
                    Changelog = DetailPlan.Log(current.Changelog, new(Guid.NewGuid(), account, theme.Id, now, ChangelogEntry.Overleaf,
                        $"Added {file.Path} from {file.ProjectName}", file.ProjectId, [file.Path], changes, null, now))
                };
            }, ct);
        }
        else await store.ChangeAsync(current => current with { PendingFiles = [.. (current.PendingFiles ?? []).Where(item => item != file)] }, ct);
        return true;
    }

    /// <summary>A manual check: read every included project again now, even if its daily check is not due.</summary>
    public void RequestSync()
    {
        Interlocked.Exchange(ref _force, 1);
        Wake();
    }

    /// <summary>Runs the worker for work that does not need Overleaf read again, such as a Gmail import.</summary>
    internal void RequestWork() => Wake();

    /// <summary>Runs the worker now for whatever is due, such as a newly included project or queued steps.</summary>
    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); }
            catch (SemaphoreFullException) { /* Simultaneous requests coalesce into one cycle. */ }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Account messages describe the last sign-in attempt, and versions before the browser sign-in kept a device-code
        // link here; neither applies after a restart (for example into an update).
        try
        {
            var saved = await store.ReadAsync(stoppingToken);
            if (saved.PendingLink is not null || saved.AccountError is not null)
                await store.ChangeAsync(current => current with { PendingLink = null, AccountError = null }, stoppingToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning("Stale account messages could not be cleared ({Type}).", error.GetType().Name); }
        try { await ResetIfDueAsync(stoppingToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning("The one-time research reset could not be applied ({Type}).", error.GetType().Name); }
        try { PruneTexts(await store.ReadAsync(stoppingToken)); }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning("Unused research texts could not be pruned ({Type}).", error.GetType().Name); }
        while (!stoppingToken.IsCancellationRequested)
        {
            bool force = Interlocked.Exchange(ref _force, 0) != 0;
            bool more = false;
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_cycleGate) _cycle = cycle;
            try { more = await RunCycleAsync(force, cycle.Token); }
            catch (OperationCanceledException) when (cycle.IsCancellationRequested)
            { logger.LogInformation("Research cycle stopped; uncommitted results were discarded."); }
            catch (ResearchFailure failure) when (Transient(failure.Code) &&
                clock.GetUtcNow() - (_serverFailingSince ??= clock.GetUtcNow()) < ServerProblemShownAfter)
            {
                // A short server outage: retried on the next cycle without an account-wide error banner.
                logger.LogInformation("Meld's server is not answering ({Code}); retrying.", failure.Code);
            }
            catch (Exception error) when (error is ResearchFailure or ApiFailure or IOException or CryptographicException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Research worker failed ({Type}); evidence and credentials omitted.", error.GetType().Name);
                try
                {
                    await store.ChangeAsync(current => current with
                    { AccountError = error is ResearchFailure or ApiFailure ? error.Message : "The research worker could not read or persist its local state." }, stoppingToken);
                }
                catch (Exception storageError) when (storageError is ResearchFailure or IOException or CryptographicException)
                { logger.LogError("Research state could not record the failure ({Type}). Existing files were retained.", storageError.GetType().Name); }
            }
            finally
            {
                lock (_cycleGate) _cycle = null;
                Interlocked.Exchange(ref _busy, 0);
            }
            // Queued summary steps continue straight away; otherwise wait for the next poll or a wake-up.
            if (more) continue;
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>
    /// One pass of the worker: sweeps that are due (they read Overleaf through the browser), then up to
    /// <paramref name="maxSteps"/> queued summary steps (they need only the stored texts and Foundry). Returns true while
    /// queued steps remain. All progress is committed per step, so a restart resumes where it stopped.
    /// </summary>
    internal async Task<bool> RunCycleAsync(bool force, CancellationToken ct, int maxSteps = 8)
    {
        Interlocked.Exchange(ref _urgent, 0);
        if (Volatile.Read(ref _halted) != 0) return false;
        await FlushRevocationsAsync(ct);
        var state = await store.ReadAsync(ct);
        if (state.Credential is null) return false;
        var credential = RequireCredential(state);
        // With encrypted sync, nothing is processed until this computer has the key: work done elsewhere is pulled first,
        // so only the changes since then are processed here.
        var mode = await _sync.ModeAsync(credential, state, ct);
        if (mode == SyncMode.Locked) return false;
        if (mode == SyncMode.Off) return await WorkAsync(credential, force, null, ct, maxSteps);
        try { if (await _sync.PullDueAsync(credential, force, ct)) PruneTexts(await store.ReadAsync(ct)); }
        catch (ResearchFailure failure) when (failure.Code == "sync_locked") { return false; }
        try { return await WorkAsync(credential, force, _sync, ct, maxSteps); }
        finally
        {
            if (!ct.IsCancellationRequested)
                try { await _sync.PushAsync(credential, ct); }
                catch (Exception error) when (error is IOException or CryptographicException or System.Text.Json.JsonException or HttpRequestException)
                { logger.LogWarning("Encrypted sync upload failed ({Type}); it is retried on the next check.", error.GetType().Name); }
        }
    }

    private async Task<bool> WorkAsync(LinkedCredential credential, bool force, ResearchSync? synced, CancellationToken ct, int maxSteps)
    {
        var state = await store.ReadAsync(ct);
        if (state.Credential?.Account.Id != credential.Account.Id) return false;
        var now = clock.GetUtcNow();
        var selected = Selected(state, credential.Account.Id);
        var due = QueueOrder(selected.Where(project => force || SweepDue(project, now))).ToArray();
        bool queued = selected.Any(project => project.Pending is not null && Due(project, now));
        var summaries = (state.Summaries ?? []).Where(item => item.AccountId == credential.Account.Id && item.Status == "queued")
            .OrderBy(item => item.RequestedAt).ToArray();
        bool themes = ThemePlan.HasWork(state, credential.Account.Id, now, ReviewInterval) && clock.GetUtcNow() >= _themeRetryAt ||
            state.PendingFiles?.Any(file => file.AccountId == credential.Account.Id && file.TargetCardId is not null) == true;
        bool mail = importer?.HasWork(state, credential.Account.Id, now) == true;
        if (due.Length == 0 && !queued && summaries.Length == 0 && !themes && !mail) return false;
        Interlocked.Exchange(ref _busy, 1);
        var identity = await remote.AccountAsync(credential.Token, ct);
        if (identity.Id != credential.Account.Id) throw AccountChanged();
        var configuration = await remote.StatusAsync(ct);
        if (!configuration.FoundryConfigured)
            throw new ResearchFailure(503, "foundry_not_configured", "Configure the remote Foundry deployments before processing research.");
        _serverFailingSince = null;
        await store.ChangeAsync(current => current.Credential?.Account.Id == credential.Account.Id ? current with { AccountError = null } : current, ct);
        // Summaries the user asked for come first: each is one project read and at most one model call.
        if (summaries.Length != 0) await SummarizeAsync(credential, summaries, ct);
        if (due.Length != 0) await SweepAsync(credential, due, force, ct);
        // A few Gmail threads per cycle, so Overleaf work and card placement continue alongside a long first import.
        bool mailLeft = mail && await importer!.StepAsync(credential, synced, ct);
        for (int step = 0; step < maxSteps; step++)
        {
            // A project the user just included is read in the next cycle, before any further queued steps.
            if (Volatile.Read(ref _urgent) != 0) return true;
            // Card work first, so a project joins its research card as soon as its import finishes.
            if (await ThemeStepAsync(credential, ct)) continue;
            if (await PendingFileStepAsync(credential, ct)) continue;
            if (!await StepRoundAsync(credential, ct)) return mailLeft;
        }
        return true;
    }

    /// <summary>The user's most recent include or resume first, then the longest-waiting work.</summary>
    private static IEnumerable<MonitoredProject> QueueOrder(IEnumerable<MonitoredProject> projects) =>
        projects.OrderByDescending(project => project.PrioritizedAt ?? DateTimeOffset.MinValue).ThenBy(project => project.PendingSince);

    /// <summary>Applies the one-time reset named by <see cref="ResetEpoch"/>; returns whether anything was reset.</summary>
    internal async Task<bool> ResetIfDueAsync(CancellationToken ct)
    {
        if ((await store.ReadAsync(ct)).ResetEpoch >= ResetEpoch) return false;
        await store.ChangeAsync(current => current.ResetEpoch >= ResetEpoch ? current : Fresh(current), ct);
        store.PruneTexts(new HashSet<string>(StringComparer.Ordinal));
        logger.LogInformation("Applied the one-time fresh start {Epoch}: research is rebuilt from Overleaf and email; choices were kept.", ResetEpoch);
        return true;
    }

    /// <summary>
    /// The one-time fresh start of OVERLEAF.md §13: research content (projects' processing state, cards, details, changelog,
    /// threads, texts) is erased; the sign-in, the sync key, Gmail connections, include/exclude choices, skipped work and the
    /// OpenAlex match are kept. Included projects are read again from scratch.
    /// </summary>
    internal static LocalResearchState Fresh(LocalResearchState state)
    {
        var skipped = (state.Themes ?? []).Where(theme => theme.Skipped == true).SelectMany(theme => theme.Members)
            .Concat(state.SkippedProjects ?? []).Where(ResearchEvidence.IsProjectId).Distinct(StringComparer.Ordinal).ToArray();
        var decisions = (state.Decisions ?? []).Where(decision => ResearchEvidence.IsProjectId(decision.ProjectId)).ToArray();
        var projects = decisions.Where(decision => decision.Included).Select(decision =>
            new MonitoredProject(decision.AccountId, decision.ProjectId,
                state.Projects.FirstOrDefault(project => project.AccountId == decision.AccountId && project.ProjectId == decision.ProjectId)?.Name ?? "Overleaf project",
                true, ResearchEvidence.ConsentVersion, null, null, [], [], null, null, null, null, null, 0, 0)).ToArray();
        return state with
        {
            Projects = projects, Decisions = decisions, Summaries = [], Themes = [], ThemesRegroupedFor = null, Changelog = [], PendingFiles = [],
            SkippedProjects = skipped.Length == 0 ? null : skipped,
            // Threads the user skipped stay skipped; everything else is imported again.
            GmailThreads = [.. (state.GmailThreads ?? []).Where(thread => thread.Status == GmailThreadRecord.Dismissed)
                .Select(thread => thread with { StreamId = null, CardId = null, FoldedMessageId = null, FoldedSentAt = null, GroupId = null, GroupPinned = null })],
            HousekeepingGroups = null,
            GmailAccounts = state.GmailAccounts?.Select(account => account with
            {
                LastImportAt = null, ImportVersion = null, LeaseBy = null, LeaseUntil = null, Processed = null
            }).ToArray(),
            GmailVersion = GmailImporter.Version,
            Sync = state.Sync is { } sync ? sync with { ProfileETag = null, ProfileSyncedFor = null } : null,
            ResetEpoch = ResetEpoch
        };
    }

    private bool SweepDue(MonitoredProject project, DateTimeOffset now) => Due(project, now) && project.Pending is null &&
        (project.LastSweepAt is not { } swept || now - swept >= options.SweepInterval || !project.ImportComplete);
    private static bool Due(MonitoredProject project, DateTimeOffset now) => project.NextAttemptAt is not { } next || next <= now;
    /// <summary>Included Overleaf projects with current consent: the projects read from Overleaf and processed file by file.</summary>
    /// <summary>Included Overleaf projects to read and process; those on a skipped card pause until it is brought back.</summary>
    private static MonitoredProject[] Selected(LocalResearchState state, Guid account)
    {
        var skipped = ThemePlan.SkippedMembers(state, account);
        return state.Projects.Where(project => project.AccountId == account && project.Enabled && project.IsOverleaf &&
            project.ConsentVersion == ResearchEvidence.ConsentVersion && !skipped.Contains(project.ProjectId)).ToArray();
    }

    /// <summary>
    /// Reads each requested project and summarizes only its main .tex file. An unchanged main file reuses the stored summary
    /// without a model call. A result is dropped if the request was replaced or reset meanwhile.
    /// </summary>
    private async Task SummarizeAsync(LinkedCredential credential, IReadOnlyList<ProjectSummaryState> queued, CancellationToken ct)
    {
        if (!source.Available)
        {
            foreach (var item in queued)
                await CommitSummaryAsync(credential, item, current => current with
                { Error = "Waiting for the browser connection. Keep your normal browser running." }, ct);
            return;
        }
        var accessible = await source.ProjectsAsync(ct);
        foreach (var item in queued)
        {
            ct.ThrowIfCancellationRequested();
            _summarizing = item.ProjectId;
            try
            {
                var metadata = accessible.FirstOrDefault(project => project.Id == item.ProjectId) ??
                    throw new ResearchFailure(409, "overleaf_project_unavailable", "The project is missing, archived, or no longer accessible.");
                var snapshot = await source.SnapshotAsync(metadata, ct);
                string main = ProjectSummaries.MainDocument(snapshot.Files.Where(file => file.Text is not null).Select(file => (file.Path, file.Text!))) ??
                    throw new ResearchFailure(422, "no_main_document",
                        "No main .tex file with \\documentclass and \\begin{document} was found. Files over 128 KB are not read.");
                var file = snapshot.Files.First(candidate => candidate.Path == main);
                string summary = item.MainSha256 == file.Sha256 && item.Summary is { } kept ? kept
                    : (await remote.SummarizeProjectAsync(credential.Token, new(item.ProjectId, metadata.Name, main, file.Text!), ct)).Summary;
                if (!ProjectSummaries.ValidSummary(summary))
                    throw new ResearchFailure(502, "invalid_project_summary", "Foundry returned an unsupported project summary. Nothing was stored.");
                var now = clock.GetUtcNow();
                await CommitSummaryAsync(credential, item, current => current with
                {
                    Name = metadata.Name, Status = "ready", MainPath = main, MainSha256 = file.Sha256, Summary = summary,
                    UpdatedAt = now, SourceUpdatedAt = metadata.UpdatedAt, Error = null
                }, ct);
            }
            catch (ResearchFailure failure)
            {
                logger.LogWarning("Project summary failed: {Code}. Names and evidence omitted.", failure.Code);
                await CommitSummaryAsync(credential, item, current => current with { Status = "error", Error = failure.Message }, ct);
            }
            finally { _summarizing = null; }
        }
    }

    private async Task CommitSummaryAsync(LinkedCredential credential, ProjectSummaryState requested,
        Func<ProjectSummaryState, ProjectSummaryState> change, CancellationToken ct) =>
        await store.ChangeAsync(current =>
        {
            var latest = current.Summaries?.FirstOrDefault(item => item.AccountId == requested.AccountId && item.ProjectId == requested.ProjectId);
            if (current.Credential?.Account.Id != credential.Account.Id || latest is null || latest.RequestedAt != requested.RequestedAt) return current;
            return current with { Summaries = [.. current.Summaries!.Where(item => item != latest), change(latest)] };
        }, ct);

    /// <summary>
    /// Reads each due project once: unchanged metadata or content costs nothing. Changed content starts an ingest cycle
    /// (OVERLEAF.md §3-§6): the root trees within the window with documents not yet folded at their current version.
    /// </summary>
    private async Task SweepAsync(LinkedCredential credential, IReadOnlyList<MonitoredProject> due, bool force, CancellationToken ct)
    {
        if (!source.Available)
        {
            foreach (var project in due)
                await ChangeProjectAsync(project, item => item with
                {
                    ErrorCode = "browser_unavailable", FailureCount = 0,
                    Error = "The browser connection is unavailable. Keep your normal browser running; Meld checks again automatically."
                }, ct);
            return;
        }
        var accessible = await source.ProjectsAsync(ct);
        foreach (var project in due)
        {
            ct.ThrowIfCancellationRequested();
            await RunForProjectAsync(project, ct, async token =>
            {
                var now = clock.GetUtcNow();
                var metadata = accessible.FirstOrDefault(item => item.Id == project.ProjectId) ??
                    throw new ResearchFailure(409, "overleaf_project_unavailable", "The project is missing, archived, or no longer accessible. Its saved knowledge was retained.");
                if (!force && project.ImportComplete && project.Pending is null && project.Error is null &&
                    project.Baseline?.SourceUpdatedAt is { } known && known == metadata.UpdatedAt)
                {
                    await ChangeProjectAsync(project, item => item with { Name = metadata.Name, LastSweepAt = now }, token);
                    logger.LogInformation("Checked: unchanged on Overleaf since the last check. Nothing was downloaded.");
                    return;
                }
                var snapshot = await source.SnapshotAsync(metadata, token);
                foreach (var file in snapshot.Files.Where(file => file.Text is not null))
                    await store.WriteTextAsync(file.Sha256, file.Text!, token);
                var manifest = new ProjectManifest(snapshot.Revision, snapshot.ObservedAt, snapshot.SourceUpdatedAt,
                    snapshot.Files.Select(file => new ManifestFile(file.Path, file.Sha256, file.Bytes, file.Text is not null)).ToArray());
                if (project.Processed is not { Count: > 0 } && !manifest.Files.Any(file => file.Analyzed))
                    throw new ResearchFailure(422, "no_supported_research_text", "This project has no supported UTF-8 .tex, .md, or .txt sources. Bibliographies, PDFs and images are not analyzed in this version.");
                int unanalyzed = manifest.Files.Count(file => !file.Analyzed);
                if (project.ImportComplete && project.Baseline is { } baseline && SameDocuments(baseline, manifest))
                {
                    // Nothing to process, so Overleaf's history is not read again: the files' known edit times are kept.
                    var paths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
                    var kept = baseline.EditTimes?.Where(entry => paths.Contains(entry.Key)).ToDictionary(StringComparer.Ordinal);
                    manifest = manifest with { EditTimes = kept is { Count: > 0 } ? kept : null };
                    await CommitAsync(credential, project, item => Clean(item) with
                    {
                        Name = metadata.Name, Baseline = manifest, Pending = null, PendingTrees = null, PendingOrder = null, PendingSince = null,
                        LastSweepAt = now, LastSuccessAt = now, UnprocessedFiles = unanalyzed, Explanation = "No supported text changes. No model calls were made."
                    }, token);
                    logger.LogInformation("Checked: no supported text changes. No model calls were made.");
                    return;
                }
                manifest = manifest with { EditTimes = await source.EditTimesAsync(project.ProjectId, token) };
                var texts = await DetailPlan.TextsAsync(store, project, manifest, token);
                var trees = DetailPlan.Trees(project, manifest, texts, now, SummaryWindow);
                await CommitAsync(credential, project, item => Clean(item) with
                {
                    Name = metadata.Name, Pending = manifest, PendingTrees = trees, PendingOrder = null, PendingSince = item.PendingSince ?? now,
                    LastSweepAt = now, UnprocessedFiles = unanalyzed
                }, token);
                logger.LogInformation("Queued {Trees} document tree(s) with {Documents} document(s) to read.", trees.Count, trees.Sum(tree => tree.Send.Count));
            });
        }
    }

    /// <summary>Whether two manifests have the same readable documents at the same versions.</summary>
    private static bool SameDocuments(ProjectManifest a, ProjectManifest b)
    {
        var first = DetailPlan.Hashes(a);
        var second = DetailPlan.Hashes(b);
        return first.Count == second.Count && first.All(entry => second.TryGetValue(entry.Key, out var hash) && hash == entry.Value);
    }

    /// <summary>
    /// Folds the next part of up to <see cref="ResearchOptions.ParallelProjects"/> projects in an ingest cycle at once:
    /// projects are independent, while each project's trees and parts stay in order. Returns false when nothing is queued.
    /// </summary>
    private async Task<bool> StepRoundAsync(LinkedCredential credential, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Credential?.Account.Id != credential.Account.Id) return false;
        var now = clock.GetUtcNow();
        var projects = QueueOrder(Selected(state, credential.Account.Id).Where(item => item.Pending is not null && Due(item, now)))
            .Take(options.ParallelProjects).ToArray();
        if (projects.Length == 0) return false;
        await Task.WhenAll(projects.Select(project => StepAsync(credential, project, ct)));
        return true;
    }

    /// <summary>
    /// One update of a project's research card (OVERLEAF.md §7): the next part of its cycle is sent with the card's details,
    /// and the edits, the documents' folded versions and a changelog entry are committed together. When no part is left the
    /// cycle finishes.
    /// </summary>
    private async Task StepAsync(LinkedCredential credential, MonitoredProject project, CancellationToken ct)
    {
        await RunForProjectAsync(project, ct, async token =>
        {
            var account = credential.Account.Id;
            var pending = project.Pending!;
            var texts = await DetailPlan.TextsAsync(store, project, pending, token);
            var part = DetailPlan.Next(project, texts);
            if (part is null)
            {
                await FinishCycleAsync(credential, project, texts, token);
                return;
            }
            var card = ThemePlan.CardOf(await store.ReadAsync(token), account, project.ProjectId);
            var hashes = DetailPlan.Hashes(pending);
            var sent = part.Documents.Select(document => document.Path).ToHashSet(StringComparer.Ordinal);
            var paper = Papers.Read(texts.Where(entry => entry.Key.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)).Select(entry => (entry.Key, entry.Value)));
            var request = new DetailStepRequest(DetailSource.Overleaf, Trim(card?.Profile?.Title ?? project.Name, ProfileEvidence.HardMaxTitle),
                paper?.Abstract is not null ? paper.Title : null, paper?.Abstract,
                DetailPlan.Inputs(card?.Details ?? [], source => source.Kind == DetailSource.Overleaf && source.Container == project.ProjectId &&
                    sent.Contains(source.Item) && hashes.TryGetValue(source.Item, out var hash) && hash != source.Version),
                part.Outline, part.Documents);
            DetailRules.ValidateRequest(request);
            var response = await remote.DetailStepAsync(credential.Token, request, token);
            DetailRules.ValidateResponse(request, response);
            DateTimeOffset Edited(string path) =>
                pending.EditTimes is { } times && times.TryGetValue(path, out var time) ? time : pending.SourceUpdatedAt ?? pending.ObservedAt;
            int count = 0;
            await store.ChangeAsync(current =>
            {
                var item = current.Projects.FirstOrDefault(entry => entry.AccountId == account && entry.ProjectId == project.ProjectId);
                if (current.Credential?.Account.Id != account || current.Credential.Token != credential.Token || item is not { Enabled: true } ||
                    item.ConsentVersion != ResearchEvidence.ConsentVersion || item.Pending?.Revision != pending.Revision) throw AccountChanged();
                var now = clock.GetUtcNow();
                if (ThemePlan.CardOf(current, account, project.ProjectId) is null) current = ThemePlan.Place(current, account, project.ProjectId, null, now);
                var theme = ThemePlan.CardOf(current, account, project.ProjectId)!;
                // Edits apply to the card as it is now (another project or email may have changed it meanwhile).
                var (details, changes) = DetailPlan.Apply(theme.Details ?? [], response,
                    quote => new DetailSource(DetailSource.Overleaf, project.ProjectId, quote.Item, hashes[quote.Item], quote.Item, Edited(quote.Item)));
                var processed = new Dictionary<string, string>(item.Processed ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                var partial = new Dictionary<string, string>(item.Partial ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                foreach (var path in sent)
                {
                    if (part.Completes.Contains(path))
                    {
                        // A document read in full: citations of its earlier versions go (OVERLEAF.md §7.5).
                        processed[path] = hashes[path];
                        partial.Remove(path);
                        var (kept, dropped) = DetailPlan.Without(details, source => source.Kind == DetailSource.Overleaf &&
                            source.Container == project.ProjectId && source.Item == path && source.Version != hashes[path]);
                        details = kept;
                        changes = [.. changes, .. dropped];
                    }
                    else partial[path] = $"{hashes[path]}:{part.Documents.Where(document => document.Path == path).Max(document => document.FirstLine)}";
                }
                count = changes.Count;
                return current with
                {
                    Themes = [.. current.Themes!.Select(entry => entry == theme ? theme with { Details = details } : entry)],
                    Projects = Replace(current.Projects, item with
                    {
                        Processed = processed, Partial = partial.Count == 0 ? null : partial, LastAttemptAt = now, Explanation = response.Explanation,
                        ErrorCode = null, Error = null, FailureCount = 0, NextAttemptAt = null
                    }),
                    Changelog = DetailPlan.Log(current.Changelog, new(Guid.NewGuid(), account, theme.Id, part.Tree.Date, ChangelogEntry.Overleaf,
                        part.Tree.Root, project.ProjectId, [.. sent], changes, $"{project.ProjectId}|{part.Tree.Root}|{pending.Revision}", now))
                };
            }, token);
            var (done, total) = DetailPlan.Progress(await ProjectAsync(project, token) ?? project);
            logger.LogInformation("{Kind}: {Done} of {Total} document(s) read; {Changes} change(s) to the project's details.",
                project.ImportComplete ? "Update" : "Import", done, total, count);
        });
    }

    private async Task<MonitoredProject?> ProjectAsync(MonitoredProject project, CancellationToken ct) =>
        (await store.ReadAsync(ct)).Projects.FirstOrDefault(item => item.AccountId == project.AccountId && item.ProjectId == project.ProjectId);

    /// <summary>
    /// Ends an ingest cycle (OVERLEAF.md §9): documents that are gone (deleted, too large, excluded) lose their sources, the
    /// observed version becomes the baseline, and the paper's title and abstract are read again. A project gets its card here
    /// if no part was sent.
    /// </summary>
    private async Task FinishCycleAsync(LinkedCredential credential, MonitoredProject project, IReadOnlyDictionary<string, string> texts, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var pending = project.Pending!;
        var paper = Papers.Read(texts.Where(entry => entry.Key.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)).Select(entry => (entry.Key, entry.Value)));
        int removed = 0;
        await store.ChangeAsync(current =>
        {
            var item = current.Projects.FirstOrDefault(entry => entry.AccountId == account && entry.ProjectId == project.ProjectId);
            if (current.Credential?.Account.Id != account || item is not { Enabled: true } || item.Pending?.Revision != pending.Revision) throw AccountChanged();
            var now = clock.GetUtcNow();
            if (ThemePlan.CardOf(current, account, project.ProjectId) is null) current = ThemePlan.Place(current, account, project.ProjectId, null, now);
            var theme = ThemePlan.CardOf(current, account, project.ProjectId)!;
            var gone = (item.Processed ?? new Dictionary<string, string>()).Keys.Where(path => !texts.ContainsKey(path)).ToHashSet(StringComparer.Ordinal);
            removed = gone.Count;
            if (gone.Count != 0)
            {
                var (details, changes) = DetailPlan.Without(theme.Details ?? [], source => source.Kind == DetailSource.Overleaf &&
                    source.Container == project.ProjectId && gone.Contains(source.Item));
                current = current with
                {
                    Themes = [.. current.Themes!.Select(entry => entry == theme ? theme with { Details = details } : entry)],
                    Changelog = DetailPlan.Log(current.Changelog, new(Guid.NewGuid(), account, theme.Id, pending.SourceUpdatedAt ?? now, ChangelogEntry.Removed,
                        gone.Count == 1 ? gone.First() + " removed" : $"{gone.Count} documents removed", project.ProjectId, [.. gone], changes, null, now))
                };
            }
            return current with
            {
                Projects = Replace(current.Projects, item with
                {
                    Baseline = pending, Pending = null, PendingTrees = null, PendingOrder = null, PendingSince = null, Partial = null,
                    Processed = (item.Processed ?? new Dictionary<string, string>()).Where(entry => !gone.Contains(entry.Key)).ToDictionary(StringComparer.Ordinal),
                    ImportComplete = true, LastSuccessAt = now, Paper = paper, PaperFor = Papers.ReadFor(pending.Revision),
                    Explanation = item.ImportComplete ? "Changes read." : "Initial import complete."
                })
            };
        }, ct);
        PruneTexts(await store.ReadAsync(ct));
        logger.LogInformation(removed == 0 ? "Ingest cycle finished." : "Ingest cycle finished; {Removed} removed document(s) lost their sources.", removed);
    }

    /// <summary>Runs one project's work with its own deadline and cancellation, recording failures on that project only.</summary>
    private async Task RunForProjectAsync(MonitoredProject project, CancellationToken ct, Func<CancellationToken, Task> action)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        work.CancelAfter(ProjectDeadline);
        lock (_cycleGate) _active[project.ProjectId] = work;
        // Log entries of this work name the project by its Overleaf id; the Log page shows its name.
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["ProjectId"] = project.ProjectId });
        try { await action(work.Token); }
        catch (Exception error) when (error is ResearchFailure { Status: 429 } or ApiFailure { Status: 429 })
        {
            // Foundry or Meld's service is at capacity: wait briefly and try again, without reporting an error.
            await ChangeProjectAsync(project, item => item with { NextAttemptAt = clock.GetUtcNow() + TimeSpan.FromSeconds(30) }, ct);
        }
        catch (Exception error) when (error is ResearchFailure or ApiFailure or InvalidDataException)
        {
            string code = error is ResearchFailure failure ? failure.Code : error is ApiFailure api ? api.Code : "invalid_research_archive";
            string detail = error is ResearchFailure or ApiFailure ? error.Message : "Overleaf returned an invalid archive. Existing research was retained.";
            if (code == "research_state_changed") return;
            logger.LogWarning("Processing failed ({Code}): {Detail}", code, detail);
            await RecordErrorAsync(project, code, detail, ct);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Paused, excluded or forgotten while processing (nothing to record), or it ran out of time.
            if (await StillSelectedAsync(project, ct))
            {
                logger.LogWarning("Research project processing timed out. Evidence omitted.");
                await RecordErrorAsync(project, "research_timeout", "Processing this project took too long. Existing research was retained; Meld retries automatically.", ct);
            }
        }
        finally { lock (_cycleGate) if (_active.TryGetValue(project.ProjectId, out var current) && current == work) _active.Remove(project.ProjectId); }
    }

    /// <summary>Clears what the earlier whole-snapshot pipeline stored, so the encrypted state stays small.</summary>
    private static MonitoredProject Clean(MonitoredProject project) => project.Snapshot is null && project.Facts.Count == 0 && project.Summary is null
        ? project with { ErrorCode = null, Error = null, FailureCount = 0, NextAttemptAt = null }
        : project with { Snapshot = null, Summary = null, Facts = [], ErrorCode = null, Error = null, FailureCount = 0, NextAttemptAt = null };

    /// <summary>Deletes stored texts that no project's processed or pending files reference.</summary>
    private void PruneTexts(LocalResearchState state)
    {
        var keep = state.Projects.SelectMany(project => (project.Processed?.Values ?? []).Concat(
            new[] { project.Pending, project.Baseline }.Where(manifest => manifest is not null)
                .SelectMany(manifest => manifest!.Files.Where(file => file.Analyzed).Select(file => file.Sha256))))
            .ToHashSet(StringComparer.Ordinal);
        try { store.PruneTexts(keep); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger.LogWarning("Unused research texts could not be pruned ({Type}).", error.GetType().Name); }
    }

    private static ResearchFailure MissingText() => new(500, "research_text_missing",
        "A stored project text is missing. Meld reads the project again on its next check.");

    private async Task<bool> StillSelectedAsync(MonitoredProject project, CancellationToken ct) =>
        (await store.ReadAsync(ct)).Projects.Any(item => item.AccountId == project.AccountId && item.ProjectId == project.ProjectId && item.Enabled);

    private Task<LocalResearchState> CommitAsync(LinkedCredential credential, MonitoredProject previous,
        Func<MonitoredProject, MonitoredProject> change, CancellationToken ct) =>
        store.ChangeAsync(state =>
        {
            var current = state.Projects.FirstOrDefault(item => item.AccountId == previous.AccountId && item.ProjectId == previous.ProjectId);
            if (state.Credential?.Account.Id != credential.Account.Id || state.Credential.Token != credential.Token ||
                current is not { Enabled: true } || current.ConsentVersion != ResearchEvidence.ConsentVersion ||
                current.Pending?.Revision != previous.Pending?.Revision) throw AccountChanged();
            return state with { Projects = Replace(state.Projects, change(current)) };
        }, ct);

    private Task<LocalResearchState> ChangeProjectAsync(MonitoredProject project, Func<MonitoredProject, MonitoredProject> change, CancellationToken ct) =>
        store.ChangeAsync(state =>
        {
            var current = state.Projects.FirstOrDefault(item => item.AccountId == project.AccountId && item.ProjectId == project.ProjectId);
            return current is null ? state : state with { Projects = Replace(state.Projects, change(current)) };
        }, ct);

    private Task<LocalResearchState> RecordErrorAsync(MonitoredProject project, string code, string detail, CancellationToken ct) =>
        ChangeProjectAsync(project, item => item with
        {
            LastAttemptAt = clock.GetUtcNow(), ErrorCode = code, Error = detail, FailureCount = Math.Min(item.FailureCount + 1, 10),
            // 1, 2, 4 ... minutes, at most a day; a manual check retries straight away.
            NextAttemptAt = clock.GetUtcNow() + TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Min(item.FailureCount, 11)), 1440))
        }, ct);

    private async Task FlushRevocationsAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        foreach (var credential in state.PendingRevocations)
        {
            try
            {
                if (credential.ExpiresAt > clock.GetUtcNow()) await remote.RevokeAsync(credential.Token, ct);
                await store.ChangeAsync(current => current with
                {
                    PendingRevocations = current.PendingRevocations.Where(item => item.Token != credential.Token).ToArray(),
                    AccountError = current.AccountError == RevocationPending ? null : current.AccountError
                }, ct);
            }
            catch (ResearchFailure failure)
            {
                logger.LogWarning("Remote credential revocation pending: {Code}. Local processing remains disconnected.", failure.Code);
                await store.ChangeAsync(current => current with
                { AccountError = RevocationPending }, ct);
            }
        }
    }

    private LinkedCredential RequireCredential(LocalResearchState state) =>
        state.Credential is { } credential && credential.ExpiresAt > clock.GetUtcNow() ? credential :
            throw new ResearchFailure(401, "meld_account_required", "Link this installation to an existing Meld account before monitoring projects.");
    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
    private static ResearchFailure AccountChanged() => new(409, "research_state_changed", "The account or monitored project changed. Uncommitted results were discarded.");
    private static IReadOnlyList<MonitoredProject> Replace(IReadOnlyList<MonitoredProject> projects, MonitoredProject updated) =>
        [.. projects.Where(item => item.AccountId != updated.AccountId || item.ProjectId != updated.ProjectId), updated];
    private void CancelCycle() { lock (_cycleGate) _cycle?.Cancel(); }
    /// <summary>Stops in-flight work for one project only; the cycle continues with the other projects.</summary>
    private void CancelProject(string projectId)
    {
        lock (_cycleGate) if (_active.TryGetValue(projectId, out var active)) active.Cancel();
    }
    public override void Dispose() { CancelCycle(); _wake.Dispose(); _accountGate.Dispose(); base.Dispose(); }
}
