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
        return new(state.Credential?.Account, state.Credential?.ExpiresAt,
            signIn is null ? null : new(signIn.SignInUrl, signIn.ExpiresAt),
            state.AccountError, Volatile.Read(ref _busy) != 0, options.Dummy,
            new Uri(options.RemoteOrigin, "privacy.html").AbsoluteUri, ResearchEvidence.ConsentVersion,
            options.PollSeconds, state.PendingRevocations.Count,
            projects.Select(project => new MonitoredProjectResponse(project.ProjectId, project.Name, project.Enabled,
                project.ConsentVersion != ResearchEvidence.ConsentVersion, project.Snapshot?.Revision, project.Summary,
                project.Facts.Count, project.LastAttemptAt, project.LastSuccessAt, project.ErrorCode, project.Error,
                project.Explanation, project.UnprocessedFiles)).ToArray(), updates.Take(100).ToArray(), updates.Length);
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
            RequestSync();
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

    public async Task SetMonitoringAsync(string projectId, SetProjectMonitoringRequest request, CancellationToken ct)
    {
        if (!ResearchEvidence.IsProjectId(projectId)) throw new ResearchFailure(400, "invalid_project_id", "Select a valid Overleaf project.");
        if (request.Enabled && request.ConsentVersion != ResearchEvidence.ConsentVersion)
            throw new ResearchFailure(400, "research_consent_required", "Approve processing of selected project text through Meld and Azure AI Foundry first.");
        var state = await store.ReadAsync(ct);
        var account = RequireCredential(state);
        string name;
        if (request.Enabled)
        {
            var projects = await source.ProjectsAsync(ct);
            name = projects.FirstOrDefault(project => project.Id == projectId)?.Name ??
                throw new ResearchFailure(404, "project_not_accessible", "This Overleaf project is not currently accessible.");
        }
        else
        {
            name = state.Projects.FirstOrDefault(project => project.AccountId == account.Account.Id && project.ProjectId == projectId)?.Name ??
                throw new ResearchFailure(404, "project_not_monitored", "This project is not being monitored.");
            CancelCycle();
        }
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account.Account.Id) throw AccountChanged();
            var existing = current.Projects.FirstOrDefault(project => project.AccountId == account.Account.Id && project.ProjectId == projectId);
            var updated = existing is null
                ? new MonitoredProject(account.Account.Id, projectId, name, true, ResearchEvidence.ConsentVersion,
                    null, null, [], [], null, null, null, null, null, 0, 0)
                : existing with { Enabled = request.Enabled, Name = name, ConsentVersion = request.Enabled ? ResearchEvidence.ConsentVersion : existing.ConsentVersion,
                    FailureCount = 0, ErrorCode = null, Error = null };
            return current with { Projects = Replace(current.Projects, updated) };
        }, ct);
        if (request.Enabled) RequestSync();
    }

    public async Task ForgetAsync(string projectId, CancellationToken ct)
    {
        var account = RequireCredential(await store.ReadAsync(ct));
        CancelCycle();
        await store.ChangeAsync(current => current with
        {
            Projects = current.Projects.Where(project => project.AccountId != account.Account.Id || project.ProjectId != projectId).ToArray()
        }, ct);
    }

    public void RequestSync()
    {
        Interlocked.Exchange(ref _force, 1);
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
        while (!stoppingToken.IsCancellationRequested)
        {
            bool force = Interlocked.Exchange(ref _force, 0) != 0;
            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cycle.CancelAfter(TimeSpan.FromMinutes(10));
            lock (_cycleGate) _cycle = cycle;
            try { await RunCycleAsync(force, cycle.Token); }
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
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task RunCycleAsync(bool force, CancellationToken ct)
    {
        await FlushRevocationsAsync(ct);
        var state = await store.ReadAsync(ct);
        if (state.Credential is null) return;
        var credential = RequireCredential(state);
        var selected = state.Projects.Where(project => project.AccountId == credential.Account.Id && project.Enabled &&
            project.ConsentVersion == ResearchEvidence.ConsentVersion).ToArray();
        if (selected.Length == 0) return;
        if (!source.Available)
        {
            foreach (var project in selected)
                await ChangeProjectAsync(project, item => item with
                {
                    ErrorCode = "browser_unavailable", FailureCount = 0,
                    Error = "The browser connection is unavailable or busy. Keep your normal browser running; monitoring will retry automatically."
                }, ct);
            return;
        }
        Interlocked.Exchange(ref _busy, 1);
        var identity = await remote.AccountAsync(credential.Token, ct);
        if (identity.Id != credential.Account.Id) throw AccountChanged();
        var configuration = await remote.StatusAsync(ct);
        if (!configuration.FoundryConfigured)
            throw new ResearchFailure(503, "foundry_not_configured", "Configure the remote Foundry deployments before processing research.");
        await store.ChangeAsync(current => current.Credential?.Account.Id == credential.Account.Id ? current with { AccountError = null } : current, ct);
        var accessible = await source.ProjectsAsync(ct);
        foreach (var selectedProject in selected)
        {
            ct.ThrowIfCancellationRequested();
            var latest = await store.ReadAsync(ct);
            var project = latest.Projects.FirstOrDefault(item => item.AccountId == credential.Account.Id && item.ProjectId == selectedProject.ProjectId);
            if (latest.Credential?.Account.Id != credential.Account.Id || project is not { Enabled: true } ||
                project.ConsentVersion != ResearchEvidence.ConsentVersion) continue;
            var now = clock.GetUtcNow();
            if (!force && project.LastAttemptAt is { } attempted &&
                now - attempted < TimeSpan.FromSeconds(options.PollSeconds * Math.Pow(2, Math.Min(project.FailureCount, 4)))) continue;
            try
            {
                var metadata = accessible.FirstOrDefault(item => item.Id == project.ProjectId) ??
                    throw new ResearchFailure(409, "overleaf_project_unavailable", "The project is missing, archived, or no longer accessible. Its saved knowledge was retained.");
                if (!force && project.Error is null && project.Snapshot is { SourceUpdatedAt: not null } known &&
                    metadata.UpdatedAt == known.SourceUpdatedAt && now - known.ObservedAt < TimeSpan.FromHours(1))
                {
                    await ChangeProjectAsync(project, item => item with { Name = metadata.Name, LastAttemptAt = now }, ct);
                    continue;
                }
                await ChangeProjectAsync(project, item => item with { LastAttemptAt = now }, ct);
                var snapshot = await source.SnapshotAsync(metadata, ct);
                if (project.Snapshot is null && snapshot.Files.All(file => file.Text is null))
                    throw new ResearchFailure(422, "no_supported_research_text", "This project has no supported UTF-8 .tex, .bib, .md, or .txt sources. PDFs and images are not analyzed in this version.");
                if (project.Snapshot?.Revision == snapshot.Revision || !ResearchEvidence.HasTextChanges(project.Snapshot, snapshot))
                {
                    await CommitAsync(credential, project, item => item with
                    {
                        Name = metadata.Name, Snapshot = snapshot, LastSuccessAt = now, Error = null, ErrorCode = null, FailureCount = 0,
                        Explanation = "No supported text changes. No model calls were made.",
                        UnprocessedFiles = snapshot.Files.Count(file => file.Text is null)
                    }, ct);
                    continue;
                }
                IReadOnlyList<ResearchFactContext> candidates = [];
                if (project.Snapshot is { } before && project.Facts.Count != 0)
                {
                    var paths = ResearchEvidence.ChangedPaths(before, snapshot).ToHashSet(StringComparer.Ordinal);
                    string query = string.Join("\n", snapshot.Files.Concat(before.Files)
                        .Where(file => file.Text is not null && paths.Contains(file.Path)).Select(file => file.Path + "\n" + file.Text));
                    // This bounded excerpt ranks candidates only; the model still receives the complete bounded snapshots.
                    query = query[..Math.Min(query.Length, 12000)];
                    var vector = await remote.EmbedAsync(credential.Token, new([query]), ct);
                    candidates = ResearchReducer.Candidates(before, snapshot, project.Facts, vector);
                }
                var request = new AnalyzeResearchRequest(project.Snapshot, snapshot, project.Summary, candidates);
                var result = await remote.AnalyzeAsync(credential.Token, request, ct);
                var reduction = ResearchReducer.Apply(request, result, project.Facts);
                await CommitAsync(credential, project, item => item with
                {
                    Name = metadata.Name, Snapshot = snapshot, Summary = result.Analysis.ProjectSummary,
                    Facts = reduction.Facts, Updates = [.. item.Updates, .. reduction.Updates], LastSuccessAt = now,
                    ErrorCode = null, Error = null, FailureCount = 0, Explanation = result.Analysis.Explanation,
                    UnprocessedFiles = snapshot.Files.Count(file => file.Text is null)
                }, ct);
                logger.LogInformation("Research cycle committed {Count} update(s). Project names and evidence omitted.", reduction.Updates.Count);
            }
            catch (Exception error) when (error is ResearchFailure or ApiFailure or InvalidDataException)
            {
                string code = error is ResearchFailure failure ? failure.Code : error is ApiFailure api ? api.Code : "invalid_research_archive";
                string detail = error is ResearchFailure or ApiFailure ? error.Message : "Overleaf returned an invalid archive. Existing research was retained.";
                logger.LogWarning("Research project processing failed: {Code}. Evidence omitted.", code);
                await RecordErrorAsync(project, code, detail, ct);
            }
        }
    }

    private Task<LocalResearchState> CommitAsync(LinkedCredential credential, MonitoredProject previous,
        Func<MonitoredProject, MonitoredProject> change, CancellationToken ct) =>
        store.ChangeAsync(state =>
        {
            var current = state.Projects.FirstOrDefault(item => item.AccountId == previous.AccountId && item.ProjectId == previous.ProjectId);
            if (state.Credential?.Account.Id != credential.Account.Id || state.Credential.Token != credential.Token ||
                current is not { Enabled: true } || current.ConsentVersion != ResearchEvidence.ConsentVersion ||
                current.Snapshot?.Revision != previous.Snapshot?.Revision) throw AccountChanged();
            return state with { Projects = Replace(state.Projects, change(current)) };
        }, ct);

    private Task<LocalResearchState> ChangeProjectAsync(MonitoredProject project, Func<MonitoredProject, MonitoredProject> change, CancellationToken ct) =>
        store.ChangeAsync(state =>
        {
            var current = state.Projects.FirstOrDefault(item => item.AccountId == project.AccountId && item.ProjectId == project.ProjectId);
            return current is null ? state : state with { Projects = Replace(state.Projects, change(current)) };
        }, ct);

    private Task<LocalResearchState> RecordErrorAsync(MonitoredProject project, string code, string detail, CancellationToken ct) =>
        ChangeProjectAsync(project, item => item with { LastAttemptAt = clock.GetUtcNow(), ErrorCode = code, Error = detail,
            FailureCount = Math.Min(item.FailureCount + 1, 10) }, ct);

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
    public override void Dispose() { CancelCycle(); _wake.Dispose(); _accountGate.Dispose(); base.Dispose(); }
}
