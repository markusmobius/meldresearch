using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Imports connected Gmail accounts into Gmail workstreams, run by the research worker a few threads at a time. About once
/// an hour (with a per-account offset of up to ten minutes) it lists the threads the user wrote in during the import window
/// and analyzes only the threads with new mail, oldest first, and of those only the new messages (earlier ones of the thread
/// are background). Foundry decides per thread: skip it, continue one of the user's workstreams, or start one; quotes are
/// checked against the email text. Each thread is committed on its own, so a restart continues where it stopped, and an
/// unchanged mailbox makes no model call. With encrypted sync the analyzed threads are synced, and a lease in the synced
/// profile makes other computers wait while one imports, so mail is not analyzed twice.
/// </summary>
internal sealed partial class GmailImporter(IResearchStore store, IResearchRemote remote, GmailConnector connector, IGmailGoogle google,
    TimeProvider clock, ILogger<GmailImporter> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1), Jitter = TimeSpan.FromMinutes(10), LeaseFor = TimeSpan.FromMinutes(15),
        RetryAfter = TimeSpan.FromMinutes(2);
    public const int ThreadsPerStep = 3, MaxCandidates = 8, FailuresBeforeSkip = 3, MaxProcessed = 1500;
    /// <summary>Characters of new messages per request; the rest of the thread limit is left for earlier messages.</summary>
    public const int MessageBudget = 32000;

    private sealed class Run(string address, IReadOnlyList<GmailThreadRef> threads, bool initial, bool leased)
    {
        public string Address { get; } = address;
        public Queue<GmailThreadRef> Queue { get; } = new(threads);
        public int Total { get; } = threads.Count;
        public int Done { get; set; }
        public bool Initial { get; } = initial;
        public bool Leased { get; } = leased;
    }

    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    private string? _device;

    /// <summary>Import now (Account → Gmail), also if an import ran recently.</summary>
    public void Request(string address)
    {
        _requested[address] = 0;
        _retryAt.TryRemove(address, out _);
    }

    public GmailImportProgress? Progress(string address) =>
        _runs.TryGetValue(address, out var run) ? new(run.Done, run.Total)
        : _waiting.TryGetValue(address, out var until) && until > clock.GetUtcNow() ? new(0, 0, Waiting: true) : null;

    public string? LastError(string address) => _errors.TryGetValue(address, out var error) ? error : null;

    /// <summary>When the next regular import is due: an hour after the last one, offset by up to ten minutes per account and import.</summary>
    public static DateTimeOffset NextImportAt(GmailAccountState account)
    {
        var last = account.LastImportAt ?? account.ChangedAt;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{account.Address.ToLowerInvariant()}|{last.UtcTicks}"));
        double offset = BitConverter.ToUInt32(hash, 0) / (double)uint.MaxValue * 2 - 1;
        return last + Interval + Jitter * offset;
    }

    /// <summary>Keeps the analyzed threads with the latest mail; older ones are outside the import window anyway.</summary>
    public static IReadOnlyList<GmailThreadState> Prune(IEnumerable<GmailThreadState> threads) =>
        [.. threads.OrderByDescending(thread => thread.LastSentAt).ThenBy(thread => thread.Id, StringComparer.Ordinal).Take(MaxProcessed)];

    public bool HasWork(LocalResearchState state, Guid account, DateTimeOffset now) =>
        Eligible(state, account).Any(mail => !Blocked(mail.Address, now) && (_runs.ContainsKey(mail.Address) || Due(mail, now)));

    /// <summary>
    /// Continues the running import (or starts one that is due) for one account by up to <see cref="ThreadsPerStep"/> threads.
    /// Returns whether import work remains that can continue straight away.
    /// </summary>
    public async Task<bool> StepAsync(LinkedCredential credential, ResearchSync? sync, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        var account = credential.Account.Id;
        var eligible = Eligible(state, account).OrderBy(mail => mail.Address, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var stale in _runs.Keys.Where(address => !eligible.Any(mail => string.Equals(mail.Address, address, StringComparison.OrdinalIgnoreCase))))
            _runs.TryRemove(stale, out _);
        var now = clock.GetUtcNow();
        foreach (var mail in eligible)
        {
            if (Blocked(mail.Address, now) || !_runs.TryGetValue(mail.Address, out var run) && !Due(mail, now)) continue;
            try
            {
                run ??= await StartAsync(credential, state, mail, sync, ct);
                if (run is not null) await ContinueAsync(credential, run, ct);
            }
            catch (ResearchFailure failure) when (failure.Code is "research_state_changed" or "gmail_lease_lost")
            {
                _runs.TryRemove(mail.Address, out _);
                if (failure.Code == "gmail_lease_lost") logger.LogInformation("Another computer took over importing this Gmail account.");
            }
            catch (Exception error) when (error is ResearchFailure or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Kept for the next attempt: what was committed stays, the rest is tried again shortly.
                string code = error is ResearchFailure failure ? failure.Code : error.GetType().Name;
                _errors[mail.Address] = error is ResearchFailure known ? known.Message : "Gmail or Meld's server did not answer. Meld tries again shortly.";
                _retryAt[mail.Address] = clock.GetUtcNow() + RetryAfter;
                logger.LogWarning("Gmail import paused ({Code}); it continues in {Minutes} minutes.", code, RetryAfter.TotalMinutes);
            }
            return HasWork(await store.ReadAsync(ct), account, clock.GetUtcNow());
        }
        return false;
    }

    private async Task<Run?> StartAsync(LinkedCredential credential, LocalResearchState state, GmailAccountState mail, ResearchSync? sync, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        bool forced = _requested.TryRemove(mail.Address, out _);
        string device = await DeviceAsync(ct);
        if (sync is not null)
        {
            if (mail.LeaseBy is { } holder && holder != device && mail.LeaseUntil is { } until && until > now)
            {
                _retryAt[mail.Address] = until;
                _waiting[mail.Address] = until;
                logger.LogInformation("Another computer is importing this Gmail account; this computer waits.");
                return null;
            }
            // The lease must reach the synced profile before any mail is read; if another computer synced first, wait and pull.
            string? before = state.Sync?.ProfileETag;
            await ChangeAccountAsync(credential, mail.Address, item => item with { LeaseBy = device, LeaseUntil = now + LeaseFor }, ct);
            await sync.PushAsync(credential, ct);
            var after = await store.ReadAsync(ct);
            if (after.Sync?.ProfileETag is not { } etag || etag == before || Find(after, credential.Account.Id, mail.Address)?.LeaseBy != device)
            {
                _retryAt[mail.Address] = now + RetryAfter;
                if (forced) _requested[mail.Address] = 0;
                logger.LogInformation("Could not confirm that no other computer is importing this Gmail account; trying again shortly.");
                return null;
            }
        }
        _waiting.TryRemove(mail.Address, out _);
        var threads = await connector.WithAccessAsync(credential, mail, token => google.ParticipatedThreadsAsync(token, GmailConnector.WindowDays, ct), ct);
        var processed = (mail.Processed ?? []).ToDictionary(thread => thread.Id, StringComparer.Ordinal);
        // The listing is newest first; mail is analyzed in the order it was written.
        var changed = threads.Where(thread => !processed.TryGetValue(thread.Id, out var known) || known.HistoryId != thread.HistoryId).Reverse().ToArray();
        await ChangeAccountAsync(credential, mail.Address, item => item with { Threads = threads.Count, CheckedAt = now, Status = "connected", Error = null }, ct);
        var run = new Run(mail.Address, changed, mail.LastImportAt is null, sync is not null);
        _runs[mail.Address] = run;
        _errors.TryRemove(mail.Address, out _);
        logger.LogInformation("Gmail import started: {Changed} of {Threads} thread(s) have new mail.", changed.Length, threads.Count);
        return run;
    }

    private async Task ContinueAsync(LinkedCredential credential, Run run, CancellationToken ct)
    {
        for (int done = 0; done < ThreadsPerStep && run.Queue.TryPeek(out var next); done++)
        {
            try { await ThreadAsync(credential, run, next, ct); }
            catch (ResearchFailure failure) when (failure.Code == "gmail_thread_missing")
            { /* Deleted since it was listed: nothing to analyze. */ }
            catch (ResearchFailure failure) when (failure.Status is 400 or 422 or 502 && !ResearchCoordinator.Transient(failure.Code))
            {
                // A thread that cannot be analyzed must not block the others: after a few attempts it is passed over until it changes.
                string key = $"{run.Address}|{next.Id}|{next.HistoryId}";
                int attempts = _failures.AddOrUpdate(key, 1, (_, count) => count + 1);
                if (attempts < FailuresBeforeSkip) throw;
                _failures.TryRemove(key, out _);
                logger.LogWarning("A Gmail thread could not be analyzed ({Code}, {Attempts} attempts); it is passed over until it changes.",
                    failure.Code, attempts);
                await PassOverAsync(credential, run, next, ct);
            }
            run.Queue.Dequeue();
            run.Done++;
            _errors.TryRemove(run.Address, out _);
        }
        if (run.Queue.Count != 0) return;
        var now = clock.GetUtcNow();
        string device = await DeviceAsync(ct);
        await ChangeAccountAsync(credential, run.Address, item => item with
        {
            LastImportAt = now,
            LeaseBy = item.LeaseBy == device ? null : item.LeaseBy,
            LeaseUntil = item.LeaseBy == device ? null : item.LeaseUntil
        }, ct);
        _runs.TryRemove(run.Address, out _);
        logger.LogInformation("Gmail import finished: {Threads} thread(s) with new mail analyzed.", run.Total);
    }

    /// <summary>Analyzes one thread's new messages (in parts if there are many) and commits each part.</summary>
    private async Task ThreadAsync(LinkedCredential credential, Run run, GmailThreadRef reference, CancellationToken ct)
    {
        var mail = Find(await store.ReadAsync(ct), credential.Account.Id, run.Address) ?? throw StateChanged();
        var thread = await connector.WithAccessAsync(credential, mail, token => google.ThreadAsync(token, reference.Id, ct), ct);
        var known = mail.Processed?.FirstOrDefault(item => item.Id == thread.Id);
        string self = mail.Address.ToLowerInvariant();
        // Mailing lists and automated mail count only when the user wrote them.
        var usable = thread.Messages.Where(message => message.Text.Length != 0 && EmailSteps.IsGmailId(message.Id))
            .Select(message => message with
            {
                From = Trim(message.From.Length == 0 ? "(unknown sender)" : message.From, 320),
                FromUser = message.FromUser || EmailText.Address(message.From) == self
            })
            .Where(message => !message.Bulk || message.FromUser).ToList();
        var since = clock.GetUtcNow() - TimeSpan.FromDays(GmailConnector.WindowDays);
        int first = known is null ? usable.FindIndex(message => message.SentAt >= since) : usable.FindIndex(message => message.SentAt > known.LastSentAt);
        string? workstream = known?.WorkstreamId;
        if (first < 0 || !usable.Any(message => message.FromUser))
        {
            var last = thread.Messages.LastOrDefault();
            await CommitAsync(credential, run, thread.Id, thread.HistoryId, known, last?.Id ?? known?.LastMessageId ?? "",
                last?.SentAt ?? known?.LastSentAt ?? clock.GetUtcNow(), workstream, null, ct);
            return;
        }
        string subject = Trim(thread.Subject, 300);
        while (first < usable.Count)
        {
            var part = new List<GmailMessage>();
            int characters = 0;
            for (int index = first; index < usable.Count && part.Count < EmailSteps.MaxMessages; index++)
            {
                if (part.Count != 0 && characters + usable[index].Text.Length > MessageBudget) break;
                part.Add(usable[index]);
                characters += usable[index].Text.Length;
            }
            var context = new List<GmailMessage>();
            for (int index = first - 1; index >= 0 && context.Count < EmailSteps.MaxContext; index--)
            {
                if (characters + usable[index].Text.Length > EmailSteps.MaxThreadCharacters) break;
                context.Insert(0, usable[index]);
                characters += usable[index].Text.Length;
            }
            first += part.Count;
            var candidates = Candidates(await store.ReadAsync(ct), credential.Account.Id, run.Address, workstream,
                subject + "\n" + string.Join("\n", part.Select(message => message.Text)));
            if (workstream is not null && candidates.All(candidate => candidate.Id != workstream)) workstream = null;
            var request = new EmailStepRequest(thread.Id, subject, self, [.. part.Select(Email)], [.. context.Select(Email)], workstream, candidates);
            EmailSteps.ValidateRequest(request);
            var response = await remote.EmailStepAsync(credential.Token, request, ct);
            EmailSteps.ValidateResponse(request, response);
            // A thread analyzed in parts keeps an unfinished marker until its last part, so a restart continues it.
            workstream = await CommitAsync(credential, run, thread.Id, first >= usable.Count ? thread.HistoryId : "", known,
                part[^1].Id, part[^1].SentAt, workstream, response, ct);
            known = new(thread.Id, "", part[^1].Id, part[^1].SentAt, workstream, clock.GetUtcNow());
        }
    }

    private async Task PassOverAsync(LinkedCredential credential, Run run, GmailThreadRef reference, CancellationToken ct)
    {
        var known = Find(await store.ReadAsync(ct), credential.Account.Id, run.Address)?.Processed?.FirstOrDefault(item => item.Id == reference.Id);
        await CommitAsync(credential, run, reference.Id, reference.HistoryId, known, known?.LastMessageId ?? "", clock.GetUtcNow(),
            known?.WorkstreamId, null, ct);
    }

    /// <summary>
    /// Commits one analyzed part of a thread atomically: the workstream (updated or new) with its Updates, and the thread as
    /// analyzed. Nothing is committed if the account changed, the Gmail account was removed or another computer took over.
    /// </summary>
    private async Task<string?> CommitAsync(LinkedCredential credential, Run run, string threadId, string historyId, GmailThreadState? known,
        string lastMessageId, DateTimeOffset lastSentAt, string? workstream, EmailStepResponse? response, CancellationToken ct)
    {
        string device = await DeviceAsync(ct);
        string? result = workstream;
        await store.ChangeAsync(current =>
        {
            var now = clock.GetUtcNow();
            if (current.Credential?.Account.Id != credential.Account.Id || current.Credential.Token != credential.Token) throw StateChanged();
            var mail = Find(current, credential.Account.Id, run.Address) ?? throw StateChanged();
            if (run.Leased && mail.LeaseBy != device) throw new ResearchFailure(409, "gmail_lease_lost", "Another computer is importing this Gmail account.");
            var projects = current.Projects;
            result = workstream;
            if (response is { Decision: EmailSteps.Existing or EmailSteps.New, Profile: { } profile })
            {
                var existing = response.Decision == EmailSteps.Existing
                    ? projects.FirstOrDefault(item => item.AccountId == credential.Account.Id && item.ProjectId == response.WorkstreamId &&
                        item.Source == ResearchSources.Gmail) ?? throw StateChanged()
                    : null;
                string id = existing?.ProjectId ?? ResearchSources.NewGmailWorkstreamId();
                string name = profile.Title;
                var updates = response.Updates.Select(update =>
                {
                    // An email Update happened when its mail was written.
                    var written = update.Evidence.Max(citation => citation.Email!.SentAt);
                    return new ResearchUpdate(Guid.NewGuid(), id, name, run.Initial ? ResearchUpdateKind.InitialImport : ResearchUpdateKind.Change,
                        update.Title, update.Summary, written, written, known is null ? null : Revision(threadId, known.LastMessageId),
                        Revision(threadId, lastMessageId), Guid.NewGuid(), update.Evidence, null);
                }).ToArray();
                var project = existing is null
                    ? new MonitoredProject(credential.Account.Id, id, name, true, ResearchEvidence.ConsentVersion, null, null, [], updates, now, now,
                        null, null, response.Explanation, 0, 0, Profile: profile, ImportComplete: true, Source: ResearchSources.Gmail, Parent: mail.Address)
                    : existing with
                    {
                        Name = name, Profile = profile, Updates = [.. existing.Updates, .. updates], LastAttemptAt = now, LastSuccessAt = now,
                        Explanation = response.Explanation
                    };
                projects = [.. projects.Where(item => item.AccountId != credential.Account.Id || item.ProjectId != id), project];
                result = id;
            }
            var entry = new GmailThreadState(threadId, historyId, lastMessageId, lastSentAt, result, now);
            var updated = mail with
            {
                Processed = Prune([.. (mail.Processed ?? []).Where(item => item.Id != threadId), entry]),
                LeaseUntil = run.Leased ? now + LeaseFor : mail.LeaseUntil
            };
            return current with { Projects = projects, GmailAccounts = [.. Others(current, credential.Account.Id, mail.Address), updated] };
        }, ct);
        if (response is not null)
            logger.LogInformation(response.Decision == EmailSteps.Skip ? "A Gmail thread was analyzed: not research work." :
                "A Gmail thread was analyzed ({Decision} workstream) with {Count} new Update(s).", response.Decision, response.Updates.Count);
        return result;
    }

    /// <summary>The account's workstreams the thread may continue: where it went before, then those sharing the most words.</summary>
    private static IReadOnlyList<EmailWorkstreamCandidate> Candidates(LocalResearchState state, Guid account, string address, string? previous, string text)
    {
        var words = Words(text);
        return [.. state.Projects.Where(item => item.AccountId == account && item.Source == ResearchSources.Gmail &&
                string.Equals(item.Parent, address, StringComparison.OrdinalIgnoreCase) && item.Profile is not null && ProfileEvidence.ValidProfile(item.Profile))
            .Select(item => (Project: item, Score: item.ProjectId == previous ? int.MaxValue : Words(Describe(item.Profile!)).Count(words.Contains)))
            .OrderByDescending(item => item.Score).ThenByDescending(item => item.Project.LastSuccessAt)
            .Take(MaxCandidates).Select(item => new EmailWorkstreamCandidate(item.Project.ProjectId, item.Project.Profile!))];
    }

    private static string Describe(ProjectProfile profile) =>
        string.Join('\n', [profile.Title, profile.Description, .. profile.Contributions, .. profile.Topics]);

    [GeneratedRegex(@"\p{L}{4,}")]
    private static partial Regex Word();
    private static HashSet<string> Words(string text) =>
        Word().Matches(text).Select(match => match.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    private bool Blocked(string address, DateTimeOffset now) => _retryAt.TryGetValue(address, out var until) && until > now;

    private bool Due(GmailAccountState mail, DateTimeOffset now) =>
        _requested.ContainsKey(mail.Address) || mail.LastImportAt is null || now >= NextImportAt(mail);

    private static IEnumerable<GmailAccountState> Eligible(LocalResearchState state, Guid account) =>
        (state.GmailAccounts ?? []).Where(item => item.AccountId == account && !item.Removed && item.RefreshToken is not null &&
            item.ConsentVersion == GmailConnector.ConsentVersion && item.Status != "reconnect");

    private static GmailAccountState? Find(LocalResearchState state, Guid account, string address) =>
        (state.GmailAccounts ?? []).FirstOrDefault(item => item.AccountId == account && !item.Removed && item.RefreshToken is not null &&
            string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<GmailAccountState> Others(LocalResearchState state, Guid account, string address) =>
        (state.GmailAccounts ?? []).Where(item => item.AccountId != account || !string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));

    private Task ChangeAccountAsync(LinkedCredential credential, string address, Func<GmailAccountState, GmailAccountState> change, CancellationToken ct) =>
        store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != credential.Account.Id) throw StateChanged();
            var mail = Find(current, credential.Account.Id, address) ?? throw StateChanged();
            return current with { GmailAccounts = [.. Others(current, credential.Account.Id, address), change(mail)] };
        }, ct);

    /// <summary>This computer's id for import leases (random, kept in the encrypted state).</summary>
    private async Task<string> DeviceAsync(CancellationToken ct)
    {
        if (_device is not null) return _device;
        var state = await store.ReadAsync(ct);
        if (state.DeviceId is null)
            state = await store.ChangeAsync(current => current.DeviceId is null
                ? current with { DeviceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) } : current, ct);
        return _device = state.DeviceId!;
    }

    private static EmailMessage Email(GmailMessage message) => new(message.Id, message.From, message.SentAt, message.FromUser, message.Text);
    private static string Revision(string threadId, string messageId) => $"gmail:{threadId}:{messageId}";
    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
    private static ResearchFailure StateChanged() => new(409, "research_state_changed", "The account or Gmail account changed. Uncommitted results were discarded.");
}
