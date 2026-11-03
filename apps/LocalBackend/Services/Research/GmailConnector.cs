using System.Collections.Concurrent;
using System.Net.Mail;
using System.Security.Cryptography;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Account → Gmail: connects Gmail accounts for read-only access. Google's consent page returns to this app's loopback
/// callback with a one-time code, which this app redeems itself with its PKCE verifier; Meld's server only supplies the
/// OAuth client. Grants are kept in the encrypted research state (and the end-to-end encrypted sync); short-lived access
/// tokens only in memory. Several accounts can be connected; removing one revokes its grant at Google.
/// </summary>
internal sealed class GmailConnector(IResearchStore store, IResearchRemote remote, IGmailGoogle google, TimeProvider clock,
    ILogger<GmailConnector> logger)
{
    /// <summary>The consent the user gives when connecting; a new version asks again.</summary>
    public const string ConsentVersion = "gmail-2026-11";
    /// <summary>How far back the first import reads (threads the user wrote in).</summary>
    public const int WindowDays = 30;
    private static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(10), ConfigurationAge = TimeSpan.FromMinutes(10);

    private sealed record Pending(string State, string Verifier, string RedirectUri, GmailClientResponse Client, Guid Account, DateTimeOffset ExpiresAt);
    private Pending? _pending;
    private string? _lastError;
    private (GmailClientResponse? Client, bool Configured, DateTimeOffset At)? _configuration;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _access = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GmailStatusResponse Status(LocalResearchState state, GmailImporter? importer = null)
    {
        // The answer whether the server offers Gmail is refreshed in the background, so status reads never wait for it.
        if (_configuration is not { } known || clock.GetUtcNow() - known.At >= ConfigurationAge) _ = ConfiguredAsync(CancellationToken.None);
        var account = state.Credential?.Account.Id;
        var accounts = (state.GmailAccounts ?? []).Where(item => item.AccountId == account && !item.Removed)
            .OrderBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .Select(item => Response(item, state, importer)).ToArray();
        return new(_configuration?.Configured ?? false, WindowDays, ConsentVersion, accounts, _lastError, GmailImporter.Threads(state, account));
    }

    /// <summary>Whether Meld's server offers Gmail (cached for ten minutes; refreshed by the status poll).</summary>
    public async Task<bool> ConfiguredAsync(CancellationToken ct)
    {
        if (_configuration is { } known && clock.GetUtcNow() - known.At < ConfigurationAge) return known.Configured;
        if (Interlocked.Exchange(ref _checking, 1) == 1) return _configuration?.Configured ?? false;
        try { _configuration = (_configuration?.Client, (await remote.StatusAsync(ct)).GmailConfigured, clock.GetUtcNow()); }
        catch (Exception error) when (error is ResearchFailure or HttpRequestException or TaskCanceledException)
        { /* Keep the last answer while the server is unreachable. */ }
        finally { Interlocked.Exchange(ref _checking, 0); }
        return _configuration?.Configured ?? false;
    }
    private int _checking;

    public async Task<GmailConnectResponse> BeginAsync(string redirectUri, string? consentVersion, CancellationToken ct)
    {
        if (consentVersion != ConsentVersion)
            throw new ResearchFailure(400, "gmail_consent_required", "Agree to how Meld reads your Gmail first.");
        var credential = Credential(await store.ReadAsync(ct));
        var client = await ClientAsync(credential, ct);
        string state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(48));
        var expires = clock.GetUtcNow() + PendingFor;
        _pending = new(state, verifier, redirectUri, client, credential.Account.Id, expires);
        _lastError = null;
        return new(google.AuthorizationUrl(client, redirectUri, state, IGmailGoogle.Challenge(verifier)).AbsoluteUri, expires);
    }

    /// <summary>The loopback callback: checks it answers the sign-in started here, redeems the code and adds the account.</summary>
    public async Task<string?> CompleteAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        var pending = _pending;
        if (pending is null || pending.ExpiresAt <= clock.GetUtcNow() || state is not { Length: 64 } ||
            !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(state), System.Text.Encoding.ASCII.GetBytes(pending.State)))
            return Fail("This Gmail sign-in was not started here or has expired. Choose Add Gmail account again.");
        _pending = null;
        if (error is not null || string.IsNullOrEmpty(code))
            return Fail(error == "access_denied" ? "Gmail was not connected: access was not allowed." : "Google did not complete the Gmail sign-in.");
        string connected;
        await _gate.WaitAsync(ct);
        try
        {
            var tokens = await google.ExchangeAsync(pending.Client, code, pending.RedirectUri, pending.Verifier, ct);
            if (!tokens.Scope.Split(' ').Contains(IGmailGoogle.ReadOnlyScope) || tokens.RefreshToken is null)
            {
                await RevokeQuietlyAsync(tokens.RefreshToken ?? tokens.AccessToken, ct);
                return Fail("Gmail was not connected: Meld needs permission to read your email. Connect again and allow it.");
            }
            var profile = await google.ProfileAsync(tokens.AccessToken, ct);
            if (!IsAddress(profile.Address)) return Fail("Google did not report a Gmail address for this account.");
            var now = clock.GetUtcNow();
            await store.ChangeAsync(current =>
            {
                if (current.Credential?.Account.Id != pending.Account) throw new ResearchFailure(409, "research_state_changed", "The Meld account changed.");
                // Connecting an account again (for example after Google ended the grant) keeps what was already analyzed.
                var existing = (current.GmailAccounts ?? []).FirstOrDefault(item => item.AccountId == pending.Account && !item.Removed &&
                    string.Equals(item.Address, profile.Address, StringComparison.OrdinalIgnoreCase));
                var account = existing is null
                    ? new GmailAccountState(pending.Account, profile.Address, tokens.RefreshToken, ConsentVersion, now, HistoryId: profile.HistoryId)
                    : existing with { RefreshToken = tokens.RefreshToken, ConsentVersion = ConsentVersion, ChangedAt = now, Status = "connected", Error = null };
                return current with { GmailAccounts = [.. Others(current, pending.Account, profile.Address), account] };
            }, ct);
            _access[profile.Address] = (tokens.AccessToken, tokens.ExpiresAt);
            connected = profile.Address;
            logger.LogInformation("Connected a Gmail account for read-only access.");
        }
        catch (ResearchFailure failure) { return Fail(failure.Message); }
        finally { _gate.Release(); }
        try { await CheckAsync(connected, ct); }
        catch (ResearchFailure) { /* Shown on the account; checked again later. */ }
        return null;
    }

    /// <summary>Counts the threads the user wrote in during the import window (no mail content is read yet).</summary>
    public async Task<GmailAccountResponse> CheckAsync(string address, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        var credential = Credential(state);
        var account = Find(state, credential.Account.Id, address);
        try
        {
            string token = await AccessTokenAsync(credential, account, ct);
            IReadOnlyList<GmailThreadRef> threads;
            try { threads = await google.ParticipatedThreadsAsync(token, WindowDays, ct); }
            catch (ResearchFailure failure) when (failure.Code == "gmail_token_expired")
            {
                _access.TryRemove(account.Address, out _);
                threads = await google.ParticipatedThreadsAsync(await AccessTokenAsync(credential, account, ct), WindowDays, ct);
            }
            var checkedAccount = await UpdateAsync(credential.Account.Id, account.Address,
                item => item with { Status = "connected", Error = null, Threads = threads.Count, CheckedAt = clock.GetUtcNow() }, ct);
            return Response(checkedAccount, null, null);
        }
        catch (ResearchFailure failure)
        {
            string status = failure.Code == "gmail_reconnect" ? "reconnect" : "error";
            await UpdateAsync(credential.Account.Id, account.Address, item => item with { Status = status, Error = failure.Message }, ct);
            logger.LogWarning("Checking a Gmail account failed ({Code}).", failure.Code);
            throw;
        }
    }

    /// <summary>Disconnects an account: revokes Meld's access at Google and removes it (and its workstreams) everywhere.</summary>
    public async Task RemoveAsync(string address, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        var credential = Credential(state);
        var account = Find(state, credential.Account.Id, address);
        if (account.RefreshToken is { } grant) await RevokeQuietlyAsync(grant, ct);
        _access.TryRemove(account.Address, out _);
        var now = clock.GetUtcNow();
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != credential.Account.Id) throw new ResearchFailure(409, "research_state_changed", "The Meld account changed.");
            var tombstone = new GmailAccountState(credential.Account.Id, account.Address, null, account.ConsentVersion, now, Removed: true, Status: "removed");
            // The account's email leaves every project's details.
            var themes = (current.Themes ?? []).Select(theme => theme.AccountId == credential.Account.Id && theme.Details is { Count: > 0 } details
                ? theme with { Details = DetailPlan.Without(details, source => source.Kind == DetailSource.Gmail &&
                    string.Equals(source.Container, account.Address, StringComparison.OrdinalIgnoreCase)).Details } : theme).ToArray();
            return current with
            {
                Themes = themes,
                GmailAccounts = [.. Others(current, credential.Account.Id, account.Address), tombstone],
                GmailThreads = [.. (current.GmailThreads ?? []).Where(item => item.AccountId != credential.Account.Id ||
                    !string.Equals(item.Address, account.Address, StringComparison.OrdinalIgnoreCase))],
                Projects = [.. current.Projects.Where(project => project.AccountId != credential.Account.Id || project.Source != ResearchSources.Gmail ||
                    !string.Equals(project.Parent, account.Address, StringComparison.OrdinalIgnoreCase))]
            };
        }, ct);
        logger.LogInformation("Disconnected a Gmail account and revoked Meld's access.");
    }

    /// <summary>Leaving Meld Research: revokes every Gmail grant (best effort).</summary>
    public async Task RevokeAllAsync(LocalResearchState state, CancellationToken ct)
    {
        foreach (var account in (state.GmailAccounts ?? []).Where(item => !item.Removed && item.RefreshToken is not null))
            await RevokeQuietlyAsync(account.RefreshToken!, ct);
        _access.Clear();
    }

    /// <summary>
    /// Calls Gmail with a current access token (renewed once if Google says it expired). When Google ended the grant, the
    /// account is marked for reconnecting.
    /// </summary>
    internal async Task<T> WithAccessAsync<T>(LinkedCredential credential, GmailAccountState account, Func<string, Task<T>> call, CancellationToken ct)
    {
        try
        {
            try { return await call(await AccessTokenAsync(credential, account, ct)); }
            catch (ResearchFailure failure) when (failure.Code == "gmail_token_expired")
            {
                _access.TryRemove(account.Address, out _);
                return await call(await AccessTokenAsync(credential, account, ct));
            }
        }
        catch (ResearchFailure failure) when (failure.Code is "gmail_reconnect" or "gmail_scope_missing")
        {
            try { await UpdateAsync(credential.Account.Id, account.Address, item => item with { Status = "reconnect", Error = failure.Message }, ct); }
            catch (ResearchFailure) { /* Removed meanwhile. */ }
            throw;
        }
    }

    private async Task<string> AccessTokenAsync(LinkedCredential credential, GmailAccountState account, CancellationToken ct)
    {
        if (_access.TryGetValue(account.Address, out var cached) && cached.ExpiresAt > clock.GetUtcNow()) return cached.Token;
        if (account.RefreshToken is null) throw new ResearchFailure(401, "gmail_reconnect", "Connect this Gmail account again.");
        var tokens = await google.RefreshAsync(await ClientAsync(credential, ct), account.RefreshToken, ct);
        _access[account.Address] = (tokens.AccessToken, tokens.ExpiresAt);
        return tokens.AccessToken;
    }

    private async Task<GmailClientResponse> ClientAsync(LinkedCredential credential, CancellationToken ct)
    {
        if (_configuration is { Client: { } client } known && clock.GetUtcNow() - known.At < ConfigurationAge) return client;
        try
        {
            var fresh = await remote.GmailClientAsync(credential.Token, ct);
            _configuration = (fresh, true, clock.GetUtcNow());
            return fresh;
        }
        catch (ResearchFailure failure) when (failure.Code == "gmail_not_configured")
        {
            _configuration = (null, false, clock.GetUtcNow());
            throw;
        }
    }

    private async Task<GmailAccountState> UpdateAsync(Guid account, string address, Func<GmailAccountState, GmailAccountState> change, CancellationToken ct)
    {
        GmailAccountState? updated = null;
        await store.ChangeAsync(current =>
        {
            var existing = (current.GmailAccounts ?? []).FirstOrDefault(item => item.AccountId == account && !item.Removed &&
                string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return current;
            updated = change(existing);
            return current with { GmailAccounts = [.. Others(current, account, address), updated] };
        }, ct);
        return updated ?? throw new ResearchFailure(404, "gmail_account_not_found", "This Gmail account is not connected.");
    }

    private async Task RevokeQuietlyAsync(string token, CancellationToken ct)
    {
        try { await google.RevokeAsync(token, ct); }
        catch (Exception error) when (error is ResearchFailure or HttpRequestException or TaskCanceledException)
        { logger.LogWarning("Revoking Gmail access at Google failed ({Type}); access ends when Google expires it.", error.GetType().Name); }
    }

    private string? Fail(string message)
    {
        _lastError = message;
        logger.LogWarning("Connecting a Gmail account did not complete.");
        return message;
    }

    private static IEnumerable<GmailAccountState> Others(LocalResearchState state, Guid account, string address) =>
        (state.GmailAccounts ?? []).Where(item => item.AccountId != account || !string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));

    /// <summary>The signed-in account's connected Gmail account with this address (404 otherwise).</summary>
    public GmailAccountState Require(LocalResearchState state, string address) => Find(state, Credential(state).Account.Id, address);

    private static GmailAccountState Find(LocalResearchState state, Guid account, string address) =>
        (state.GmailAccounts ?? []).FirstOrDefault(item => item.AccountId == account && !item.Removed &&
            string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)) ??
        throw new ResearchFailure(404, "gmail_account_not_found", "This Gmail account is not connected.");

    private static GmailAccountResponse Response(GmailAccountState item, LocalResearchState? state, GmailImporter? importer) =>
        new(item.Address, item.Status, item.Error, item.Threads, item.CheckedAt, item.ChangedAt,
            state?.GmailThreads?.Count(thread => thread.AccountId == item.AccountId &&
                string.Equals(thread.Address, item.Address, StringComparison.OrdinalIgnoreCase)) ?? 0,
            state?.GmailThreads?.Where(thread => thread.AccountId == item.AccountId && thread.Status == GmailThreadRecord.Assigned && thread.CardId is not null &&
                string.Equals(thread.Address, item.Address, StringComparison.OrdinalIgnoreCase)).Select(thread => thread.CardId).Distinct().Count() ?? 0,
            item.LastImportAt, item.Status == "connected" && item.LastImportAt is not null ? GmailImporter.NextImportAt(item) : null,
            importer?.Progress(item.Address), importer?.LastError(item.Address));

    private LinkedCredential Credential(LocalResearchState state) =>
        state.Credential is { } credential && credential.ExpiresAt > clock.GetUtcNow() ? credential
            : throw new ResearchFailure(401, "meld_account_required", "Sign in with Google first.");

    private static bool IsAddress(string? value) => value is { Length: > 3 and <= 320 } && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;
}
