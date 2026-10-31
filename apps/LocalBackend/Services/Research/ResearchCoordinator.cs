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
    void RequestSync();
    /// <summary>True while a research cycle is reading sources or calling the remote models.</summary>
    bool Busy { get; }
}

internal sealed class ResearchCoordinator(IResearchStore store, IResearchRemote remote, IOverleafEvidenceSource source,
    ResearchOptions options, ILogger<ResearchCoordinator> logger, TimeProvider clock)
    : BackgroundService, IResearchCoordinator
{
    private sealed record PendingSignIn(string State, string Verifier, string SignInUrl, DateTimeOffset ExpiresAt);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private readonly object _cycleGate = new();
    private CancellationTokenSource? _cycle;
    // The project the cycle is processing now, so pausing or excluding another project does not interrupt it.
    private (string ProjectId, CancellationTokenSource Token)? _active;
    private static readonly TimeSpan ProjectDeadline = TimeSpan.FromMinutes(10);
    // Held in memory only: a sign-in is a short browser round trip; after a restart the user simply signs in again.
    private PendingSignIn? _signIn;
    private int _busy;
    private int _force;
    private const string RevocationPending = "Local access has stopped. Remote credential revocation is pending and will retry automatically.";
    public bool Busy => Volatile.Read(ref _busy) != 0;

    public async Task<ResearchStatusResponse> StatusAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        Guid? account = state.Credential?.Account.Id;
        var projects = state.Projects.Where(project => project.AccountId == account).ToArray();
        var updates = projects.SelectMany(project => project.Updates).OrderByDescending(update => update.ObservedAt).ToArray();
        var signIn = _signIn is { } pending && pending.ExpiresAt > clock.GetUtcNow() && state.Credential is null ? pending : null;
        var researcher = state.Researchers?.FirstOrDefault(item => item.AccountId == account);
        string? active;
        lock (_cycleGate) active = _active?.ProjectId;
        bool busy = Volatile.Read(ref _busy) != 0;
        var queue = projects.Where(project => project.Enabled && project.Pending is not null).OrderBy(project => project.PendingSince).ToArray();
        var running = queue.FirstOrDefault(project => project.ProjectId == active) ?? (busy ? queue.FirstOrDefault() : null);
        ProcessingStatusResponse? processing = null;
        if (running is not null)
        {
            var (done, total) = ProcessingPlan.Progress(running);
            processing = new(running.ProjectId, running.Name, running.ImportComplete ? "update" : "import", done, total, queue.Length - 1);
        }
        string Phase(MonitoredProject project) =>
            project.ErrorCode is not null ? "error"
            : project.Pending is not null ? (project == running ? (project.ImportComplete ? "updating" : "importing") : "queued")
            : project.ProjectId == active ? "checking"
            : !project.ImportComplete ? "waiting" : "ready";
        return new(state.Credential?.Account, state.Credential?.ExpiresAt,
            signIn is null ? null : new(signIn.SignInUrl, signIn.ExpiresAt),
            state.AccountError, Volatile.Read(ref _busy) != 0, options.Dummy,
            new Uri(options.RemoteOrigin, "privacy.html").AbsoluteUri, ResearchEvidence.ConsentVersion,
            options.PollSeconds, state.PendingRevocations.Count,
            projects.Select(project =>
            {
                var (done, total) = ProcessingPlan.Progress(project);
                return new MonitoredProjectResponse(project.ProjectId, project.Name, project.Enabled,
                    project.ConsentVersion != ResearchEvidence.ConsentVersion, project.Baseline?.Revision ?? project.Snapshot?.Revision,
                    project.Profile?.Description ?? project.Summary, project.Profile?.Contributions.Count ?? project.Facts.Count,
                    project.LastAttemptAt, project.LastSuccessAt, project.ErrorCode, project.Error, project.Explanation, project.UnprocessedFiles,
                    project.Profile, Phase(project), done, total, project.LastSweepAt,
                    project.Enabled && project.LastSweepAt is { } swept ? swept + options.SweepInterval : null);
            }).ToArray(), updates.Take(100).ToArray(), updates.Length,
            (state.Decisions ?? []).Where(decision => decision.AccountId == account)
                .Select(decision => new ProjectDecisionResponse(decision.ProjectId, decision.Included, decision.DecidedAt)).ToArray(),
            account is null ? null : source.Latest,
            researcher is null ? null : new(researcher.Version, researcher.Status, researcher.SearchName, researcher.Candidates,
                researcher.Researcher, researcher.Works.Count, researcher.UpdatedAt,
                researcher.Status == "ready" ? researcher.DueAt : null, researcher.LastError), processing);
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
                Credential = null, PendingLink = null, AccountError = null
            }, ct);
            await FlushRevocationsAsync(ct);
        }
        finally { _accountGate.Release(); }
    }

    public Task SetMonitoringAsync(string projectId, SetProjectMonitoringRequest request, CancellationToken ct) =>
        ChangeMonitoringAsync(projectId, request.Enabled, request.ConsentVersion, null, ct);

    public async Task DecideProjectAsync(string projectId, ProjectDecisionRequest request, CancellationToken ct)
    {
        if (request.Included)
        {
            await ChangeMonitoringAsync(projectId, true, request.ConsentVersion, true, ct);
            return;
        }
        if (!ResearchEvidence.IsProjectId(projectId)) throw new ResearchFailure(400, "invalid_project_id", "Select a valid Overleaf project.");
        var account = RequireCredential(await store.ReadAsync(ct));
        CancelProject(projectId);
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account.Account.Id) throw AccountChanged();
            var existing = current.Projects.FirstOrDefault(project => project.AccountId == account.Account.Id && project.ProjectId == projectId);
            return current with
            {
                Projects = existing is { Enabled: true } ? Replace(current.Projects, existing with { Enabled = false }) : current.Projects,
                Decisions = Decide(current.Decisions, account.Account.Id, projectId, false)
            };
        }, ct);
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
                    FailureCount = 0, ErrorCode = null, Error = null };
            return current with
            {
                Projects = Replace(current.Projects, updated),
                Decisions = decision is { } included ? Decide(current.Decisions, account.Account.Id, projectId, included) : current.Decisions
            };
        }, ct);
        if (enabled) Wake();
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
    }

    /// <summary>A manual check: read every included project again now, even if its daily check is not due.</summary>
    public void RequestSync()
    {
        Interlocked.Exchange(ref _force, 1);
        Wake();
    }

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
        await FlushRevocationsAsync(ct);
        var state = await store.ReadAsync(ct);
        if (state.Credential is null) return false;
        var credential = RequireCredential(state);
        var now = clock.GetUtcNow();
        var selected = Selected(state, credential.Account.Id);
        var due = selected.Where(project => force || SweepDue(project, now)).ToArray();
        bool queued = selected.Any(project => project.Pending is not null && Due(project, now));
        if (due.Length == 0 && !queued) return false;
        Interlocked.Exchange(ref _busy, 1);
        var identity = await remote.AccountAsync(credential.Token, ct);
        if (identity.Id != credential.Account.Id) throw AccountChanged();
        var configuration = await remote.StatusAsync(ct);
        if (!configuration.FoundryConfigured)
            throw new ResearchFailure(503, "foundry_not_configured", "Configure the remote Foundry deployments before processing research.");
        await store.ChangeAsync(current => current.Credential?.Account.Id == credential.Account.Id ? current with { AccountError = null } : current, ct);
        if (due.Length != 0) await SweepAsync(credential, due, force, ct);
        for (int step = 0; step < maxSteps; step++)
            if (!await StepAsync(credential, ct)) return false;
        return true;
    }

    private bool SweepDue(MonitoredProject project, DateTimeOffset now) => Due(project, now) && project.Pending is null &&
        (project.LastSweepAt is not { } swept || now - swept >= options.SweepInterval || !project.ImportComplete);
    private static bool Due(MonitoredProject project, DateTimeOffset now) => project.NextAttemptAt is not { } next || next <= now;
    private static MonitoredProject[] Selected(LocalResearchState state, Guid account) =>
        state.Projects.Where(project => project.AccountId == account && project.Enabled &&
            project.ConsentVersion == ResearchEvidence.ConsentVersion).ToArray();

    /// <summary>Reads each due project once: unchanged metadata or content costs nothing; changed files are queued.</summary>
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
                    return;
                }
                var snapshot = await source.SnapshotAsync(metadata, token);
                foreach (var file in snapshot.Files.Where(file => file.Text is not null))
                    await store.WriteTextAsync(file.Sha256, file.Text!, token);
                var manifest = new ProjectManifest(snapshot.Revision, snapshot.ObservedAt, snapshot.SourceUpdatedAt,
                    snapshot.Files.Select(file => new ManifestFile(file.Path, file.Sha256, file.Bytes, file.Text is not null)).ToArray());
                if (project.Profile is null && !manifest.Files.Any(file => file.Analyzed))
                    throw new ResearchFailure(422, "no_supported_research_text", "This project has no supported UTF-8 .tex, .md, or .txt sources. Bibliographies, PDFs and images are not analyzed in this version.");
                int unanalyzed = manifest.Files.Count(file => !file.Analyzed);
                var work = ProcessingPlan.Work(project.Processed, manifest);
                if (work.Count == 0)
                {
                    await CommitAsync(credential, project, item => Clean(item) with
                    {
                        Name = metadata.Name, Baseline = manifest, Pending = null, PendingOrder = null, PendingSince = null,
                        ImportComplete = item.Profile is not null, LastSweepAt = now, LastSuccessAt = now, UnprocessedFiles = unanalyzed,
                        Explanation = "No supported text changes. No model calls were made."
                    }, token);
                    return;
                }
                manifest = manifest with { EditTimes = await source.EditTimesAsync(project.ProjectId, token) };
                var order = ProcessingPlan.Order(work, manifest);
                await CommitAsync(credential, project, item => Clean(item) with
                {
                    Name = metadata.Name, Pending = manifest, PendingOrder = order, PendingSince = item.PendingSince ?? now,
                    LastSweepAt = now, UnprocessedFiles = unanalyzed
                }, token);
                logger.LogInformation("Queued {Count} changed file(s) for summary processing. Names and evidence omitted.", order.Count);
            });
        }
    }

    /// <summary>Folds the next queued file of the earliest queued project into its summary. Returns false when nothing is queued.</summary>
    private async Task<bool> StepAsync(LinkedCredential credential, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Credential?.Account.Id != credential.Account.Id) return false;
        var now = clock.GetUtcNow();
        var project = Selected(state, credential.Account.Id).Where(item => item.Pending is not null && Due(item, now))
            .OrderBy(item => item.PendingSince).FirstOrDefault();
        if (project is null) return false;
        await RunForProjectAsync(project, ct, async token =>
        {
            var pending = project.Pending!;
            string? path = ProcessingPlan.Next(project);
            if (path is null)
            {
                bool firstImport = !project.ImportComplete;
                var finished = await CommitAsync(credential, project, item => item with
                {
                    Baseline = pending, Pending = null, PendingOrder = null, PendingSince = null, ImportComplete = item.Profile is not null,
                    LastSuccessAt = clock.GetUtcNow(),
                    Explanation = firstImport ? "Initial import complete: the summary now reflects every supported file." : item.Explanation
                }, token);
                PruneTexts(finished);
                return;
            }
            var processed = project.Processed ?? new Dictionary<string, string>();
            var file = pending.Files.FirstOrDefault(item => item.Path == path && item.Analyzed);
            processed.TryGetValue(path, out var before);
            bool initial = !project.ImportComplete || project.Profile is null;
            // Identical copies (for example earlier submission folders) and deletions during the first import need no model call.
            if ((initial && file is null) || (file is not null && before is null && processed.Values.Contains(file.Sha256)))
            {
                await CommitStepAsync(credential, project, path, before, file?.Sha256, null, [], token);
                return;
            }
            string? current = file is null ? null : await store.ReadTextAsync(file.Sha256, token) ?? throw MissingText();
            string? previous = initial || before is null ? null : await store.ReadTextAsync(before, token) ?? throw MissingText();
            var request = new ProfileStepRequest(project.ProjectId, project.Name, initial, project.Profile, new(path, previous, current));
            var response = await remote.ProfileStepAsync(credential.Token, request, token);
            ProfileEvidence.ValidateResponse(request, response);
            var observed = clock.GetUtcNow();
            DateTimeOffset? edited = pending.EditTimes is { } times && times.TryGetValue(path, out var time) ? time : pending.SourceUpdatedAt;
            var updates = response.Updates.Select(update => new ResearchUpdate(Guid.NewGuid(), project.ProjectId, project.Name,
                file is null ? ResearchUpdateKind.Removal : ResearchUpdateKind.Change, update.Title, update.Summary, observed, edited,
                project.Baseline?.Revision, pending.Revision, Guid.NewGuid(), update.Evidence, null)).ToArray();
            await CommitStepAsync(credential, project, path, before, file?.Sha256, response, updates, token);
            if (updates.Length != 0)
                logger.LogInformation("Summary step committed {Count} update(s). Names and evidence omitted.", updates.Length);
        });
        return true;
    }

    /// <summary>Runs one project's work with its own deadline and cancellation, recording failures on that project only.</summary>
    private async Task RunForProjectAsync(MonitoredProject project, CancellationToken ct, Func<CancellationToken, Task> action)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        work.CancelAfter(ProjectDeadline);
        lock (_cycleGate) _active = (project.ProjectId, work);
        try { await action(work.Token); }
        catch (Exception error) when (error is ResearchFailure or ApiFailure or InvalidDataException)
        {
            string code = error is ResearchFailure failure ? failure.Code : error is ApiFailure api ? api.Code : "invalid_research_archive";
            string detail = error is ResearchFailure or ApiFailure ? error.Message : "Overleaf returned an invalid archive. Existing research was retained.";
            if (code == "research_state_changed") return;
            logger.LogWarning("Research project processing failed: {Code}. Evidence omitted.", code);
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
        finally { lock (_cycleGate) if (_active?.Token == work) _active = null; }
    }

    /// <summary>Clears what the earlier whole-snapshot pipeline stored, so the encrypted state stays small.</summary>
    private static MonitoredProject Clean(MonitoredProject project) => project.Snapshot is null && project.Facts.Count == 0 && project.Summary is null
        ? project with { ErrorCode = null, Error = null, FailureCount = 0, NextAttemptAt = null }
        : project with { Snapshot = null, Summary = null, Facts = [], ErrorCode = null, Error = null, FailureCount = 0, NextAttemptAt = null };

    private Task<LocalResearchState> CommitStepAsync(LinkedCredential credential, MonitoredProject project, string path, string? before,
        string? after, ProfileStepResponse? response, IReadOnlyList<ResearchUpdate> updates, CancellationToken ct) =>
        CommitAsync(credential, project, item =>
        {
            var processed = new Dictionary<string, string>(item.Processed ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            // Another step or a pause changed this file's state meanwhile: discard this result.
            if ((processed.TryGetValue(path, out var stored) ? stored : null) != before) throw AccountChanged();
            if (after is null) processed.Remove(path); else processed[path] = after;
            return item with
            {
                Processed = processed, Profile = response?.Profile ?? item.Profile, Updates = [.. item.Updates, .. updates],
                Explanation = response?.Explanation ?? item.Explanation, LastAttemptAt = clock.GetUtcNow(),
                ErrorCode = null, Error = null, FailureCount = 0, NextAttemptAt = null
            };
        }, ct);

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
    private static ResearchFailure AccountChanged() => new(409, "research_state_changed", "The account or monitored project changed. Uncommitted results were discarded.");
    private static IReadOnlyList<MonitoredProject> Replace(IReadOnlyList<MonitoredProject> projects, MonitoredProject updated) =>
        [.. projects.Where(item => item.AccountId != updated.AccountId || item.ProjectId != updated.ProjectId), updated];
    private void CancelCycle() { lock (_cycleGate) _cycle?.Cancel(); }
    /// <summary>Stops in-flight work for one project only; the cycle continues with the other projects.</summary>
    private void CancelProject(string projectId)
    {
        lock (_cycleGate) if (_active is { } active && active.ProjectId == projectId) active.Token.Cancel();
    }
    public override void Dispose() { CancelCycle(); _wake.Dispose(); _accountGate.Dispose(); base.Dispose(); }
}
