using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Imports connected Gmail accounts, run by the research worker a few threads at a time. About once an hour (with a
/// per-account offset of up to ten minutes) it lists the threads the user wrote in during the import window and analyzes
/// only the threads with new mail, oldest first, and of those only the new messages (earlier ones are background). Each
/// thread is one record whose list of bullets grows as mail arrives. A thread seen for the first time is placed by Foundry:
/// skipped (not work), assigned to one of the user's research cards (its bullets join that card's email stream for this
/// Gmail account), or left unassigned in Private updates, where the user decides; no project is created without the user.
/// A known thread only gains bullets. Quotes are checked against the email text. Each thread is committed on its own, so a
/// restart continues where it stopped, and an unchanged mailbox makes no model call. With encrypted sync the threads are
/// synced, and a lease in the synced profile makes other computers wait while one imports, so mail is not analyzed twice.
/// After an import, each changed email stream is summarized once, so its card's combined summary includes it.
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
    /// <summary>The description of a new email stream until its first summary.</summary>
    public const string Placeholder = "Email threads about this line of work, summarized once the import finishes.";

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
        StreamDue(state, account, now) is not null;

    /// <summary>
    /// Continues the running import (or starts one that is due) for one account by up to <see cref="ThreadsPerStep"/> threads,
    /// or else summarizes one changed email stream. Returns whether Gmail work remains that can continue straight away.
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
        if (StreamDue(state, account, now) is { } stream)
        {
            await SummarizeAsync(credential, stream, ct);
            return HasWork(await store.ReadAsync(ct), account, clock.GetUtcNow());
        }
        return false;
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
        if (item?.Status == GmailThreadRecord.Dismissed)
        {
            // The user chose to ignore this thread: its new mail is not read.
            await CommitAsync(credential, run, reference.Id, item.Subject, reference.HistoryId, item.LastMessageId, item.LastSentAt, null, null, ct);
            return;
        }
        var thread = await connector.WithAccessAsync(credential, mail, token => google.ThreadAsync(token, reference.Id, ct), ct);
        string self = mail.Address.ToLowerInvariant();
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
                last?.SentAt ?? item?.LastSentAt ?? clock.GetUtcNow(), null, null, ct);
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
            bool known = item?.Status is GmailThreadRecord.Assigned or GmailThreadRecord.Pending;
            var request = new EmailStepRequest(thread.Id, subject, self, [.. part.Select(Email)], [.. context.Select(Email)], known,
                known ? [.. item!.Bullets.TakeLast(EmailSteps.MaxKnownBullets).Select(bullet => bullet.Text)] : [],
                known && item!.Status == GmailThreadRecord.Assigned ? Card(state, account, item.StreamId) : null,
                known ? [] : Cards(state, account));
            EmailSteps.ValidateRequest(request);
            var response = await remote.EmailStepAsync(credential.Token, request, ct);
            EmailSteps.ValidateResponse(request, response);
            // A thread analyzed in parts keeps an unfinished marker until its last part, so a restart continues it.
            await CommitAsync(credential, run, thread.Id, subject, first >= usable.Count ? thread.HistoryId : "", part[^1].Id, part[^1].SentAt,
                request, response, ct);
        }
    }

    private async Task PassOverAsync(LinkedCredential credential, Run run, GmailThreadRef reference, CancellationToken ct)
    {
        var item = Item(await store.ReadAsync(ct), credential.Account.Id, run.Address, reference.Id);
        await CommitAsync(credential, run, reference.Id, item?.Subject ?? "(no subject)", reference.HistoryId, item?.LastMessageId ?? "",
            item?.LastSentAt ?? clock.GetUtcNow(), null, null, ct);
    }

    /// <summary>
    /// Commits one analyzed part of a thread atomically: its status, placement and new bullets, and a new email stream when it
    /// joins a card without one. Nothing is committed if the account changed, the Gmail account was removed or another
    /// computer took over the import.
    /// </summary>
    private async Task CommitAsync(LinkedCredential credential, Run run, string threadId, string subject, string historyId,
        string lastMessageId, DateTimeOffset lastSentAt, EmailStepRequest? request, EmailStepResponse? response, CancellationToken ct)
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
            item = item with { Subject = subject, HistoryId = historyId, LastMessageId = lastMessageId, LastSentAt = lastSentAt, UpdatedAt = now };
            if (response is not null)
            {
                var bullets = response.Bullets.Select(bullet => new GmailBullet(Guid.NewGuid(), bullet.Text, bullet.Evidence,
                    bullet.Evidence.Max(citation => citation.Email!.SentAt), now, response.Newsworthy)).ToArray();
                item = item with { Explanation = response.Explanation, Bullets = [.. item.Bullets, .. bullets] };
                switch (response.Decision)
                {
                    case EmailSteps.Skip:
                        item = item with { Status = GmailThreadRecord.Skipped, Bullets = [], StreamId = null, Headline = null, NewsAt = null };
                        break;
                    case EmailSteps.Project:
                        var card = ThemePlan.Themes(current, account).FirstOrDefault(theme => theme.Id == response.ProjectId);
                        if (card is null)
                            item = item with { Status = GmailThreadRecord.Pending, Suggested = request!.Projects.First(project => project.Id == response.ProjectId).Title };
                        else
                        {
                            (current, string stream) = Stream(current, account, mail.Address, card, null, now);
                            item = item with { Status = GmailThreadRecord.Assigned, StreamId = stream };
                        }
                        break;
                    case EmailSteps.Unassigned:
                        item = item with { Status = GmailThreadRecord.Pending, Suggested = response.SuggestedProject };
                        break;
                }
                if (response.Newsworthy) item = item with { Headline = response.Headline, NewsAt = bullets.Max(bullet => bullet.SentAt) };
                if (item is { Status: GmailThreadRecord.Assigned, StreamId: { } changed } && bullets.Length != 0) current = Changed(current, account, changed);
            }
            current = current with
            {
                GmailThreads = Prune([.. Others(current, account, mail.Address, threadId), item]),
                GmailAccounts = run.Leased
                    ? [.. (current.GmailAccounts ?? []).Where(other => other != mail), mail with { LeaseUntil = now + LeaseFor }]
                    : current.GmailAccounts
            };
            return Tidy(current, account);
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
            var item = Item(current, account, request.Address, request.ThreadId);
            if (item?.Status is not (GmailThreadRecord.Assigned or GmailThreadRecord.Pending))
                throw new ResearchFailure(404, "gmail_thread_not_found", "This thread is no longer waiting for a decision.");
            var now = clock.GetUtcNow();
            string? before = item.Status == GmailThreadRecord.Assigned ? item.StreamId : null;
            switch (request.Action)
            {
                case "dismiss":
                    item = item with { Status = GmailThreadRecord.Dismissed, StreamId = null };
                    break;
                case "project":
                    var card = ThemePlan.Themes(current, account).FirstOrDefault(theme => theme.Id == request.CardId)
                        ?? throw new ResearchFailure(404, "research_card_not_found", "That research project no longer exists.");
                    (current, string stream) = Stream(current, account, item.Address, card, null, now);
                    item = item with { Status = GmailThreadRecord.Assigned, StreamId = stream };
                    break;
                case "new":
                    string name = request.Name?.Trim() ?? "";
                    if (name.Length is 0 or > 120) throw new ResearchFailure(400, "invalid_project_name", "Give the new project a name of at most 120 characters.");
                    (current, string created) = Stream(current, account, item.Address, null, name, now);
                    item = item with { Status = GmailThreadRecord.Assigned, StreamId = created };
                    break;
                default:
                    throw new ResearchFailure(400, "invalid_thread_decision", "Choose a thread and what to do with it.");
            }
            item = item with { UpdatedAt = now };
            if (before is not null && before != item.StreamId) current = Changed(current, account, before);
            if (item.StreamId is { } after && after != before) current = Changed(current, account, after);
            current = current with { GmailThreads = [.. Others(current, account, item.Address, item.Id), item] };
            return Tidy(current, account);
        }, ct);
        logger.LogInformation("A Gmail thread was {Action} by the user.", request.Action switch
        {
            "dismiss" => "dismissed", "project" => "added to a research project", _ => "made into a new research project"
        });
    }

    /// <summary>The account's threads for the feed: assigned ones with newsworthy progress, and those waiting for a decision.</summary>
    public static IReadOnlyList<GmailThreadResponse> Threads(LocalResearchState state, Guid? account)
    {
        if (account is not { } id) return [];
        var themes = ThemePlan.Themes(state, id);
        return [.. (state.GmailThreads ?? []).Where(item => item.AccountId == id &&
                (item.Status == GmailThreadRecord.Assigned && item.NewsAt is not null || item.Status == GmailThreadRecord.Pending))
            .OrderByDescending(item => item.NewsAt ?? item.LastSentAt).Take(MaxFeedThreads)
            .Select(item =>
            {
                var card = item.StreamId is { } stream ? themes.FirstOrDefault(theme => theme.Members.Contains(stream)) : null;
                return new GmailThreadResponse(item.Address, item.Id, item.Subject, item.Headline ?? item.Subject, item.Status, card?.Id,
                    card is null ? null : CardTitle(state, card), item.Suggested,
                    [.. item.Bullets.Select(bullet => new GmailBulletResponse(bullet.Id, bullet.Text, bullet.SentAt, bullet.Newsworthy, bullet.Evidence))],
                    item.LastSentAt, item.NewsAt);
            })];
    }

    /// <summary>A changed email stream to summarize once no import of its Gmail account is running.</summary>
    private MonitoredProject? StreamDue(LocalResearchState state, Guid account, DateTimeOffset now) =>
        state.Projects.Where(project => project.AccountId == account && project.Source == ResearchSources.Gmail && project.Enabled &&
                !project.ImportComplete && (project.NextAttemptAt is null || project.NextAttemptAt <= now) &&
                project.Parent is { } address && !_runs.ContainsKey(address) &&
                (state.GmailThreads ?? []).Any(item => item.StreamId == project.ProjectId && item.Status == GmailThreadRecord.Assigned && item.Bullets.Count != 0))
            .OrderBy(project => project.LastAttemptAt ?? DateTimeOffset.MinValue).FirstOrDefault();

    /// <summary>Summarizes one email stream from its threads' bullets, so its card's combined summary includes the email.</summary>
    private async Task SummarizeAsync(LinkedCredential credential, MonitoredProject stream, CancellationToken ct)
    {
        var account = credential.Account.Id;
        var request = StreamRequest(await store.ReadAsync(ct), stream);
        if (request is null) return;
        string print = Print(request);
        try
        {
            var response = await remote.EmailStreamAsync(credential.Token, request, ct);
            if (!ProfileEvidence.ValidProfile(response.Profile))
                throw new ResearchFailure(502, "invalid_email_stream", "Foundry returned an unsupported email summary. Nothing was changed.");
            await store.ChangeAsync(current =>
            {
                var project = current.Projects.FirstOrDefault(item => item.AccountId == account && item.ProjectId == stream.ProjectId);
                if (current.Credential?.Account.Id != account || project is null) return current;
                // Bullets added meanwhile keep the stream waiting for another summary.
                bool settled = StreamRequest(current, project) is { } now && Print(now) == print;
                var updated = project with
                {
                    Profile = response.Profile with { Title = project.Name }, ImportComplete = settled, LastAttemptAt = clock.GetUtcNow(),
                    LastSuccessAt = clock.GetUtcNow(), ErrorCode = null, Error = null, FailureCount = 0, NextAttemptAt = null
                };
                return current with { Projects = [.. current.Projects.Where(item => item != project), updated] };
            }, ct);
            logger.LogInformation("An email stream was summarized from {Threads} thread(s).", request.Threads.Count);
        }
        catch (ResearchFailure failure)
        {
            logger.LogWarning("Summarizing an email stream failed ({Code}); it is retried later.", failure.Code);
            await store.ChangeAsync(current =>
            {
                var project = current.Projects.FirstOrDefault(item => item.AccountId == account && item.ProjectId == stream.ProjectId);
                if (project is null) return current;
                var now = clock.GetUtcNow();
                var failed = project with
                {
                    LastAttemptAt = now, ErrorCode = failure.Code, Error = failure.Message, FailureCount = Math.Min(project.FailureCount + 1, 10),
                    NextAttemptAt = now + TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Min(project.FailureCount, 10)), 720))
                };
                return current with { Projects = [.. current.Projects.Where(item => item != project), failed] };
            }, ct);
        }
    }

    private static EmailStreamRequest? StreamRequest(LocalResearchState state, MonitoredProject stream)
    {
        var threads = new List<EmailStreamThread>();
        int characters = 0;
        foreach (var item in (state.GmailThreads ?? []).Where(item => item.StreamId == stream.ProjectId && item.Status == GmailThreadRecord.Assigned &&
                     item.Bullets.Count != 0).OrderByDescending(item => item.NewsAt ?? item.LastSentAt).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var bullets = item.Bullets.TakeLast(12).Select(bullet => bullet.Text).ToArray();
            int size = item.Subject.Length + (item.Headline?.Length ?? 0) + bullets.Sum(bullet => bullet.Length);
            if (threads.Count == EmailSteps.MaxStreamThreads || characters + size > EmailSteps.MaxStreamCharacters - 2000) break;
            threads.Add(new(item.Subject, item.Headline, bullets));
            characters += size;
        }
        if (threads.Count == 0) return null;
        var previous = stream.Profile is { } profile && profile.Description != Placeholder && ProfileEvidence.ValidProfile(profile) ? profile : null;
        return new(stream.Name, previous, threads);
    }

    private static string Print(EmailStreamRequest request) =>
        ResearchEvidence.Hash(JsonSerializer.Serialize(request with { Profile = null }, ResearchJson.Options));

    /// <summary>
    /// The email stream of a Gmail account on a research card, made if the card has none yet (and placed there for good), or
    /// a new stream on a card of its own for a new project named <paramref name="name"/>.
    /// </summary>
    private static (LocalResearchState State, string Id) Stream(LocalResearchState state, Guid account, string address, ResearchTheme? card,
        string? name, DateTimeOffset now)
    {
        if (card is not null && state.Projects.FirstOrDefault(project => project.AccountId == account && project.Source == ResearchSources.Gmail &&
                Same(project.Parent, address) && card.Members.Contains(project.ProjectId)) is { } existing)
            return (state, existing.ProjectId);
        string id = ResearchSources.NewGmailWorkstreamId();
        string title = Trim(name ?? "Email · " + (card is null ? "research" : CardTitle(state, card)), 120);
        var stream = new MonitoredProject(account, id, title, true, ResearchEvidence.ConsentVersion, null, null, [], [], now, null, null, null,
            null, 0, 0, Profile: new(title, Placeholder, [], []), ImportComplete: false, Source: ResearchSources.Gmail, Parent: address);
        state = state with { Projects = [.. state.Projects, stream] };
        return (ThemePlan.Place(state, account, id, card?.Id, now, pin: true), id);
    }

    /// <summary>An email stream's threads changed: it is summarized again (and its card waits for that summary).</summary>
    private static LocalResearchState Changed(LocalResearchState state, Guid account, string streamId) =>
        state with
        {
            Projects = [.. state.Projects.Select(project => project.AccountId == account && project.ProjectId == streamId && project.Source == ResearchSources.Gmail
                ? project with { ImportComplete = false, NextAttemptAt = null } : project)]
        };

    /// <summary>Email streams without threads are removed (their cards drop them).</summary>
    private static LocalResearchState Tidy(LocalResearchState state, Guid account)
    {
        var used = (state.GmailThreads ?? []).Where(item => item.AccountId == account && item.Status == GmailThreadRecord.Assigned && item.StreamId is not null)
            .Select(item => item.StreamId!).ToHashSet(StringComparer.Ordinal);
        var empty = state.Projects.Where(project => project.AccountId == account && project.Source == ResearchSources.Gmail && !used.Contains(project.ProjectId))
            .Select(project => project.ProjectId).ToHashSet(StringComparer.Ordinal);
        return empty.Count == 0 ? state : state with
        {
            Projects = [.. state.Projects.Where(project => project.AccountId != account || !empty.Contains(project.ProjectId))],
            Themes = [.. (state.Themes ?? []).Select(theme => theme.AccountId == account && theme.Members.Any(empty.Contains)
                    ? theme with { Members = [.. theme.Members.Where(member => !empty.Contains(member))], Pinned = theme.Pinned?.Where(member => !empty.Contains(member)).ToArray() }
                    : theme).Where(theme => theme.Members.Count != 0)]
        };
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
    /// The user's research cards with summaries (also those the user made for email), as compact descriptors: title, the first
    /// sentences and topics.
    /// </summary>
    private static IReadOnlyList<ThemeCandidate> Cards(LocalResearchState state, Guid account) =>
        [.. ThemePlan.Themes(state, account).Where(theme => theme.Profile is not null).Take(ResearchThemes.MaxCandidates).Select(Candidate)];

    private static ThemeCandidate? Card(LocalResearchState state, Guid account, string? streamId) =>
        streamId is null ? null : ThemePlan.Themes(state, account).FirstOrDefault(theme => theme.Members.Contains(streamId) && theme.Profile is not null) is { } card
            ? Candidate(card) : null;

    private static ThemeCandidate Candidate(ResearchTheme theme)
    {
        var profile = theme.Profile!;
        string description = profile.Description;
        if (description.Length > 400)
        {
            int end = description.LastIndexOf(". ", 400, StringComparison.Ordinal);
            description = end > 100 ? description[..(end + 1)] : description[..400];
        }
        return new(theme.Id, Trim(profile.Title, ProfileEvidence.HardMaxTitle), description, [.. profile.Topics.Take(8)]);
    }

    private static string CardTitle(LocalResearchState state, ResearchTheme card) =>
        card.Profile?.Title ?? state.Projects.FirstOrDefault(project => card.Members.Contains(project.ProjectId))?.Name ?? "Research project";

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
