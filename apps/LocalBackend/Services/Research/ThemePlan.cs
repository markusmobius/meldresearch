using System.Text.Json;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Research card (project) work that is due. A card holds the details of one piece of research (OVERLEAF.md §1, §11): its
/// Overleaf projects (members) and the email threads assigned to it feed one list of details. A card's title and two-sentence
/// summary are rewritten from its details once its members' ingest cycles have finished; a new card is then matched
/// (strictly) against the others, and cards are compared once more whenever the set changed. Excluded projects leave their
/// card with their sources, and empty cards disappear. Projects on a card are never moved automatically.
/// </summary>
internal static class ThemePlan
{
    public static string Fingerprint(ProjectProfile profile) => ResearchEvidence.Hash(JsonSerializer.Serialize(profile, ResearchJson.Options));

    public static IReadOnlyList<ResearchTheme> Themes(LocalResearchState state, Guid account) =>
        (state.Themes ?? []).Where(theme => theme.AccountId == account).ToArray();

    /// <summary>The account's research cards in the profile, without those skipped as not the user's own research.</summary>
    public static IReadOnlyList<ResearchTheme> Active(LocalResearchState state, Guid account) =>
        [.. Themes(state, account).Where(theme => theme.Skipped != true)];

    /// <summary>Members of skipped cards: their Overleaf projects pause and their changes leave the feed.</summary>
    public static IReadOnlySet<string> SkippedMembers(LocalResearchState state, Guid account) =>
        Themes(state, account).Where(theme => theme.Skipped == true).SelectMany(theme => theme.Members).ToHashSet(StringComparer.Ordinal);

    /// <summary>The card an Overleaf project is on.</summary>
    public static ResearchTheme? CardOf(LocalResearchState state, Guid account, string projectId) =>
        Themes(state, account).FirstOrDefault(theme => theme.Members.Contains(projectId));

    /// <summary>A skipped card not yet compared (loosely) with the other skipped cards.</summary>
    public static ResearchTheme? SkipUngrouped(LocalResearchState state, Guid account) =>
        Themes(state, account).FirstOrDefault(theme => theme.Skipped == true && theme.SkipGrouped != true && theme.Profile is not null &&
            theme.SummaryFor == DetailPlan.Fingerprint(theme.Details));

    /// <summary>
    /// The member whose paper titles the card: the user's choice, else an Overleaf member with an abstract (then a title),
    /// most recently edited first.
    /// </summary>
    public static MonitoredProject? AnchorOf(LocalResearchState state, ResearchTheme theme)
    {
        var members = theme.Members.Select(id => state.Projects.FirstOrDefault(project => project.AccountId == theme.AccountId && project.ProjectId == id))
            .OfType<MonitoredProject>().ToArray();
        if (theme.Anchor is { } chosen && members.FirstOrDefault(member => member.ProjectId == chosen) is { } picked) return picked;
        return members.OrderByDescending(member => member.Paper?.Abstract is not null).ThenByDescending(member => member.Paper?.Title is not null)
            .ThenByDescending(member => member.Baseline?.SourceUpdatedAt ?? member.LastSuccessAt ?? DateTimeOffset.MinValue)
            .ThenBy(member => member.ProjectId, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>An included Overleaf project the user has not excluded.</summary>
    public static bool Member(LocalResearchState state, Guid account, MonitoredProject project) =>
        project.AccountId == account && project.IsOverleaf && project.Enabled &&
        state.Decisions?.Any(decision => decision.AccountId == account && decision.ProjectId == project.ProjectId && !decision.Included) != true;

    public static bool Settled(MonitoredProject project) => project.ImportComplete && project.Pending is null;

    /// <summary>Email threads assigned to a card whose new messages are not folded yet.</summary>
    public static bool FoldPending(LocalResearchState state, ResearchTheme theme) =>
        (state.GmailThreads ?? []).Any(thread => thread.AccountId == theme.AccountId && thread.CardId == theme.Id &&
            thread.Status == GmailThreadRecord.Assigned && thread.FoldedMessageId != thread.LastMessageId);

    /// <summary>
    /// The account's cards without departed members (whose sources leave the details) and without cards that are empty (no
    /// members, no email threads, no details); null when nothing changes.
    /// </summary>
    public static LocalResearchState? Cleaned(LocalResearchState state, Guid account)
    {
        var members = state.Projects.Where(project => Member(state, account, project)).Select(project => project.ProjectId).ToHashSet(StringComparer.Ordinal);
        var threads = (state.GmailThreads ?? []).Where(thread => thread.AccountId == account && thread.CardId is not null &&
            thread.Status == GmailThreadRecord.Assigned).Select(thread => thread.CardId!).ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        var themes = new List<ResearchTheme>();
        var log = state.Changelog;
        foreach (var theme in state.Themes ?? [])
        {
            if (theme.AccountId != account) { themes.Add(theme); continue; }
            var departed = theme.Members.Where(member => !members.Contains(member)).ToHashSet(StringComparer.Ordinal);
            var card = theme;
            if (departed.Count != 0)
            {
                changed = true;
                var (details, changes) = DetailPlan.Without(theme.Details ?? [], source => source.Kind == DetailSource.Overleaf && departed.Contains(source.Container));
                card = theme with { Members = [.. theme.Members.Where(members.Contains)], Pinned = theme.Pinned?.Where(members.Contains).ToArray(), Details = details };
                if (changes.Count != 0)
                    log = DetailPlan.Log(log, new(Guid.NewGuid(), account, theme.Id, DateTimeOffset.UtcNow, ChangelogEntry.Removed,
                        "Project removed from this card", null, [.. departed], changes, null, DateTimeOffset.UtcNow));
            }
            // An empty card goes, unless the user named it or a file is waiting to be added to it.
            if (card.Members.Count == 0 && !threads.Contains(card.Id) && (card.Details?.Count ?? 0) == 0 && card.Name is null &&
                state.PendingFiles?.Any(file => file.TargetCardId == card.Id) != true) { changed = true; continue; }
            themes.Add(card);
        }
        return changed ? state with { Themes = themes, Changelog = log } : null;
    }

    /// <summary>How a card is shown: the user's name for it, else the name of a project started from email, else its short name, else its title.</summary>
    public static string DisplayTitle(ResearchTheme theme, string fallback = "Research project") =>
        theme.ChosenName is { Length: > 0 } chosen ? chosen : theme.Name is { Length: > 0 } name ? name :
        theme.ShortName is { Length: > 0 } shortName ? shortName : theme.Profile?.Title ?? fallback;

    /// <summary>
    /// A card whose details changed since its summary was written (or that has no short name yet), once its members' cycles
    /// and email folds finished.
    /// </summary>
    public static ResearchTheme? SummaryDue(LocalResearchState state, Guid account) =>
        Themes(state, account).FirstOrDefault(theme => theme.Details is { Count: > 0 } details &&
            (theme.SummaryFor != DetailPlan.Fingerprint(details) || theme.ShortName is null) &&
            theme.Members.All(member => state.Projects.FirstOrDefault(project => project.AccountId == account && project.ProjectId == member) is not { } project ||
                Settled(project)) && !FoldPending(state, theme));

    /// <summary>
    /// A card of the profile whose details changed since its last review, settled like <see cref="SummaryDue"/>, with at least
    /// <see cref="DetailRules.MinReviewDetails"/> details and no review in the last <paramref name="interval"/> (OVERLEAF.md §8a).
    /// </summary>
    public static ResearchTheme? ReviewDue(LocalResearchState state, Guid account, DateTimeOffset now, TimeSpan interval) =>
        Active(state, account).FirstOrDefault(theme => theme.Details is { Count: >= DetailRules.MinReviewDetails } details &&
            theme.ReviewedFor != DetailPlan.Fingerprint(details) && (theme.ReviewedAt is not { } reviewed || now - reviewed >= interval) &&
            theme.Members.All(member => state.Projects.FirstOrDefault(project => project.AccountId == account && project.ProjectId == member) is not { } project ||
                Settled(project)) && !FoldPending(state, theme));

    /// <summary>A new card, summarized, waiting to be compared (strictly) with the other cards.</summary>
    public static ResearchTheme? MatchDue(LocalResearchState state, Guid account) =>
        Active(state, account).FirstOrDefault(theme => theme.MatchPending == true && theme.Profile is not null &&
            theme.SummaryFor == DetailPlan.Fingerprint(theme.Details));

    /// <summary>
    /// An included project whose paper was read by an earlier way of reading papers (Papers.Version): it is read again from
    /// the stored text of its current version, without a model call.
    /// </summary>
    public static MonitoredProject? PaperDue(LocalResearchState state, Guid account) =>
        state.Projects.FirstOrDefault(project => project.AccountId == account && project.IsOverleaf && project.Enabled && project.Pending is null &&
            project.Baseline is { } baseline && project.PaperFor != Papers.ReadFor(baseline.Revision));

    /// <summary>The title a card takes from its main paper (one with an abstract), exactly as the summary step stores it; else null.</summary>
    public static string? PaperTitle(LocalResearchState state, ResearchTheme theme)
    {
        if (AnchorOf(state, theme)?.Paper is not { Abstract: not null, Title: { } title }) return null;
        if (title.Length > Papers.MaxTitle) title = title[..Papers.MaxTitle];
        return title.Length > DetailRules.MaxTitle ? title[..DetailRules.MaxTitle].TrimEnd() : title;
    }

    /// <summary>A card whose title differs from its main paper's (the paper was edited or read again): the title follows, no model call.</summary>
    public static ResearchTheme? TitleDue(LocalResearchState state, Guid account) =>
        Themes(state, account).FirstOrDefault(theme => theme.Profile is not null && PaperTitle(state, theme) is { } title && title != theme.Profile.Title);

    public static bool HasWork(LocalResearchState state, Guid account, DateTimeOffset now, TimeSpan interval) =>
        Cleaned(state, account) is not null || PaperDue(state, account) is not null || TitleDue(state, account) is not null || SummaryDue(state, account) is not null || ReviewDue(state, account, now, interval) is not null || MatchDue(state, account) is not null ||
        RegroupDue(state, account) || SkipUngrouped(state, account) is not null || CollaboratorPlan.Due(state, account) is not null;

    /// <summary>The account's cards and their members, independent of order; regrouping runs again only when it changes.</summary>
    public static string Signature(LocalResearchState state, Guid account) => ResearchEvidence.Hash(string.Join("|",
        Active(state, account).Select(theme => theme.Id + ":" + string.Join(",", theme.Members.Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)));

    /// <summary>
    /// The cards should be compared once more: they changed since the last regrouping, every card is summarized, none waits
    /// for its first match, and no project is in an ingest cycle.
    /// </summary>
    public static bool RegroupDue(LocalResearchState state, Guid account)
    {
        var themes = Active(state, account);
        if (themes.Count < 2 || themes.Any(theme => theme.Profile is null || theme.MatchPending == true) || SummaryDue(state, account) is not null) return false;
        if (state.Projects.Any(project => Member(state, account, project) && !Settled(project) && project.FailureCount < 3)) return false;
        return state.ThemesRegroupedFor != Signature(state, account);
    }

    /// <summary>
    /// Puts a project on the chosen card (or a new one), removing it from any other card of the account. A placement by the
    /// user pins the project there.
    /// </summary>
    public static LocalResearchState Place(LocalResearchState state, Guid account, string projectId, string? themeId, DateTimeOffset now,
        bool pin = false, string? reason = null)
    {
        var themes = (state.Themes ?? []).Select(theme => theme.AccountId == account && theme.Members.Contains(projectId)
                ? theme with { Members = theme.Members.Where(member => member != projectId).ToArray(),
                    Pinned = theme.Pinned?.Where(member => member != projectId).ToArray() } : theme).ToList();
        int index = themeId is null ? -1 : themes.FindIndex(theme => theme.AccountId == account && theme.Id == themeId);
        if (index >= 0)
            themes[index] = themes[index] with { Members = [.. themes[index].Members, projectId],
                Pinned = pin ? [.. themes[index].Pinned ?? [], projectId] : themes[index].Pinned, Reason = reason ?? themes[index].Reason };
        else themes.Add(NewCard(account, [projectId], now, pin, matchPending: !pin, skipped: state.SkippedProjects?.Contains(projectId) == true));
        return state with { Themes = themes };
    }

    public static ResearchTheme NewCard(Guid account, IReadOnlyList<string> members, DateTimeOffset now, bool pin = false, bool matchPending = false,
        bool skipped = false, string? name = null) =>
        new(account, Guid.NewGuid().ToString("N"), members, null, null, now, pin ? members : null, Details: [],
            MatchPending: matchPending && !skipped ? true : null, Skipped: skipped ? true : null, SkipGrouped: skipped && !matchPending ? true : null, Name: name);
}
