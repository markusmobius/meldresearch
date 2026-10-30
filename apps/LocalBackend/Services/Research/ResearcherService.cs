using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal interface IResearcherService
{
    Task DecideAsync(ResearcherDecisionRequest request, CancellationToken ct);
    Task<PublicationsResponse> PublicationsAsync(CancellationToken ct);
}

/// <summary>
/// Matches the signed-in account to an OpenAlex author (ported from Project Meld). A search starts only on the user's
/// request; the remote service proposes candidates; only the user confirms one. The confirmed author's public works are
/// stored in the encrypted local research state and refreshed weekly. A result is discarded if the account or the
/// researcher state changed while it was being fetched.
/// </summary>
internal sealed class ResearcherService(IResearchStore store, IResearchRemote remote, ILogger<ResearcherService> logger,
    TimeProvider clock) : BackgroundService, IResearcherService
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim _wake = new(0, 1);

    public async Task DecideAsync(ResearcherDecisionRequest request, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        var account = state.Credential?.Account.Id ??
            throw new ResearchFailure(409, "meld_account_required", "Sign in to your Meld account first.");
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account) throw new ResearchFailure(409, "research_state_changed", "The account changed. Reload and try again.");
            var existing = current.Researchers?.FirstOrDefault(item => item.AccountId == account);
            if (request.BaseVersion is { } version && existing is not null && version != existing.Version)
                throw new ResearchFailure(409, "researcher_changed", "Your researcher choice changed. Reload before choosing again.");
            var now = clock.GetUtcNow();
            ResearcherState next;
            switch (request.Action)
            {
                case "search":
                    string name = request.SearchName?.Trim() ?? "";
                    if (name.Length is < 2 or > 160) throw new ResearchFailure(400, "invalid_researcher_name", "Enter a researcher name of 2-160 characters.");
                    next = new(account, (existing?.Version ?? 0) + 1, "searching", name, [], null, [], null, now, null);
                    break;
                case "confirm":
                    var candidate = existing?.Candidates.FirstOrDefault(item => item.Id == request.AuthorId);
                    if (existing is not { Status: "choice" } || candidate is null)
                        throw new ResearchFailure(400, "invalid_researcher_choice", "Choose one of the offered researchers.");
                    next = existing with { Version = existing.Version + 1, Status = "importing", Candidates = [], Researcher = candidate,
                        Works = [], UpdatedAt = null, DueAt = now, LastError = null };
                    break;
                case "none":
                    if (existing is null)
                    {
                        next = new(account, 1, "rejected", current.Credential.Account.Name, [], null, [], null, null, null);
                        break;
                    }
                    if (existing is not { Status: "choice" or "unmatched" or "searching" or "rejected" })
                        throw new ResearchFailure(400, "no_researcher_choice", "There is no pending researcher choice.");
                    next = existing with { Version = existing.Version + 1, Status = "rejected", Candidates = [], Researcher = null,
                        Works = [], UpdatedAt = null, DueAt = null, LastError = null };
                    break;
                case "retry":
                    if (existing is not { Status: "searching" or "importing" or "ready" })
                        throw new ResearchFailure(400, "nothing_to_retry", "There is no OpenAlex search or import to retry.");
                    next = existing with { Version = existing.Version + 1, DueAt = now, LastError = null, Failures = 0 };
                    break;
                default:
                    throw new ResearchFailure(400, "invalid_researcher_action", "Choose search, confirm, none or retry.");
            }
            return current with { Researchers = Replace(current.Researchers, next) };
        }, ct);
        Wake();
    }

    public async Task<PublicationsResponse> PublicationsAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        var researcher = state.Researchers?.FirstOrDefault(item => item.AccountId == state.Credential?.Account.Id);
        return researcher?.Researcher is null ? new(null, [], null) : new(researcher.Researcher, researcher.Works, researcher.UpdatedAt);
    }

    private void Wake()
    {
        if (_wake.CurrentCount != 0) return;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* Already signalled. */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is ResearchFailure or IOException or System.Security.Cryptography.CryptographicException)
            { logger.LogWarning("The researcher worker could not read its local state ({Type}).", error.GetType().Name); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Processes the signed-in account's due search or import, if any.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Credential is not { } credential || credential.ExpiresAt <= clock.GetUtcNow()) return;
        var due = state.Researchers?.FirstOrDefault(item => item.AccountId == credential.Account.Id &&
            item.DueAt is { } at && at <= clock.GetUtcNow() && item.Status is "searching" or "importing" or "ready");
        if (due is null) return;
        ResearcherState result;
        try
        {
            if (due.Status == "searching")
            {
                var found = await remote.SearchResearcherAsync(credential.Token, new(due.SearchName), ct);
                var candidates = found.Candidates.Take(5).ToArray();
                result = due with { Status = candidates.Length > 0 ? "choice" : "unmatched", Candidates = candidates, DueAt = null, LastError = null, Failures = 0 };
            }
            else
            {
                var works = await remote.ResearcherWorksAsync(credential.Token, new(due.Researcher!.Id), ct);
                var now = clock.GetUtcNow();
                result = due with { Status = "ready", Works = works.Works.Take(2000).ToArray(), UpdatedAt = now, DueAt = now + RefreshInterval, LastError = null, Failures = 0 };
            }
        }
        catch (ResearchFailure failure)
        {
            logger.LogWarning("Researcher {Operation} failed: {Code}.", due.Status == "searching" ? "search" : "import", failure.Code);
            // Rate limits wait an hour; other failures retry after 1, 2, 4, 8 and then every 15 minutes.
            var retry = failure.Status == 429 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(Math.Min(15, Math.Pow(2, due.Failures)));
            result = due with { DueAt = clock.GetUtcNow() + retry, LastError = failure.Message, Failures = due.Failures + 1 };
        }
        await store.ChangeAsync(current =>
        {
            var latest = current.Researchers?.FirstOrDefault(item => item.AccountId == due.AccountId);
            // A newer decision or another account wins; this result is then simply dropped.
            if (current.Credential?.Account.Id != due.AccountId || latest?.Version != due.Version) return current;
            return current with { Researchers = Replace(current.Researchers, result with { Version = due.Version + 1 }) };
        }, ct);
    }

    private static IReadOnlyList<ResearcherState> Replace(IReadOnlyList<ResearcherState>? researchers, ResearcherState next) =>
        [.. (researchers ?? []).Where(item => item.AccountId != next.AccountId), next];

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
