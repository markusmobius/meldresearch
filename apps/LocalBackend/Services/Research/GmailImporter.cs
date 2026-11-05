using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Imports connected Gmail accounts, run by the research worker a few threads at a time. About once an hour (with a
/// per-account offset of up to ten minutes) it lists the threads the user wrote in during the import window and reads only
/// the threads with new mail, oldest first, and of those only the new messages (earlier ones are background). A thread seen
/// for the first time is placed by Foundry: skipped (not work), put on one of the user's research projects, or left in
/// Housekeeping (with a short preview), grouped loosely with related waiting threads (<see cref="Housekeeping"/>), where the
/// user decides on a whole group; no project is created without the user. A thread on a
/// project is a source of that project's details (OVERLEAF.md §12): its new messages are one update step. Quotes are checked
/// against the email text. Each step commits on its own, so a restart continues where it stopped, and an unchanged mailbox
/// makes no model call. With encrypted sync the threads are synced, and a lease in the synced profile makes other computers
/// wait while one imports. Threads the user skips join skipped work (About me → Skipped), grouped generously.
/// </summary>
internal sealed class GmailImporter(IResearchStore store, IResearchRemote remote, GmailConnector connector, IGmailGoogle google,
    TimeProvider clock, ILogger<GmailImporter> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1), Jitter = TimeSpan.FromMinutes(10), LeaseFor = TimeSpan.FromMinutes(15),
        RetryAfter = TimeSpan.FromMinutes(2);
    public const int ThreadsPerStep = 3, FailuresBeforeSkip = 3, MaxSkipped = 2000, MaxFeedThreads = 150;
    /// <summary>The thread analysis in use: state from an earlier one is replaced once and mail is imported again.</summary>
    public const int Version = 2;
    /// <summary>Characters of new messages per request; the rest of the thread limit is left for earlier messages.</summary>
    public const int MessageBudget = 32000;

    private sealed class Run(string address, IReadOnlyList<GmailThreadRef> threads, bool leased)
    {
        public string Address { get; } = address;
        public Queue<GmailThreadRef> Queue { get; } = new(threads);
        public int Total { get; } = threads.Count;
        public int Done { get; set; }
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

    public bool HasWork(LocalResearchState state, Guid account, DateTimeOffset now) =>
        state.GmailVersion < Version ||
        Eligible(state, account).Any(mail => !Blocked(mail.Address, now) && (_runs.ContainsKey(mail.Address) || Due(mail, now))) ||
        SkipDue(state, account, now) is not null || FoldDue(state, account, now) is not null || PeopleDue(state, account, now) is not null ||
        HousekeepingDue(state, account, now);

    /// <summary>Housekeeping work: tidy groups, group a waiting thread, compare groups once more, or check a group against the projects.</summary>
    private bool HousekeepingDue(LocalResearchState state, Guid account, DateTimeOffset now) =>
        Housekeeping.Cleaned(state, account) is not null || !Blocked(RetitleKey, now) && Housekeeping.RetitleDue(state, account).Count != 0 ||
        Housekeeping.GroupDue(state, account, item => !Blocked(GroupKey(item), now)) is not null ||
        !Blocked(RegroupKey, now) && Housekeeping.RegroupDue(state, account) ||
        Housekeeping.SuggestDue(state, account, group => !Blocked(SuggestKey(group), now)) is not null;

    private static string GroupKey(GmailThreadRecord item) => $"group|{item.Address}|{item.Id}";
    private const string RegroupKey = "regroup", RetitleKey = "retitle";
    private static string SuggestKey(HousekeepingGroup group) => $"suggest|{group.Id}";

    /// <summary>
    /// Continues the running import (or starts one that is due) for one account by up to <see cref="ThreadsPerStep"/> threads,
    /// or else folds one assigned thread's new mail into its card's details. Returns whether Gmail work remains that can
    /// continue straight away.
    /// </summary>
    public async Task<bool> StepAsync(LinkedCredential credential, ResearchSync? sync, CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        var account = credential.Account.Id;
        if (state.GmailVersion < Version)
        {
            await UpgradeAsync(ct);
            return true;
        }
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
        if (SkipDue(state, account, now) is { } skipped)
        {
            try { await GroupSkippedAsync(credential, skipped, ct); }
            catch (ResearchFailure failure) when (failure.Code != "research_state_changed")
            {
                _retryAt[SkipKey(skipped)] = clock.GetUtcNow() + RetryAfter;
                logger.LogWarning("Grouping a skipped Gmail thread failed ({Code}); it is retried shortly.", failure.Code);
            }
            return HasWork(await store.ReadAsync(ct), account, clock.GetUtcNow());
        }
        if (FoldDue(state, account, now) is { } fold)
        {
            try { await FoldPendingAsync(credential, fold, ct); }
            catch (Exception error) when (error is ResearchFailure { Code: not "research_state_changed" } or HttpRequestException ||
                error is TaskCanceledException && !ct.IsCancellationRequested)
            {
                _retryAt[FoldKey(fold)] = clock.GetUtcNow() + RetryAfter;
                logger.LogWarning("Adding a Gmail thread to its project failed ({Type}); it is retried shortly.", error is ResearchFailure failure ? failure.Code : error.GetType().Name);
            }
            return HasWork(await store.ReadAsync(ct), account, clock.GetUtcNow());
        }
        if (PeopleDue(state, account, now) is { } unread)
        {
            try { await ReadPeopleAsync(credential, unread, ct); }
            catch (Exception error) when (error is ResearchFailure { Code: not "research_state_changed" } or HttpRequestException ||
                error is TaskCanceledException && !ct.IsCancellationRequested)
            {
                _retryAt[PeopleKey(unread)] = clock.GetUtcNow() + RetryAfter;
                logger.LogWarning("Reading who is on a Gmail thread failed ({Type}); it is retried shortly.", error is ResearchFailure failure ? failure.Code : error.GetType().Name);
            }
            return HasWork(await store.ReadAsync(ct), account, clock.GetUtcNow());
        }
        if (Housekeeping.Cleaned(state, account) is not null)
        {
            await store.ChangeAsync(current => current.Credential?.Account.Id == account && Housekeeping.Cleaned(current, account) is { } cleaned ? cleaned : current, ct);
            return HasWork(await store.ReadAsync(ct), account, clock.GetUtcNow());
        }
        string? key = null;
        try
        {
            if (!Blocked(RetitleKey, now) && Housekeeping.RetitleDue(state, account) is { Count: > 0 } untitled)
            {
                key = RetitleKey;
                await RetitleAsync(credential, untitled, ct);
            }
            else if (Housekeeping.GroupDue(state, account, item => !Blocked(GroupKey(item), now)) is { } waiting)
            {
                key = GroupKey(waiting);
                await GroupWaitingAsync(credential, waiting, ct);
            }
            else if (!Blocked(RegroupKey, now) && Housekeeping.RegroupDue(state, account))
            {
                key = RegroupKey;
                await RegroupWaitingAsync(credential, ct);
            }
            else if (Housekeeping.SuggestDue(state, account, group => !Blocked(SuggestKey(group), now)) is { } group)
            {
                key = SuggestKey(group);
                await DescribeAsync(credential, group, ct);
            }
            else return false;
        }
        catch (Exception error) when (error is ResearchFailure { Code: not "research_state_changed" } or HttpRequestException ||
            error is TaskCanceledException && !ct.IsCancellationRequested)
        {
            _retryAt[key!] = clock.GetUtcNow() + RetryAfter;
            logger.LogWarning("Housekeeping grouping failed ({Type}); it is retried shortly.", error is ResearchFailure failure ? failure.Code : error.GetType().Name);
        }
        return HasWork(await store.ReadAsync(ct), account, clock.GetUtcNow());
    }

    /// <summary>
    /// Writes short headlines for up to 12 waiting threads in one call (from their subjects and latest points), so
    /// Housekeeping shows what each thread is about rather than a vague subject. A thread is titled again only when new mail
    /// arrives or the title style changes.
    /// </summary>
    private async Task RetitleAsync(LinkedCredential credential, IReadOnlyList<GmailThreadRecord> threads, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var request = new ThreadTitlesRequest([.. threads.Select(item => new WaitingThread(Trim(item.Subject is { Length: > 0 } subject ? subject : "(no subject)", 500),
            [.. item.Bullets.TakeLast(Housekeeping.PointsPerThread).Select(bullet => Trim(bullet.Text, 1000)).Where(text => text.Trim().Length != 0)]))]);
        var titles = (await remote.TitleThreadsAsync(credential.Token, request, ct)).Titles;
        if (titles.Count != threads.Count) throw new ResearchFailure(502, "invalid_thread_titles", "Foundry returned unsupported thread titles.");
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account) return current;
            var now = clock.GetUtcNow();
            var written = threads.Select((thread, index) => (Key: Housekeeping.Key(thread), Message: thread.LastMessageId, Title: titles[index]))
                .ToDictionary(item => item.Key, StringComparer.Ordinal);
            return current with
            {
                GmailThreads = [.. current.GmailThreads!.Select(item => item.AccountId == account && item.Status == GmailThreadRecord.Pending &&
                    written.TryGetValue(Housekeeping.Key(item), out var title) && title.Message == item.LastMessageId
                    ? item with { Title = title.Title, TitledFor = Housekeeping.TitleKey(item), UpdatedAt = now } : item)]
            };
        }, ct);
        logger.LogInformation("Titled {Count} waiting Gmail thread(s).", threads.Count);
    }

    /// <summary>
    /// Puts a waiting thread in a Housekeeping group: the one Foundry finds related (loosely: the same topic, people or kind
    /// of work), else a new group named after the project the thread's first check suggested. No call when there are no
    /// groups yet.
    /// </summary>
    private async Task GroupWaitingAsync(LinkedCredential credential, GmailThreadRecord thread, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        var groups = Housekeeping.Groups(state, account).Where(group => Housekeeping.Members(state, group).Count != 0)
            .OrderByDescending(group => group.UpdatedAt).Take(ResearchThemes.MaxCandidates).ToArray();
        string name = Housekeeping.NameOf(thread);
        var match = groups.Length == 0 ? null : await remote.MatchThemeAsync(credential.Token,
            new(name, Housekeeping.Profile(name, [thread]), [.. groups.Select(group => Housekeeping.Candidate(state, group))], Waiting: true), ct);
        bool joined = false;
        await store.ChangeAsync(current =>
        {
            var item = Item(current, account, thread.Address, thread.Id);
            if (current.Credential?.Account.Id != account || item is not { Status: GmailThreadRecord.Pending, GroupId: null }) return current;
            var now = clock.GetUtcNow();
            var all = (current.HousekeepingGroups ?? []).ToList();
            int index = match?.ThemeId is { } id ? all.FindIndex(group => group.AccountId == account && group.Id == id) : -1;
            HousekeepingGroup target;
            if (index >= 0) all[index] = target = all[index] with { UpdatedAt = now };
            else all.Add(target = Housekeeping.NewGroup(account, name, now));
            joined = index >= 0;
            item = item with { GroupId = target.Id, UpdatedAt = now };
            var next = current with { HousekeepingGroups = all, GmailThreads = [.. Others(current, account, item.Address, item.Id), item] };
            return Housekeeping.Cleaned(next, account) ?? next;
        }, ct);
        logger.LogInformation(joined ? "A waiting Gmail thread joined a related Housekeeping group." : "A waiting Gmail thread starts a Housekeeping group.");
    }

    /// <summary>
    /// Compares the Housekeeping groups once more (groups form one thread at a time) and combines related ones into the group
    /// with the most threads. Groups the user made, renamed or arranged are left out; groups that changed meanwhile are not
    /// combined and are compared again next time.
    /// </summary>
    private async Task RegroupWaitingAsync(LinkedCredential credential, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        var loose = Housekeeping.Loose(state, account).Where(group => Housekeeping.Members(state, group).Count != 0)
            .OrderByDescending(group => group.UpdatedAt).Take(ResearchThemes.MaxCandidates).ToArray();
        var seen = loose.ToDictionary(group => group.Id, group => Housekeeping.Members(state, group).Select(Housekeeping.Key).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        IReadOnlyList<ThemeGroup> sets = loose.Length < 2 ? [] : (await remote.RegroupThemesAsync(credential.Token,
            new([.. loose.Select(group => Housekeeping.Candidate(state, group))], Waiting: true), ct)).Groups;
        int combined = 0;
        await store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != account) return current;
            var now = clock.GetUtcNow();
            bool Unchanged(LocalResearchState at, HousekeepingGroup group) => seen.TryGetValue(group.Id, out var keys) && !Housekeeping.Held(at, group) &&
                Housekeeping.Members(at, group).Select(Housekeeping.Key).ToHashSet(StringComparer.Ordinal).SetEquals(keys);
            var next = current;
            foreach (var set in sets)
            {
                var members = Housekeeping.Groups(next, account).Where(group => set.CardIds.Contains(group.Id) && Unchanged(next, group)).ToArray();
                if (members.Length < 2) continue;
                var target = members.OrderByDescending(group => Housekeeping.Members(next, group).Count).ThenBy(group => group.CreatedAt).First();
                var others = members.Where(group => group != target).Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
                // The target now holds the others' threads; it counts as seen with all of them.
                seen[target.Id] = [.. members.SelectMany(group => seen[group.Id])];
                next = next with
                {
                    GmailThreads = [.. next.GmailThreads!.Select(item => item.AccountId == account && item.GroupId is { } id && others.Contains(id)
                        ? item with { GroupId = target.Id, UpdatedAt = now } : item)],
                    HousekeepingGroups = [.. next.HousekeepingGroups!.Where(group => group.AccountId != account || !others.Contains(group.Id))
                        .Select(group => group.AccountId == account && group.Id == target.Id ? group with { UpdatedAt = now } : group)]
                };
                combined += others.Count;
            }
            // Groups compared and unchanged meanwhile are not compared again until they gain threads; combined groups take a provisional name.
            next = next with
            {
                HousekeepingGroups = [.. next.HousekeepingGroups!.Select(group => group.AccountId == account && Unchanged(next, group)
                    ? group with { Checked = [.. Housekeeping.Members(next, group).Select(Housekeeping.Key).Order(StringComparer.Ordinal)] } : group)]
            };
            return Housekeeping.Cleaned(next, account) ?? next;
        }, ct);
        logger.LogInformation("Compared the Housekeeping groups: {Count} combined.", combined);
    }

    /// <summary>
    /// One call per changed Housekeeping group: a headline written from all its threads (replacing the provisional name,
    /// unless the user named the group) and a strict comparison with the research projects, whose match is only suggested;
    /// the user confirms it. No call for a group the user named when there are no projects.
    /// </summary>
    private async Task DescribeAsync(LinkedCredential credential, HousekeepingGroup group, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        string print = Housekeeping.Fingerprint(state, group), members = Housekeeping.MembersPrint(state, group);
        var threads = Housekeeping.Members(state, group);
        var cards = Cards(state, account);
        var described = threads.Count == 0 || group.Named == true && cards.Count == 0 ? null
            : await remote.DescribeWaitingGroupAsync(credential.Token, new(group.Name, Housekeeping.Threads(threads), cards), ct);
        await store.ChangeAsync(current =>
        {
            var item = Housekeeping.Groups(current, account).FirstOrDefault(candidate => candidate.Id == group.Id);
            if (current.Credential?.Account.Id != account || item is null || Housekeeping.Fingerprint(current, item) != print) return current;
            string? cardId = described?.ProjectId;
            var updated = item with
            {
                Name = item.Named != true && described is not null ? described.Headline : item.Name, NamedFor = members,
                SuggestedCardId = cardId, SuggestionReason = cardId is null ? null : Trim(described!.Explanation, 300), SuggestedFor = print,
                UpdatedAt = clock.GetUtcNow()
            };
            return current with { HousekeepingGroups = [.. current.HousekeepingGroups!.Select(candidate => candidate == item ? updated : candidate)] };
        }, ct);
        logger.LogInformation(described?.ProjectId is null ? "A Housekeeping group was named; it looks like none of the research projects." :
            "A Housekeeping group was named; it looks like an existing research project, which is suggested.");
    }

    /// <summary>
    /// Replaces what an earlier analysis made from Gmail (one workstream per line of work, with separate Updates): those
    /// workstreams are excluded everywhere, so synced copies are removed too, and the mail is imported again as threads.
    /// </summary>
    private async Task UpgradeAsync(CancellationToken ct)
    {
        int removed = 0;
        await store.ChangeAsync(current =>
        {
            if (current.GmailVersion >= Version) return current;
            var now = clock.GetUtcNow();
            var earlier = current.Projects.Where(project => project.Source == ResearchSources.Gmail).ToArray();
            removed = earlier.Length;
            var decided = (current.Decisions ?? []).Select(decision => (decision.AccountId, decision.ProjectId)).ToHashSet();
            return current with
            {
                Projects = [.. current.Projects.Where(project => project.Source != ResearchSources.Gmail)],
                Decisions = [.. (current.Decisions ?? []).Select(decision => earlier.Any(project => project.AccountId == decision.AccountId &&
                        project.ProjectId == decision.ProjectId) ? decision with { Included = false, DecidedAt = now } : decision),
                    .. earlier.Where(project => !decided.Contains((project.AccountId, project.ProjectId)))
                        .Select(project => new ProjectDecision(project.AccountId, project.ProjectId, false, now))],
                GmailThreads = null,
                GmailAccounts = current.GmailAccounts?.Select(item => item with { Processed = null }).ToArray(),
                GmailVersion = Version
            };
        }, ct);
        _runs.Clear();
        if (removed != 0) logger.LogInformation("Gmail analysis upgraded: {Count} earlier workstream(s) replaced; mail is imported again as threads.", removed);
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
        var known = (state.GmailThreads ?? []).Where(item => item.AccountId == credential.Account.Id && Same(item.Address, mail.Address))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        // The listing is newest first; mail is analyzed in the order it was written.
        var changed = threads.Where(thread => !known.TryGetValue(thread.Id, out var item) || item.HistoryId != thread.HistoryId).Reverse().ToArray();
        await ChangeAccountAsync(credential, mail.Address, item => item with { Threads = threads.Count, CheckedAt = now, Status = "connected", Error = null }, ct);
        var run = new Run(mail.Address, changed, sync is not null);
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
            LastImportAt = now, ImportVersion = Version,
            LeaseBy = item.LeaseBy == device ? null : item.LeaseBy,
            LeaseUntil = item.LeaseBy == device ? null : item.LeaseUntil
        }, ct);
        _runs.TryRemove(run.Address, out _);
        logger.LogInformation("Gmail import finished: {Threads} thread(s) with new mail analyzed.", run.Total);
    }

    /// <summary>Analyzes one thread's new messages (in parts if there are many) and commits each part.</summary>
    private async Task ThreadAsync(LinkedCredential credential, Run run, GmailThreadRef reference, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        var mail = Find(state, account, run.Address) ?? throw StateChanged();
        var item = Item(state, account, run.Address, reference.Id);
        if (item?.Status == GmailThreadRecord.Dismissed || item?.Status == GmailThreadRecord.Assigned && OnSkippedCard(state, account, item.CardId))
        {
            // The user skipped this thread (or its project): its new mail is not read.
            await CommitAsync(credential, run, reference.Id, item.Subject, reference.HistoryId, item.LastMessageId, item.LastSentAt, null, null, ct);
            return;
        }
        var thread = await connector.WithAccessAsync(credential, mail, token => google.ThreadAsync(token, reference.Id, ct), ct);
        string self = mail.Address.ToLowerInvariant();
        var people = CollaboratorPlan.PeopleOf(thread, Addresses(state, account));
        // Mailing lists and automated mail count only when the user wrote them.
        var usable = thread.Messages.Where(message => message.Text.Length != 0 && EmailSteps.IsGmailId(message.Id))
            .Select(message => message with
            {
                From = Trim(message.From.Length == 0 ? "(unknown sender)" : message.From, 320),
                FromUser = message.FromUser || EmailText.Address(message.From) == self
            })
            .Where(message => !message.Bulk || message.FromUser).ToList();
        string subject = Trim(thread.Subject, 300);
        var since = clock.GetUtcNow() - TimeSpan.FromDays(GmailConnector.WindowDays);
        int first = item is null ? usable.FindIndex(message => message.SentAt >= since) : usable.FindIndex(message => message.SentAt > item.LastSentAt);
        if (first < 0 || !usable.Any(message => message.FromUser))
        {
            var last = thread.Messages.LastOrDefault();
            await CommitAsync(credential, run, thread.Id, subject, thread.HistoryId, last?.Id ?? item?.LastMessageId ?? "",
                last?.SentAt ?? item?.LastSentAt ?? clock.GetUtcNow(), null, null, ct, people);
            return;
        }
        if (item is { Status: GmailThreadRecord.Assigned, CardId: { } card })
        {
            // A thread on a project: its new messages update the project's details.
            var latest = thread.Messages[^1];
            await CommitAsync(credential, run, thread.Id, subject, thread.HistoryId, latest.Id, latest.SentAt, null, null, ct, people);
            await FoldAsync(credential, run.Address, thread.Id, subject, usable, usable.FindIndex(message => message.SentAt > (item.FoldedSentAt ?? since)),
                card, ct);
            return;
        }
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
            state = await store.ReadAsync(ct);
            item = Item(state, account, run.Address, thread.Id);
            bool known = item?.Status is GmailThreadRecord.Pending;
            var request = new EmailStepRequest(thread.Id, subject, self, [.. part.Select(Email)], [.. context.Select(Email)], known,
                known ? [.. item!.Bullets.TakeLast(EmailSteps.MaxKnownBullets).Select(bullet => bullet.Text)] : [], null,
                known ? [] : Cards(state, account), known ? null : NotMine(state, account, ResearchThemes.MaxCandidates - Cards(state, account).Count));
            EmailSteps.ValidateRequest(request);
            var response = await remote.EmailStepAsync(credential.Token, request, ct);
            EmailSteps.ValidateResponse(request, response);
            // A thread analyzed in parts keeps an unfinished marker until its last part, so a restart continues it.
            await CommitAsync(credential, run, thread.Id, subject, first >= usable.Count ? thread.HistoryId : "", part[^1].Id, part[^1].SentAt,
                request, response, ct, people);
            // Placed on a project: everything it says so far updates the project's details.
            if (Item(await store.ReadAsync(ct), account, run.Address, thread.Id) is { Status: GmailThreadRecord.Assigned, CardId: { } placed })
            {
                await FoldAsync(credential, run.Address, thread.Id, subject, usable, usable.FindIndex(message => message.SentAt >= since), placed, ct);
                return;
            }
        }
    }

    /// <summary>
    /// Updates a research project's details from a thread's messages from <paramref name="first"/> on (OVERLEAF.md §12), in
    /// parts: each part is one step whose edits, the thread's folded message and a changelog entry commit together.
    /// </summary>
    private async Task FoldAsync(LinkedCredential credential, string address, string threadId, string subject, IReadOnlyList<GmailMessage> usable,
        int first, string cardId, CancellationToken ct)
    {
        var account = credential.Account.Id;
        if (first < 0) first = usable.Count;
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
            var known = await store.ReadAsync(ct);
            var card = ThemePlan.Themes(known, account).FirstOrDefault(theme => theme.Id == cardId);
            if (card is null) return;
            // Who each update rests on: the senders of the messages it cites (the user for their own).
            var self = CollaboratorPlan.Self(known, account);
            var senders = part.ToDictionary(message => message.Id, message => CollaboratorPlan.SenderKey(self, message), StringComparer.Ordinal);
            var everyone = senders.Values.Where(key => key.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
            var request = new DetailStepRequest(DetailSource.Gmail, Trim(card.Profile?.Title ?? card.Name ?? subject, ProfileEvidence.HardMaxTitle), null, null,
                DetailPlan.Inputs(card.Details ?? []), [], [], subject, [.. part.Select(Detail)], [.. context.Select(Detail)]);
            DetailRules.ValidateRequest(request);
            var response = await remote.DetailStepAsync(credential.Token, request, ct);
            DetailRules.ValidateResponse(request, response);
            var sent = usable.ToDictionary(message => message.Id, message => message.SentAt, StringComparer.Ordinal);
            var last = part[^1];
            await store.ChangeAsync(current =>
            {
                var theme = current.Themes?.FirstOrDefault(item => item.AccountId == account && item.Id == cardId);
                var thread = Item(current, account, address, threadId);
                if (current.Credential?.Account.Id != account || theme is null || thread is not { Status: GmailThreadRecord.Assigned } || thread.CardId != cardId)
                    throw StateChanged();
                var now = clock.GetUtcNow();
                var (details, changes) = DetailPlan.Apply(theme.Details ?? [], response,
                    quote => new DetailSource(DetailSource.Gmail, thread.Address, threadId, quote.Item, subject, sent.GetValueOrDefault(quote.Item)),
                    quote => senders.TryGetValue(quote.Item, out var key) && key.Length != 0 ? [key] : [], everyone);
                return current with
                {
                    Themes = [.. current.Themes!.Select(item => item == theme ? theme with { Details = details } : item)],
                    GmailThreads = [.. Others(current, account, thread.Address, threadId), thread with
                    {
                        FoldedMessageId = first >= usable.Count ? thread.LastMessageId : last.Id,
                        FoldedSentAt = last.SentAt, UpdatedAt = now
                    }],
                    Changelog = DetailPlan.Log(current.Changelog, new(Guid.NewGuid(), account, cardId, last.SentAt, ChangelogEntry.Gmail, subject, null,
                        [subject], changes, null, now))
                };
            }, ct);
            logger.LogInformation("A Gmail thread updated its project with {Count} change(s).", response.Ops.Count);
        }
    }

    private static DetailMessage Detail(GmailMessage message) => new(message.Id, message.From, message.SentAt, message.FromUser, message.Text);

    private static string FoldKey(GmailThreadRecord item) => $"fold|{item.Address}|{item.Id}";

    /// <summary>A thread put on a project (by Foundry or the user) whose messages have not updated it yet.</summary>
    private GmailThreadRecord? FoldDue(LocalResearchState state, Guid account, DateTimeOffset now) =>
        (state.GmailThreads ?? []).Where(item => item.AccountId == account && item.Status == GmailThreadRecord.Assigned && item.CardId is { } card &&
            item.FoldedMessageId != item.LastMessageId && !_runs.ContainsKey(item.Address) && !Blocked(FoldKey(item), now) &&
            ThemePlan.Themes(state, account).Any(theme => theme.Id == card) && Find(state, account, item.Address) is not null)
            .OrderBy(item => item.UpdatedAt).FirstOrDefault();

    private static string PeopleKey(GmailThreadRecord item) => $"people|{item.Address}|{item.Id}";

    /// <summary>
    /// A thread on a project of the profile whose people were never recorded, or recorded without who sent which message (it
    /// was read before collaborators were kept): it is read once more for its senders and recipients (no model call).
    /// </summary>
    internal GmailThreadRecord? PeopleDue(LocalResearchState state, Guid account, DateTimeOffset now) =>
        (state.GmailThreads ?? []).FirstOrDefault(item => item.AccountId == account && item.Status == GmailThreadRecord.Assigned && CollaboratorPlan.PeopleUnread(item) &&
            item.CardId is { } card && item.FoldedMessageId == item.LastMessageId && !_runs.ContainsKey(item.Address) && !Blocked(PeopleKey(item), now) &&
            ThemePlan.Active(state, account).Any(theme => theme.Id == card) && Eligible(state, account).Any(mail => Same(mail.Address, item.Address)));

    private async Task ReadPeopleAsync(LinkedCredential credential, GmailThreadRecord item, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        var mail = Find(state, account, item.Address) ?? throw StateChanged();
        IReadOnlyList<MailPerson> people;
        try { people = CollaboratorPlan.PeopleOf(await connector.WithAccessAsync(credential, mail, token => google.ThreadAsync(token, item.Id, ct), ct), Addresses(state, account)); }
        catch (ResearchFailure failure) when (failure.Code == "gmail_thread_missing") { people = []; }
        await store.ChangeAsync(current => Item(current, account, item.Address, item.Id) is { } read && CollaboratorPlan.PeopleUnread(read)
            ? current with { GmailThreads = [.. Others(current, account, item.Address, item.Id), read with { People = people }] } : current, ct);
    }

    /// <summary>The user's own Gmail addresses (lower case): never their own collaborators.</summary>
    private static IReadOnlySet<string> Addresses(LocalResearchState state, Guid account) =>
        (state.GmailAccounts ?? []).Where(mail => mail.AccountId == account && !mail.Removed).Select(mail => mail.Address.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>Reads a thread put on a project and updates the project's details from its messages not folded yet.</summary>
    private async Task FoldPendingAsync(LinkedCredential credential, GmailThreadRecord item, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var mail = Find(await store.ReadAsync(ct), account, item.Address) ?? throw StateChanged();
        GmailThread thread;
        try { thread = await connector.WithAccessAsync(credential, mail, token => google.ThreadAsync(token, item.Id, ct), ct); }
        catch (ResearchFailure failure) when (failure.Code == "gmail_thread_missing")
        {
            await store.ChangeAsync(current => Item(current, account, item.Address, item.Id) is { } gone
                ? current with { GmailThreads = [.. Others(current, account, item.Address, item.Id), gone with { FoldedMessageId = gone.LastMessageId }] } : current, ct);
            return;
        }
        string self = mail.Address.ToLowerInvariant();
        var people = CollaboratorPlan.PeopleOf(thread, Addresses(await store.ReadAsync(ct), account));
        await store.ChangeAsync(current => Item(current, account, item.Address, item.Id) is { } read
            ? current with { GmailThreads = [.. Others(current, account, item.Address, item.Id), read with { People = people }] } : current, ct);
        var usable = thread.Messages.Where(message => message.Text.Length != 0 && EmailSteps.IsGmailId(message.Id))
            .Select(message => message with { From = Trim(message.From.Length == 0 ? "(unknown sender)" : message.From, 320),
                FromUser = message.FromUser || EmailText.Address(message.From) == self })
            .Where(message => !message.Bulk || message.FromUser).Where(message => message.SentAt <= item.LastSentAt).ToList();
        var since = clock.GetUtcNow() - TimeSpan.FromDays(GmailConnector.WindowDays);
        int first = usable.FindIndex(message => message.SentAt > (item.FoldedSentAt ?? since));
        if (first >= 0) await FoldAsync(credential, item.Address, item.Id, Trim(thread.Subject, 300), usable, first, item.CardId!, ct);
        // Nothing (more) to read: the thread counts as folded.
        await store.ChangeAsync(current => Item(current, account, item.Address, item.Id) is { Status: GmailThreadRecord.Assigned } done && done.CardId == item.CardId
            ? current with { GmailThreads = [.. Others(current, account, item.Address, item.Id), done with { FoldedMessageId = done.LastMessageId }] } : current, ct);
    }

    private async Task PassOverAsync(LinkedCredential credential, Run run, GmailThreadRef reference, CancellationToken ct)
    {
        var item = Item(await store.ReadAsync(ct), credential.Account.Id, run.Address, reference.Id);
        await CommitAsync(credential, run, reference.Id, item?.Subject ?? "(no subject)", reference.HistoryId, item?.LastMessageId ?? "",
            item?.LastSentAt ?? clock.GetUtcNow(), null, null, ct);
    }

    /// <summary>
    /// Commits one analyzed part of a thread atomically: its status, placement and preview bullets (an assigned thread's mail
    /// is folded into its card's details afterwards). Nothing is committed if the account changed, the Gmail account was
    /// removed or another computer took over the import.
    /// </summary>
    private async Task CommitAsync(LinkedCredential credential, Run run, string threadId, string subject, string historyId,
        string lastMessageId, DateTimeOffset lastSentAt, EmailStepRequest? request, EmailStepResponse? response, CancellationToken ct,
        IReadOnlyList<MailPerson>? people = null)
    {
        string device = await DeviceAsync(ct);
        var account = credential.Account.Id;
        await store.ChangeAsync(current =>
        {
            var now = clock.GetUtcNow();
            if (current.Credential?.Account.Id != account || current.Credential.Token != credential.Token) throw StateChanged();
            var mail = Find(current, account, run.Address) ?? throw StateChanged();
            if (run.Leased && mail.LeaseBy != device) throw new ResearchFailure(409, "gmail_lease_lost", "Another computer is importing this Gmail account.");
            var item = Item(current, account, mail.Address, threadId)
                ?? new GmailThreadRecord(account, mail.Address, threadId, subject, GmailThreadRecord.Skipped, historyId, lastMessageId, lastSentAt, now, []);
            item = item with { Subject = subject, HistoryId = historyId, LastMessageId = lastMessageId, LastSentAt = lastSentAt, UpdatedAt = now,
                People = people ?? item.People };
            if (response is not null)
            {
                var bullets = response.Bullets.Select(bullet => new GmailBullet(Guid.NewGuid(), bullet.Text, bullet.Evidence,
                    bullet.Evidence.Max(citation => citation.Email!.SentAt), now, response.Newsworthy)).ToArray();
                item = item with { Explanation = response.Explanation, Bullets = [.. item.Bullets, .. bullets] };
                switch (response.Decision)
                {
                    case EmailSteps.Skip:
                        item = item with { Status = GmailThreadRecord.Skipped, Bullets = [], StreamId = null, CardId = null, Headline = null, NewsAt = null };
                        break;
                    case EmailSteps.Project:
                        var card = ThemePlan.Themes(current, account).FirstOrDefault(theme => theme.Id == response.ProjectId);
                        item = card is null
                            ? item with { Status = GmailThreadRecord.Pending, Suggested = request!.Projects.Concat(request.NotMine ?? []).First(project => project.Id == response.ProjectId).Title }
                            : item with { Status = GmailThreadRecord.Assigned, CardId = card.Id, FoldedMessageId = null, FoldedSentAt = null };
                        break;
                    case EmailSteps.Unassigned:
                        item = item with { Status = GmailThreadRecord.Pending, Suggested = response.SuggestedProject };
                        break;
                }
                if (response.Newsworthy) item = item with { Headline = response.Headline, NewsAt = bullets.Max(bullet => bullet.SentAt) };
            }
            current = current with
            {
                GmailThreads = Prune([.. Others(current, account, mail.Address, threadId), item]),
                GmailAccounts = run.Leased
                    ? [.. (current.GmailAccounts ?? []).Where(other => other != mail), mail with { LeaseUntil = now + LeaseFor }]
                    : current.GmailAccounts
            };
            return current;
        }, ct);
        if (response is not null)
            logger.LogInformation("A Gmail thread was analyzed ({Decision}) with {Count} new bullet(s){News}.", response.Decision,
                response.Bullets.Count, response.Newsworthy ? ", newsworthy" : "");
    }

    /// <summary>
    /// The user's choice for a recorded thread: ignore it from now on, put it on a research card, or start a new research
    /// project for it. Email streams that lose or gain the thread are summarized again.
    /// </summary>
    public async Task DecideAsync(GmailThreadDecisionRequest request, CancellationToken ct)
    {
        if (request is null || !EmailSteps.IsGmailId(request.ThreadId) || string.IsNullOrWhiteSpace(request.Address))
            throw new ResearchFailure(400, "invalid_thread_decision", "Choose a thread and what to do with it.");
        await store.ChangeAsync(current =>
        {
            var credential = current.Credential is { } linked && linked.ExpiresAt > clock.GetUtcNow() ? linked
                : throw new ResearchFailure(401, "meld_account_required", "Sign in with Google first.");
            var account = credential.Account.Id;
            var state0 = current;
            var item = Item(current, account, request.Address, request.ThreadId);
            if (item?.Status is not (GmailThreadRecord.Assigned or GmailThreadRecord.Pending or GmailThreadRecord.Dismissed))
                throw new ResearchFailure(404, "gmail_thread_not_found", "This thread is no longer waiting for a decision.");
            var now = clock.GetUtcNow();
            // Leaving its project, the thread's sources leave that project's details too.
            current = Detach(current, account, item, now);
            switch (request.Action)
            {
                case "dismiss":
                    // Grouped with related skipped work next (GroupSkippedAsync).
                    item = item with { Status = GmailThreadRecord.Dismissed, CardId = null, StreamId = null };
                    break;
                case "unskip":
                    if (item.Status != GmailThreadRecord.Dismissed && !OnSkippedCard(current, account, item.CardId) && !OnSkippedCard(state0, account, item.CardId))
                        throw new ResearchFailure(409, "gmail_thread_not_skipped", "This thread is not skipped.");
                    item = item with { Status = GmailThreadRecord.Pending, CardId = null, StreamId = null };
                    break;
                case "project":
                    var card = ThemePlan.Themes(current, account).FirstOrDefault(theme => theme.Id == request.CardId)
                        ?? throw new ResearchFailure(404, "research_card_not_found", "That research project no longer exists.");
                    item = item with { Status = GmailThreadRecord.Assigned, CardId = card.Id, StreamId = null };
                    break;
                case "new":
                    string name = request.Name?.Trim() ?? "";
                    if (name.Length is 0 or > 120) throw new ResearchFailure(400, "invalid_project_name", "Give the new project a name of at most 120 characters.");
                    var created = ThemePlan.NewCard(account, [], now, name: name);
                    current = current with { Themes = [.. current.Themes ?? [], created] };
                    item = item with { Status = GmailThreadRecord.Assigned, CardId = created.Id, StreamId = null };
                    break;
                case "group":
                    // Housekeeping: the user moves a waiting thread to another group, or to a new group of its own. It is pinned there.
                    if (item.Status != GmailThreadRecord.Pending)
                        throw new ResearchFailure(409, "gmail_thread_not_waiting", "Only threads in Housekeeping can be moved between groups.");
                    var all = (current.HousekeepingGroups ?? []).ToList();
                    HousekeepingGroup target;
                    if (request.GroupId is { } groupId)
                    {
                        int index = all.FindIndex(group => group.AccountId == account && group.Id == groupId);
                        if (index < 0) throw new ResearchFailure(404, "housekeeping_group_not_found", "That group no longer exists. Reload and try again.");
                        all[index] = target = all[index] with { UpdatedAt = now };
                    }
                    else
                    {
                        string groupName = request.Name?.Trim() ?? "";
                        if (groupName.Length > Housekeeping.MaxName) throw new ResearchFailure(400, "invalid_group_name", "Give the group a name of at most 120 characters.");
                        target = Housekeeping.NewGroup(account, groupName.Length == 0 ? Housekeeping.NameOf(item) : groupName, now, held: true);
                        // A name the user typed is theirs: never replaced by a headline.
                        if (groupName.Length != 0) target = target with { Named = true };
                        all.Add(target);
                    }
                    current = current with { HousekeepingGroups = all };
                    item = item with { GroupId = target.Id, GroupPinned = true };
                    break;
                default:
                    throw new ResearchFailure(400, "invalid_thread_decision", "Choose a thread and what to do with it.");
            }
            item = item with { UpdatedAt = now, FoldedMessageId = null, FoldedSentAt = null };
            if (request.Action != "group") item = item with { GroupId = null, GroupPinned = null };
            var next = current with { GmailThreads = [.. Others(current, account, item.Address, item.Id), item] };
            return Housekeeping.Cleaned(next, account) ?? next;
        }, ct);
        logger.LogInformation("A Gmail thread was {Action} by the user.", request.Action switch
        {
            "dismiss" => "skipped", "unskip" => "brought back to Housekeeping", "project" => "added to a research project",
            "group" => "moved to another Housekeeping group", _ => "made into a new research project"
        });
    }

    /// <summary>
    /// The user's choice for a whole Housekeeping group: a new research project from all its threads ("new", named Name or
    /// the group's name), adding them all to research project CardId ("project"), or skipping them all ("dismiss": one
    /// skipped project, grouped with related skipped work later). The threads' messages then fill that project's details.
    /// </summary>
    public async Task DecideGroupAsync(string groupId, HousekeepingGroupDecisionRequest request, CancellationToken ct)
    {
        if (!ResearchThemes.IsThemeId(groupId) || request is null)
            throw new ResearchFailure(400, "invalid_group_decision", "Choose a group and what to do with it.");
        int count = 0;
        await store.ChangeAsync(current =>
        {
            var credential = current.Credential is { } linked && linked.ExpiresAt > clock.GetUtcNow() ? linked
                : throw new ResearchFailure(401, "meld_account_required", "Sign in with Google first.");
            var account = credential.Account.Id;
            var group = Housekeeping.Groups(current, account).FirstOrDefault(item => item.Id == groupId);
            var members = group is null ? [] : Housekeeping.Members(current, group);
            if (members.Count == 0) throw new ResearchFailure(404, "housekeeping_group_not_found", "This group is no longer waiting for a decision.");
            var now = clock.GetUtcNow();
            string cardId;
            switch (request.Action)
            {
                case "new":
                    string name = request.Name?.Trim() is { Length: > 0 } chosen ? chosen : group!.Name;
                    if (name.Length > 120) throw new ResearchFailure(400, "invalid_project_name", "Give the new project a name of at most 120 characters.");
                    var created = ThemePlan.NewCard(account, [], now, name: name);
                    current = current with { Themes = [.. current.Themes ?? [], created] };
                    cardId = created.Id;
                    break;
                case "project":
                    cardId = ThemePlan.Active(current, account).FirstOrDefault(theme => theme.Id == request.CardId)?.Id
                        ?? throw new ResearchFailure(404, "research_card_not_found", "That research project no longer exists.");
                    break;
                case "dismiss":
                    var skipped = ThemePlan.NewCard(account, [], now, skipped: true, name: group!.Name);
                    current = current with { Themes = [.. current.Themes ?? [], skipped] };
                    cardId = skipped.Id;
                    break;
                default:
                    throw new ResearchFailure(400, "invalid_group_decision", "Choose a group and what to do with it.");
            }
            var keys = members.Select(Housekeeping.Key).ToHashSet(StringComparer.Ordinal);
            count = keys.Count;
            var next = current with
            {
                GmailThreads = [.. current.GmailThreads!.Select(item => item.AccountId == account && item.Status == GmailThreadRecord.Pending &&
                    keys.Contains(Housekeeping.Key(item))
                    ? item with
                    {
                        Status = GmailThreadRecord.Assigned, CardId = cardId, StreamId = null, GroupId = null, GroupPinned = null,
                        FoldedMessageId = null, FoldedSentAt = null, UpdatedAt = now
                    } : item)]
            };
            return Housekeeping.Cleaned(next, account) ?? next;
        }, ct);
        logger.LogInformation("A Housekeeping group of {Count} thread(s) was {Action} by the user.", count, request.Action switch
        {
            "new" => "made into a new research project", "project" => "added to a research project", _ => "skipped"
        });
    }

    /// <summary>Renames a Housekeeping group; a renamed group keeps its name and is never combined by regrouping.</summary>
    public async Task RenameGroupAsync(string groupId, string? name, CancellationToken ct)
    {
        string chosen = name?.Trim() ?? "";
        if (!ResearchThemes.IsThemeId(groupId) || chosen.Length is 0 or > Housekeeping.MaxName)
            throw new ResearchFailure(400, "invalid_group_name", "Give the group a name of at most 120 characters.");
        await store.ChangeAsync(current =>
        {
            var account = current.Credential?.Account.Id ?? throw new ResearchFailure(401, "meld_account_required", "Sign in with Google first.");
            var group = Housekeeping.Groups(current, account).FirstOrDefault(item => item.Id == groupId)
                ?? throw new ResearchFailure(404, "housekeeping_group_not_found", "That group no longer exists. Reload and try again.");
            var renamed = group with { Name = chosen, Named = true, UpdatedAt = clock.GetUtcNow() };
            return current with { HousekeepingGroups = [.. current.HousekeepingGroups!.Select(item => item == group ? renamed : item)] };
        }, ct);
    }

    /// <summary>The account's Housekeeping groups that hold waiting threads, with the research project each looks like.</summary>
    public static IReadOnlyList<HousekeepingGroupResponse> Groups(LocalResearchState state, Guid? account)
    {
        if (account is not { } id) return [];
        var active = ThemePlan.Active(state, id);
        return [.. Housekeeping.Groups(state, id).Where(group => Housekeeping.Members(state, group).Count != 0).Select(group =>
        {
            var card = group.SuggestedCardId is { } cardId ? active.FirstOrDefault(theme => theme.Id == cardId && theme.Profile is not null) : null;
            return new HousekeepingGroupResponse(group.Id, group.Name, group.Named == true, Housekeeping.Held(state, group), card?.Id,
                card is null ? null : CardTitle(state, card), card is null ? null : group.SuggestionReason);
        })];
    }

    /// <summary>
    /// The account's threads: for the feed, assigned ones with newsworthy progress and those waiting for a decision; for
    /// About me → Skipped, the threads the user skipped (on skipped cards, or still being grouped).
    /// </summary>
    public static IReadOnlyList<GmailThreadResponse> Threads(LocalResearchState state, Guid? account)
    {
        if (account is not { } id) return [];
        var themes = ThemePlan.Themes(state, id);
        GmailThreadResponse Response(GmailThreadRecord item, bool skipped)
        {
            var card = item.CardId is { } id0 ? themes.FirstOrDefault(theme => theme.Id == id0) : null;
            return new GmailThreadResponse(item.Address, item.Id, item.Subject, item.Title ?? item.Headline ?? item.Subject, item.Status, card?.Id,
                card is null ? null : CardTitle(state, card), item.Suggested,
                [.. item.Bullets.Select(bullet => new GmailBulletResponse(bullet.Id, bullet.Text, bullet.SentAt, bullet.Newsworthy, bullet.Evidence))],
                item.LastSentAt, item.NewsAt, skipped, item.Status == GmailThreadRecord.Pending ? item.GroupId : null);
        }
        var mine = (state.GmailThreads ?? []).Where(item => item.AccountId == id).ToArray();
        bool Skipped(GmailThreadRecord item) => item.Status == GmailThreadRecord.Dismissed ||
            item.Status == GmailThreadRecord.Assigned && OnSkippedCard(state, id, item.CardId);
        // Housekeeping (threads waiting for a decision), and the skipped ones for About me → Skipped; threads on projects
        // appear through their projects' changelogs.
        var waiting = mine.Where(item => item.Status == GmailThreadRecord.Pending).OrderByDescending(item => item.LastSentAt).Take(MaxFeedThreads)
            .Select(item => Response(item, false));
        var skippedThreads = mine.Where(Skipped).OrderByDescending(item => item.LastSentAt).Take(MaxFeedThreads).Select(item => Response(item, true));
        return [.. waiting, .. skippedThreads];
    }

    private static bool OnSkippedCard(LocalResearchState state, Guid account, string? cardId) =>
        cardId is not null && ThemePlan.Themes(state, account).Any(theme => theme.Skipped == true && theme.Id == cardId);

    /// <summary>Removes a thread's sources from the details of the project it is on (it is moving, skipped or back in Housekeeping).</summary>
    private static LocalResearchState Detach(LocalResearchState state, Guid account, GmailThreadRecord item, DateTimeOffset now)
    {
        if (item.CardId is not { } cardId || ThemePlan.Themes(state, account).FirstOrDefault(theme => theme.Id == cardId) is not { } card) return state;
        string key = new DetailSource(DetailSource.Gmail, item.Address, item.Id, "", item.Subject, null).Key;
        var (details, changes) = DetailPlan.Without(card.Details ?? [], source => source.Key == key);
        if (changes.Count == 0 && details.Count == (card.Details?.Count ?? 0) &&
            details.Zip(card.Details ?? []).All(pair => pair.First.Citations.Count == pair.Second.Citations.Count)) return state;
        return state with
        {
            Themes = [.. state.Themes!.Select(theme => theme == card ? card with { Details = details } : theme)],
            Changelog = DetailPlan.Log(state.Changelog, new(Guid.NewGuid(), account, cardId, now, ChangelogEntry.User, "Email removed: " + item.Subject,
                null, [item.Subject], changes, null, now))
        };
    }

    private static string SkipKey(GmailThreadRecord item) => $"skip|{item.Address}|{item.Id}";

    /// <summary>A thread the user skipped that is not yet grouped with skipped work.</summary>
    private GmailThreadRecord? SkipDue(LocalResearchState state, Guid account, DateTimeOffset now) =>
        (state.GmailThreads ?? []).Where(item => item.AccountId == account && item.Status == GmailThreadRecord.Dismissed && item.CardId is null &&
            !Blocked(SkipKey(item), now)).OrderBy(item => item.UpdatedAt).FirstOrDefault();

    /// <summary>
    /// Puts a skipped thread with related skipped work: on the skipped card Foundry finds related (generously: the same people
    /// or topic is enough), or on a new skipped card of its own. No confirmation is needed; unskipping undoes it.
    /// </summary>
    private async Task GroupSkippedAsync(LinkedCredential credential, GmailThreadRecord thread, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var state = await store.ReadAsync(ct);
        var others = NotMine(state, account) ?? [];
        string name = Trim(thread.Suggested ?? thread.Headline ?? thread.Subject, 120);
        string description = thread.Bullets.Count == 0 ? thread.Subject : string.Join(' ', thread.Bullets.TakeLast(8).Select(bullet => bullet.Text));
        var profile = new ProjectProfile(name, Trim(description, 1200), [], []);
        var match = others.Count == 0 ? null : await remote.MatchThemeAsync(credential.Token, new(name, profile, others, Loose: true), ct);
        await store.ChangeAsync(current =>
        {
            var now = clock.GetUtcNow();
            var item = Item(current, account, thread.Address, thread.Id);
            if (current.Credential?.Account.Id != account || item is not { Status: GmailThreadRecord.Dismissed, CardId: null }) return current;
            var target = match?.ThemeId is { } id ? ThemePlan.Themes(current, account).FirstOrDefault(theme => theme.Id == id && theme.Skipped == true) : null;
            if (target is null)
            {
                target = ThemePlan.NewCard(account, [], now, skipped: true, name: name);
                current = current with { Themes = [.. current.Themes ?? [], target] };
            }
            // The thread is a source of the skipped project; its messages are folded into it next.
            item = item with { Status = GmailThreadRecord.Assigned, CardId = target.Id, FoldedMessageId = null, FoldedSentAt = null, UpdatedAt = now };
            return current with { GmailThreads = [.. Others(current, account, item.Address, item.Id), item] };
        }, ct);
        logger.LogInformation(match?.ThemeId is null ? "A skipped Gmail thread starts a skipped project of its own." :
            "A skipped Gmail thread was grouped with related skipped work.");
    }

    /// <summary>Skipped threads are kept only so they are not analyzed again; the oldest go first beyond a limit.</summary>
    private static IReadOnlyList<GmailThreadRecord> Prune(IReadOnlyList<GmailThreadRecord> threads)
    {
        var skipped = threads.Where(item => item.Status == GmailThreadRecord.Skipped).ToArray();
        if (skipped.Length <= MaxSkipped) return threads;
        var dropped = skipped.OrderByDescending(item => item.LastSentAt).Skip(MaxSkipped).ToHashSet();
        return [.. threads.Where(item => !dropped.Contains(item))];
    }

    /// <summary>
    /// The user's research cards with summaries (also those the user made for email), as compact descriptors: title, the
    /// summary and the first details.
    /// </summary>
    private static IReadOnlyList<ThemeCandidate> Cards(LocalResearchState state, Guid account) =>
        [.. ThemePlan.Active(state, account).Where(theme => theme.Profile is not null).Take(ResearchThemes.MaxCandidates).Select(Candidate)];

    /// <summary>Cards skipped as not the user's own research; new mail that belongs to one is skipped too.</summary>
    private static IReadOnlyList<ThemeCandidate>? NotMine(LocalResearchState state, Guid account, int max = ResearchThemes.MaxCandidates)
    {
        var cards = ThemePlan.Themes(state, account).Where(theme => theme.Skipped == true && theme.Profile is not null)
            .Take(Math.Max(0, max)).Select(Candidate).ToArray();
        return cards.Length == 0 ? null : cards;
    }

    /// <summary>A compact candidate: the card's summary and its first details, at most <see cref="CandidateCharacters"/>.</summary>
    private static ThemeCandidate Candidate(ResearchTheme theme)
    {
        var profile = theme.Profile!;
        string description = string.Join(' ', new[] { profile.Description }.Concat((theme.Details ?? []).Take(4).Select(detail => detail.Text))
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (description.Length > CandidateCharacters)
        {
            int end = description.LastIndexOf(". ", CandidateCharacters, StringComparison.Ordinal);
            description = end > 100 ? description[..(end + 1)] : description[..CandidateCharacters];
        }
        return new(theme.Id, Trim(profile.Title, ProfileEvidence.HardMaxTitle), description, [.. profile.Topics.Take(8)]);
    }

    private const int CandidateCharacters = 700;

    private static string CardTitle(LocalResearchState state, ResearchTheme card) =>
        ThemePlan.DisplayTitle(card, state.Projects.FirstOrDefault(project => card.Members.Contains(project.ProjectId))?.Name ?? "Research project");

    private bool Blocked(string address, DateTimeOffset now) => _retryAt.TryGetValue(address, out var until) && until > now;

    private bool Due(GmailAccountState mail, DateTimeOffset now) =>
        _requested.ContainsKey(mail.Address) || mail.LastImportAt is null || mail.ImportVersion != Version || now >= NextImportAt(mail);

    private static IEnumerable<GmailAccountState> Eligible(LocalResearchState state, Guid account) =>
        (state.GmailAccounts ?? []).Where(item => item.AccountId == account && !item.Removed && item.RefreshToken is not null &&
            item.ConsentVersion == GmailConnector.ConsentVersion && item.Status != "reconnect");

    private static GmailAccountState? Find(LocalResearchState state, Guid account, string address) =>
        (state.GmailAccounts ?? []).FirstOrDefault(item => item.AccountId == account && !item.Removed && item.RefreshToken is not null &&
            Same(item.Address, address));

    private static GmailThreadRecord? Item(LocalResearchState state, Guid account, string address, string threadId) =>
        (state.GmailThreads ?? []).FirstOrDefault(item => item.AccountId == account && item.Id == threadId && Same(item.Address, address));

    private static IEnumerable<GmailThreadRecord> Others(LocalResearchState state, Guid account, string address, string threadId) =>
        (state.GmailThreads ?? []).Where(item => item.AccountId != account || item.Id != threadId || !Same(item.Address, address));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private Task ChangeAccountAsync(LinkedCredential credential, string address, Func<GmailAccountState, GmailAccountState> change, CancellationToken ct) =>
        store.ChangeAsync(current =>
        {
            if (current.Credential?.Account.Id != credential.Account.Id) throw StateChanged();
            var mail = Find(current, credential.Account.Id, address) ?? throw StateChanged();
            return current with { GmailAccounts = [.. (current.GmailAccounts ?? []).Where(item => item != mail), change(mail)] };
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
    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
    private static ResearchFailure StateChanged() => new(409, "research_state_changed", "The account or Gmail account changed. Uncommitted results were discarded.");
}
