using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal enum SyncMode { Off, Locked, Ready }

/// <summary>
/// End-to-end encrypted sync of this account's research through Meld's storage. The data key is created here, encrypted
/// under the user's passphrase (and a recovery key) in the account's key file, and kept on this computer only inside the
/// encrypted research state. Before any import or update, other computers' work is pulled (so only the delta is processed
/// here); finished work, checkpoints of long imports, and changed choices are pushed. Uploads are conditional on the blob's
/// ETag, so a computer that lost a race pulls and reconciles instead of overwriting.
/// </summary>
internal sealed class ResearchSync(IResearchStore store, IResearchRemote remote, TimeProvider clock, ILogger<ResearchSync> logger)
{
    private static readonly TimeSpan ConfigurationAge = TimeSpan.FromMinutes(10), PullEvery = TimeSpan.FromMinutes(5),
        CheckpointEvery = TimeSpan.FromMinutes(10);
    private (bool Configured, DateTimeOffset At)? _configuration;
    private bool? _remoteHasKey;
    private DateTimeOffset _lastPull = DateTimeOffset.MinValue;
    private IReadOnlyDictionary<string, string> _remote = new Dictionary<string, string>();
    private string? _lastError;

    public void RequestPull() => _lastPull = DateTimeOffset.MinValue;

    private static byte[]? DataKey(LocalResearchState state, Guid account) =>
        state.Sync is { } sync && sync.AccountId == account ? Convert.FromBase64String(sync.DataKey) : null;

    /// <summary>Whether this cycle may process: always when sync is off; with sync, only once this computer has the key.</summary>
    public async Task<SyncMode> ModeAsync(LinkedCredential credential, LocalResearchState state, CancellationToken ct)
    {
        if (_configuration is not { } known || clock.GetUtcNow() - known.At > ConfigurationAge)
            _configuration = ((await remote.StatusAsync(ct)).SyncConfigured, clock.GetUtcNow());
        if (!_configuration.Value.Configured) return SyncMode.Off;
        if (DataKey(state, credential.Account.Id) is not null) return SyncMode.Ready;
        _remoteHasKey = (await remote.SyncListAsync(credential.Token, ct)).Blobs.Any(blob => blob.Name == SyncCrypto.KeyBlob);
        return SyncMode.Locked;
    }

    public SyncStatusResponse Status(LocalResearchState state)
    {
        var account = state.Credential?.Account.Id;
        string phase = _configuration is { Configured: false } ? "off"
            : account is null ? "signed-out"
            : state.Sync is { } sync && sync.AccountId == account ? "ready"
            : _configuration is null || _remoteHasKey is null ? "checking"
            : _remoteHasKey == true ? "locked" : "setup";
        var mine = state.Sync is { } current && current.AccountId == account ? current : null;
        return new(phase, state.Projects.Count(item => item.AccountId == account && item.SyncETag is not null),
            mine?.LastPushAt, mine?.LastPullAt, _lastError);
    }

    /// <summary>Asks the server now whether sync is configured and whether this account already has a key file.</summary>
    public async Task<SyncStatusResponse> RefreshAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        _configuration = null;
        if (state.Credential is { } credential) await ModeAsync(credential, state, ct);
        else _configuration = ((await remote.StatusAsync(ct)).SyncConfigured, clock.GetUtcNow());
        return Status(await store.ReadAsync(ct));
    }

    /// <summary>First computer: creates the data key, the key file (passphrase and recovery key) and uploads it.</summary>
    public async Task<SyncKeyResponse> CreateAsync(LinkedCredential credential, string? passphrase, CancellationToken ct)
    {
        SyncCrypto.ValidatePassphrase(passphrase);
        byte[] dataKey = SyncCrypto.NewDataKey(), recovery = SyncCrypto.NewRecoveryKey();
        var file = SyncCrypto.CreateKeyFile(credential.Account.Id, dataKey, passphrase!, recovery, clock.GetUtcNow());
        var link = await LinkAsync(credential, SyncCrypto.KeyBlob, write: true, ct);
        string etag;
        try { etag = await remote.BlobPutAsync(link, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(file, ResearchJson.Options), null, ct); }
        catch (ResearchFailure failure) when (failure.Code == "sync_conflict")
        { throw new ResearchFailure(409, "sync_key_exists", "This account already has encrypted sync. Enter its passphrase, or start over."); }
        await KeepKeyAsync(credential, dataKey, etag, file.CreatedAt, ct);
        _remoteHasKey = true;
        return new(SyncCrypto.FormatRecoveryKey(recovery));
    }

    /// <summary>Another computer: unlocks the data key with the passphrase or the recovery key.</summary>
    public async Task UnlockAsync(LinkedCredential credential, SyncPassphraseRequest request, CancellationToken ct)
    {
        var (file, etag) = await KeyFileAsync(credential, ct) ??
            throw new ResearchFailure(404, "sync_key_missing", "This account has no encrypted sync yet. Choose a passphrase to start it.");
        byte[]? dataKey = !string.IsNullOrWhiteSpace(request.RecoveryKey)
            ? SyncCrypto.UnlockWithRecoveryKey(file, credential.Account.Id, request.RecoveryKey)
            : SyncCrypto.UnlockWithPassphrase(file, credential.Account.Id, request.Passphrase ?? "");
        if (dataKey is null)
            throw new ResearchFailure(400, "wrong_passphrase", !string.IsNullOrWhiteSpace(request.RecoveryKey)
                ? "That recovery key does not unlock this account's sync." : "That passphrase does not unlock this account's sync.");
        await KeepKeyAsync(credential, dataKey, etag, file.CreatedAt, ct);
        RequestPull();
    }

    /// <summary>
    /// Forgotten passphrase: deletes everything synced for the account and starts a new key. Research on this computer is
    /// kept and uploaded again; other computers must unlock with the new passphrase.
    /// </summary>
    public async Task<SyncKeyResponse> StartOverAsync(LinkedCredential credential, string? passphrase, CancellationToken ct)
    {
        SyncCrypto.ValidatePassphrase(passphrase);
        await remote.SyncDeleteAllAsync(credential.Token, ct);
        _remoteHasKey = false;
        logger.LogInformation("Encrypted sync was started over: the account's synced data was deleted.");
        return await CreateAsync(credential, passphrase, ct);
    }

    /// <summary>
    /// Shows the recovery key again on a computer that is unlocked. A key file made before it kept a copy gets a new recovery
    /// key (the previous one stops working).
    /// </summary>
    public async Task<SyncKeyResponse> RecoveryKeyAsync(LinkedCredential credential, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var dataKey = DataKey(await store.ReadAsync(ct), account) ??
            throw new ResearchFailure(409, "sync_locked", "Unlock encrypted sync on this computer first.");
        var link = await LinkAsync(credential, SyncCrypto.KeyBlob, write: true, ct);
        var current = await remote.BlobGetAsync(link, ct) ?? throw new ResearchFailure(404, "sync_key_missing", "The account's key file is missing.");
        var file = System.Text.Json.JsonSerializer.Deserialize<SyncKeyFile>(current.Content, ResearchJson.Options)!;
        if (SyncCrypto.OpenRecoveryKey(file, account, dataKey) is { } existing) return new(SyncCrypto.FormatRecoveryKey(existing));
        byte[] recovery = SyncCrypto.NewRecoveryKey();
        var replaced = SyncCrypto.ReplaceRecoveryKey(file, account, dataKey, recovery);
        string etag = await remote.BlobPutAsync(link, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(replaced, ResearchJson.Options), current.ETag, ct);
        await store.ChangeAsync(state => state.Sync is { } sync && sync.AccountId == account
            ? state with { Sync = sync with { KeyETag = etag } } : state, ct);
        logger.LogInformation("Made a new recovery key: the account's key file had no copy of the previous one.");
        return new(SyncCrypto.FormatRecoveryKey(recovery), Replaced: true);
    }

    /// <summary>A new passphrase for the same data key (the recovery key keeps working).</summary>
    public async Task ChangePassphraseAsync(LinkedCredential credential, string? passphrase, CancellationToken ct)
    {
        var dataKey = DataKey(await store.ReadAsync(ct), credential.Account.Id) ??
            throw new ResearchFailure(409, "sync_locked", "Unlock encrypted sync on this computer first.");
        var link = await LinkAsync(credential, SyncCrypto.KeyBlob, write: true, ct);
        var current = await remote.BlobGetAsync(link, ct) ?? throw new ResearchFailure(404, "sync_key_missing", "The account's key file is missing.");
        var file = System.Text.Json.JsonSerializer.Deserialize<SyncKeyFile>(current.Content, ResearchJson.Options)!;
        var changed = SyncCrypto.ChangePassphrase(file, credential.Account.Id, dataKey, passphrase!);
        string etag = await remote.BlobPutAsync(link, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(changed, ResearchJson.Options), current.ETag, ct);
        await store.ChangeAsync(state => state.Sync is { } sync && sync.AccountId == credential.Account.Id
            ? state with { Sync = sync with { KeyETag = etag, KeyCreatedAt = changed.CreatedAt } } : state, ct);
    }

    public async Task<bool> PullDueAsync(LinkedCredential credential, bool force, CancellationToken ct)
    {
        if (!force && clock.GetUtcNow() - _lastPull < PullEvery) return false;
        await PullAsync(credential, ct);
        return true;
    }

    /// <summary>Downloads what other computers changed: the profile first (it says which projects are included), then projects.</summary>
    public async Task PullAsync(LinkedCredential credential, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        var dataKey = DataKey(state, account) ?? throw new ResearchFailure(409, "sync_locked", "Unlock encrypted sync on this computer first.");
        try
        {
            await CheckKeyAsync(credential, state, ct);
            if (_remote.TryGetValue(SyncCrypto.ProfileBlob, out var profileTag) && profileTag != state.Sync!.ProfileETag)
            {
                var blob = await DownloadAsync(credential, SyncCrypto.ProfileBlob, ct);
                if (blob is not null)
                {
                    var bundle = SyncCrypto.Open<ProfileBundle>(dataKey, account, SyncCrypto.ProfileBlob, blob.Content);
                    await store.ChangeAsync(current => current.Credential?.Account.Id == account && current.Sync?.AccountId == account
                        ? SyncBundles.ApplyProfile(current, account, bundle, blob.ETag, ResearchEvidence.ConsentVersion) : current, ct);
                    logger.LogInformation("Pulled choices and research cards changed on another computer.");
                }
            }
            state = await store.ReadAsync(ct);
            var changed = state.Projects.Where(item => item.AccountId == account)
                .Select(item => (Project: item, Name: SyncCrypto.ProjectBlob(dataKey, item.ProjectId)))
                .Where(item => _remote.TryGetValue(item.Name, out var tag) && tag != item.Project.SyncETag).ToArray();
            foreach (var (project, name) in changed)
            {
                var blob = await DownloadAsync(credential, name, ct);
                if (blob is null) continue;
                var bundle = SyncCrypto.Open<ProjectBundle>(dataKey, account, name, blob.Content);
                if (bundle.ProjectId != project.ProjectId) continue;
                foreach (var (hash, text) in bundle.Texts)
                    if (ResearchEvidence.Hash(text) == hash) await store.WriteTextAsync(hash, text, ct);
                await store.ChangeAsync(current => current.Credential?.Account.Id == account
                    ? SyncBundles.ApplyProject(current, account, bundle, blob.ETag) : current, ct);
                using var scope = logger.BeginScope(new Dictionary<string, object?> { ["ProjectId"] = project.ProjectId });
                logger.LogInformation("Pulled work from another computer: {Files} file(s) already folded in; only later changes are processed here.",
                    bundle.Processed?.Count ?? 0);
            }
            await store.ChangeAsync(current => current.Sync is { } sync && sync.AccountId == account
                ? current with { Sync = sync with { LastPullAt = clock.GetUtcNow() } } : current, ct);
            _lastPull = clock.GetUtcNow();
            _lastError = null;
        }
        catch (ResearchFailure failure) when (failure.Code is "sync_blob_invalid" or "sync_format_unsupported")
        {
            throw await LockAsync(account, "Synced data could not be decrypted with this computer's key. Unlock encrypted sync again.", ct);
        }
    }

    /// <summary>
    /// Lists the account's blobs and makes sure the key this computer holds is still the account's key. After a start over
    /// on another computer it is not: syncing stops here (nothing is uploaded under the old key) until unlocked again.
    /// A passphrase change keeps the key (and its creation time), so other computers continue.
    /// </summary>
    private async Task CheckKeyAsync(LinkedCredential credential, LocalResearchState state, CancellationToken ct)
    {
        var account = credential.Account.Id;
        _remote = (await remote.SyncListAsync(credential.Token, ct)).Blobs.ToDictionary(blob => blob.Name, blob => blob.ETag, StringComparer.Ordinal);
        var sync = state.Sync!;
        if (_remote.TryGetValue(SyncCrypto.KeyBlob, out var tag) && tag == sync.KeyETag) return;
        var current = tag is null ? null : await KeyFileAsync(credential, ct);
        if (current is not { } found || sync.KeyCreatedAt is { } created && found.File.CreatedAt != created)
        {
            _remoteHasKey = current is not null;
            throw await LockAsync(account, "Encrypted sync was started over on another computer. Enter the new passphrase here.", ct);
        }
        await store.ChangeAsync(value => value.Sync is { } mine && mine.AccountId == account
            ? value with { Sync = mine with { KeyETag = found.ETag, KeyCreatedAt = found.File.CreatedAt } } : value, ct);
    }

    private async Task<ResearchFailure> LockAsync(Guid account, string message, CancellationToken ct)
    {
        _lastError = message;
        await store.ChangeAsync(current => current.Sync?.AccountId == account ? current with { Sync = null } : current, ct);
        logger.LogWarning("Encrypted sync is locked on this computer until the passphrase is entered again.");
        return new ResearchFailure(409, "sync_locked", message);
    }

    /// <summary>
    /// Uploads finished work, checkpoints of long imports (every ten minutes), changed choices, and removes bundles of
    /// excluded projects. Failures are kept for the next cycle; they never undo local work.
    /// </summary>
    public async Task PushAsync(LinkedCredential credential, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        var dataKey = DataKey(state, account);
        if (dataKey is null) return;
        var now = clock.GetUtcNow();
        try
        {
            var excluded = (state.Decisions ?? []).Where(item => item.AccountId == account && !item.Included)
                .Select(item => item.ProjectId).ToHashSet(StringComparer.Ordinal);
            var projects = state.Projects.Where(item => item.AccountId == account && !excluded.Contains(item.ProjectId) && SyncBundles.HasResearch(item) &&
                SyncBundles.Fingerprint(item) != item.SyncedFor &&
                (item.Pending is null || item.LastSyncedAt is not { } synced || now - synced >= CheckpointEvery)).ToArray();
            var profile = SyncBundles.Profile(state, account, now);
            bool profileChanged = SyncBundles.Fingerprint(profile) != state.Sync!.ProfileSyncedFor;
            var removed = excluded.Select(id => SyncCrypto.ProjectBlob(dataKey, id)).Where(_remote.ContainsKey).ToArray();
            var names = projects.Select(item => SyncCrypto.ProjectBlob(dataKey, item.ProjectId))
                .Concat(profileChanged ? [SyncCrypto.ProfileBlob] : []).Concat(removed).Distinct().ToArray();
            if (names.Length == 0) return;
            await CheckKeyAsync(credential, state, ct);
            // The fresh listing decides which excluded projects still have a blob to delete.
            var stale = removed.Where(name => !_remote.ContainsKey(name)).ToHashSet(StringComparer.Ordinal);
            removed = [.. removed.Where(_remote.ContainsKey)];
            names = [.. names.Where(name => !stale.Contains(name))];
            if (names.Length == 0) return;
            var links = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var batch in names.Chunk(100))
                foreach (var link in (await remote.SyncLinksAsync(credential.Token, new(batch, Write: true), ct)).Links) links[link.Name] = link.Url;

            foreach (var project in projects)
            {
                string name = SyncCrypto.ProjectBlob(dataKey, project.ProjectId), print = SyncBundles.Fingerprint(project);
                var texts = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var hash in SyncBundles.TextHashes(project))
                    if (await store.ReadTextAsync(hash, ct) is { } text) texts[hash] = text;
                byte[] sealedValue = SyncCrypto.Seal(dataKey, account, name, SyncBundles.Project(project, texts, now));
                string? etag = await PutAsync(links[name], sealedValue, project.SyncETag, ct);
                if (etag is null) continue;
                Remember(name, etag);
                await store.ChangeAsync(current => current with
                {
                    Projects = [.. current.Projects.Select(item => item.AccountId == account && item.ProjectId == project.ProjectId
                        ? item with { SyncETag = etag, SyncedFor = SyncBundles.Fingerprint(item) == print ? print : item.SyncedFor, LastSyncedAt = now } : item)]
                }, ct);
                using var scope = logger.BeginScope(new Dictionary<string, object?> { ["ProjectId"] = project.ProjectId });
                logger.LogInformation(project.Pending is null ? "Synced the project (encrypted, {Bytes} bytes)." :
                    "Saved an encrypted checkpoint of the import ({Bytes} bytes).", sealedValue.Length);
            }
            if (profileChanged)
            {
                string print = SyncBundles.Fingerprint(profile);
                string? etag = await PutAsync(links[SyncCrypto.ProfileBlob], SyncCrypto.Seal(dataKey, account, SyncCrypto.ProfileBlob, profile),
                    state.Sync.ProfileETag, ct);
                if (etag is not null) Remember(SyncCrypto.ProfileBlob, etag);
                if (etag is not null)
                    await store.ChangeAsync(current => current.Sync is { } sync && sync.AccountId == account
                        ? current with { Sync = sync with { ProfileETag = etag, ProfileSyncedFor = print } } : current, ct);
            }
            foreach (var name in removed)
            {
                await remote.BlobDeleteAsync(links[name], ct);
                _remote = _remote.Where(item => item.Key != name).ToDictionary(StringComparer.Ordinal);
                logger.LogInformation("Deleted the synced copy of an excluded project.");
            }
            await store.ChangeAsync(current => current.Sync is { } sync && sync.AccountId == account
                ? current with { Sync = sync with { LastPushAt = now } } : current, ct);
            _lastError = null;
        }
        catch (ResearchFailure failure)
        {
            _lastError = failure.Message;
            logger.LogWarning("Encrypted sync upload failed ({Code}); it is retried on the next check.", failure.Code);
        }
    }

    /// <summary>Blobs uploaded here join the last listing, so a later exclusion knows to delete them before the next pull.</summary>
    private void Remember(string name, string etag) => _remote = new Dictionary<string, string>(_remote, StringComparer.Ordinal) { [name] = etag };

    /// <summary>The new ETag, or null when another computer changed the blob first (the next cycle pulls and reconciles).</summary>
    private async Task<string?> PutAsync(string url, byte[] content, string? ifMatch, CancellationToken ct)
    {
        try { return await remote.BlobPutAsync(url, content, ifMatch, ct); }
        catch (ResearchFailure failure) when (failure.Code == "sync_conflict")
        {
            RequestPull();
            logger.LogInformation("Another computer synced first; its work is merged on the next check.");
            return null;
        }
    }

    private async Task KeepKeyAsync(LinkedCredential credential, byte[] dataKey, string keyETag, DateTimeOffset keyCreatedAt, CancellationToken ct)
    {
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != credential.Account.Id)
                throw new ResearchFailure(409, "research_state_changed", "The account changed. Reload and try again.");
            // A new key or computer: every project and the profile are compared with the remote copies again.
            var projects = current.Projects.Select(item => item.AccountId == credential.Account.Id
                ? item with { SyncETag = null, SyncedFor = null, LastSyncedAt = null } : item).ToArray();
            return current with
            {
                Projects = projects,
                Sync = new(credential.Account.Id, Convert.ToBase64String(dataKey), clock.GetUtcNow(), KeyETag: keyETag, KeyCreatedAt: keyCreatedAt)
            };
        }, ct);
        _lastError = null;
    }

    private async Task<(SyncKeyFile File, string ETag)?> KeyFileAsync(LinkedCredential credential, CancellationToken ct)
    {
        var blob = await remote.BlobGetAsync(await LinkAsync(credential, SyncCrypto.KeyBlob, write: false, ct), ct);
        return blob is null ? null : (System.Text.Json.JsonSerializer.Deserialize<SyncKeyFile>(blob.Content, ResearchJson.Options)!, blob.ETag);
    }

    private async Task<SyncBlob?> DownloadAsync(LinkedCredential credential, string name, CancellationToken ct) =>
        await remote.BlobGetAsync(await LinkAsync(credential, name, write: false, ct), ct);

    private async Task<string> LinkAsync(LinkedCredential credential, string name, bool write, CancellationToken ct) =>
        (await remote.SyncLinksAsync(credential.Token, new([name], write), ct)).Links.Single().Url;
}
