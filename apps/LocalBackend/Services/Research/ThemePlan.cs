using System.Text.Json;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Which research card work is due. A project joins a card once its first import has finished and it has no queued files.
/// A card is described from its anchor, its main paper, and again whenever the anchor or its paper or summary changes; the
/// other members are its materials. Excluded or forgotten projects leave their card, and empty cards disappear. Projects
/// already on a card are never moved automatically.
/// </summary>
internal static class ThemePlan
{
    /// <summary>The card analysis in use: cards grouped under an earlier, looser rule are rebuilt once.</summary>
    public const int Version = 2;

    public static string Fingerprint(ProjectProfile profile) => ResearchEvidence.Hash(JsonSerializer.Serialize(profile, ResearchJson.Options));

    /// <summary>
    /// The member a card is described from: the one the user chose; otherwise, among its Overleaf projects (or, on a card
    /// made only of email, its email streams), one with a paper abstract, then one with a paper title, the most recently
    /// edited first, then the most developed summary.
    /// </summary>
    public static MonitoredProject? AnchorOf(LocalResearchState state, ResearchTheme theme)
    {
        var members = theme.Members.Select(id => state.Projects.FirstOrDefault(project => project.AccountId == theme.AccountId && project.ProjectId == id))
            .OfType<MonitoredProject>().Where(project => project.Profile is not null).ToArray();
        if (theme.Anchor is { } chosen && members.FirstOrDefault(member => member.ProjectId == chosen) is { } picked) return picked;
        var papers = members.Where(member => member.IsOverleaf).ToArray();
        return (papers.Length != 0 ? papers : members)
            .OrderByDescending(member => member.Paper?.Abstract is not null).ThenByDescending(member => member.Paper?.Title is not null)
            .ThenByDescending(member => member.Baseline?.SourceUpdatedAt ?? member.LastSuccessAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(member => member.Processed?.Count ?? 0).ThenBy(member => member.ProjectId, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>What a card's description depends on: its anchor's summary and paper.</summary>
    public static string AnchorPrint(MonitoredProject anchor) =>
        Fingerprint(anchor.Profile!) + "|" + (anchor.Paper is { } paper ? ResearchEvidence.Hash(paper.Title + "\n" + paper.Abstract) : "");

    public static IReadOnlyList<ResearchTheme> Themes(LocalResearchState state, Guid account) =>
        (state.Themes ?? []).Where(theme => theme.AccountId == account).ToArray();

    /// <summary>A project with a summary that the user has not excluded.</summary>
    public static bool Member(LocalResearchState state, Guid account, MonitoredProject project) =>
        project.AccountId == account && project.Profile is not null &&
        state.Decisions?.Any(decision => decision.AccountId == account && decision.ProjectId == project.ProjectId && !decision.Included) != true;

    public static bool Settled(MonitoredProject project) => project.ImportComplete && project.Pending is null && project.Profile is not null;

    /// <summary>The account's cards without departed members and without empty cards; null when nothing changes.</summary>
    public static IReadOnlyList<ResearchTheme>? Cleaned(LocalResearchState state, Guid account)
    {
        var members = state.Projects.Where(project => Member(state, account, project)).Select(project => project.ProjectId).ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        var themes = new List<ResearchTheme>();
        foreach (var theme in state.Themes ?? [])
        {
            if (theme.AccountId != account) { themes.Add(theme); continue; }
            var kept = theme.Members.Where(members.Contains).Distinct(StringComparer.Ordinal).ToArray();
            if (kept.Length == theme.Members.Count) { themes.Add(theme); continue; }
            changed = true;
            if (kept.Length != 0) themes.Add(theme with { Members = kept });
        }
        return changed ? themes : null;
    }

    /// <summary>The next settled project that is not on a card yet, oldest import first.</summary>
    public static MonitoredProject? Unplaced(LocalResearchState state, Guid account)
    {
        var placed = Themes(state, account).SelectMany(theme => theme.Members).ToHashSet(StringComparer.Ordinal);
        return state.Projects.Where(project => Member(state, account, project) && Settled(project) && !placed.Contains(project.ProjectId))
            .OrderBy(project => project.LastSuccessAt ?? DateTimeOffset.MaxValue).FirstOrDefault();
    }

    /// <summary>An Overleaf project whose processed revision's title and abstract have not been read yet.</summary>
    public static bool PaperDue(LocalResearchState state, Guid account) =>
        state.Projects.Any(project => project.AccountId == account && project.IsOverleaf && project.Pending is null &&
            project.Baseline is { } baseline && project.PaperFor != baseline.Revision);

    /// <summary>A card whose description no longer matches its settled anchor.</summary>
    public static (ResearchTheme Theme, MonitoredProject Anchor)? Stale(LocalResearchState state, Guid account)
    {
        foreach (var theme in Themes(state, account))
        {
            if (AnchorOf(state, theme) is not { } anchor || !Settled(anchor)) continue;
            if (theme.Profile is not null && Matches(theme, anchor)) continue;
            return (theme, anchor);
        }
        return null;
    }

    public static bool Matches(ResearchTheme theme, MonitoredProject? anchor) =>
        anchor?.Profile is not null && theme.MergedFrom is { Count: 1 } merged &&
        merged.TryGetValue(anchor.ProjectId, out var print) && print == AnchorPrint(anchor);

    /// <summary>
    /// The one-time rebuild for cards grouped under the earlier rule: each card keeps its anchor and the members the user
    /// placed there; the others are placed again (strictly), and every card is described again from its anchor.
    /// </summary>
    public static LocalResearchState Rebuild(LocalResearchState state, Guid account) =>
        state with
        {
            Themes = [.. (state.Themes ?? []).Select(theme =>
            {
                if (theme.AccountId != account) return theme;
                string? anchor = AnchorOf(state, theme)?.ProjectId;
                var kept = theme.Members.Where(member => member == anchor || theme.Pinned?.Contains(member) == true).ToArray();
                if (kept.Length == 0) kept = [theme.Members[0]];
                return theme with { Members = kept, Pinned = theme.Pinned?.Where(kept.Contains).ToArray(), MergedFrom = null, Reason = null };
            })],
            ThemesRegroupedFor = null, ThemesVersion = Version
        };

    public static bool HasWork(LocalResearchState state, Guid account) =>
        Cleaned(state, account) is not null || state.ThemesVersion < Version || PaperDue(state, account) || Unplaced(state, account) is not null || RegroupDue(state, account) ||
        Stale(state, account) is not null || BlurbDue(state, account) is not null;

    /// <summary>A card whose 4-5 sentence description was made from an earlier version of its summary (or not yet at all).</summary>
    public static ResearchTheme? BlurbDue(LocalResearchState state, Guid account) =>
        Stale(state, account) is not null ? null
        : Themes(state, account).FirstOrDefault(theme => theme.Profile is not null && theme.BlurbFor != Fingerprint(theme.Profile));

    /// <summary>The account's cards and their members, independent of order; regrouping runs again only when it changes.</summary>
    public static string Signature(LocalResearchState state, Guid account) => ResearchEvidence.Hash(string.Join("|",
        Themes(state, account).Select(theme => theme.Id + ":" + string.Join(",", theme.Members.Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)));

    /// <summary>
    /// The cards should be compared once more: they changed since the last regrouping, every card has a summary, nothing waits
    /// to be placed, and no project is still on its first import (one that keeps failing does not hold regrouping back).
    /// </summary>
    public static bool RegroupDue(LocalResearchState state, Guid account)
    {
        var themes = Themes(state, account);
        if (themes.Count < 2 || themes.Any(theme => theme.Profile is null) || Unplaced(state, account) is not null) return false;
        if (state.Projects.Any(project => project.AccountId == account && project.Enabled && !project.ImportComplete && project.FailureCount < 3))
            return false;
        return state.ThemesRegroupedFor != Signature(state, account);
    }

    /// <summary>
    /// Combines each group of cards into its largest card. Members the user placed by hand stay where they are (their card
    /// remains), so automatic regrouping never undoes a manual choice.
    /// </summary>
    public static LocalResearchState ApplyGroups(LocalResearchState state, Guid account, IReadOnlyList<ThemeGroup> groups)
    {
        var themes = (state.Themes ?? []).ToList();
        foreach (var group in groups)
        {
            var cards = themes.Where(theme => theme.AccountId == account && group.CardIds.Contains(theme.Id)).ToList();
            if (cards.Count < 2) continue;
            var target = cards.OrderByDescending(theme => theme.Members.Count).ThenBy(theme => theme.CreatedAt).First();
            var moved = new List<string>();
            foreach (var source in cards.Where(theme => theme != target))
            {
                var pinned = source.Pinned ?? [];
                moved.AddRange(source.Members.Where(member => !pinned.Contains(member)));
                var kept = source.Members.Where(pinned.Contains).ToArray();
                int index = themes.IndexOf(source);
                if (kept.Length == 0) themes.RemoveAt(index);
                else themes[index] = source with { Members = kept };
            }
            if (moved.Count != 0) themes[themes.IndexOf(target)] = target with { Members = [.. target.Members, .. moved], Reason = group.Explanation };
        }
        return state with { Themes = themes };
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
                    Pinned = theme.Pinned?.Where(member => member != projectId).ToArray() } : theme)
            .Where(theme => theme.AccountId != account || theme.Members.Count != 0).ToList();
        int index = themeId is null ? -1 : themes.FindIndex(theme => theme.AccountId == account && theme.Id == themeId);
        if (index >= 0)
            themes[index] = themes[index] with { Members = [.. themes[index].Members, projectId],
                Pinned = pin ? [.. themes[index].Pinned ?? [], projectId] : themes[index].Pinned, Reason = reason ?? themes[index].Reason };
        else themes.Add(new ResearchTheme(account, Guid.NewGuid().ToString("N"), [projectId], null, null, now, pin ? [projectId] : null));
        return state with { Themes = themes };
    }

    /// <summary>The user combines one card into another: every member moves and is pinned there.</summary>
    public static LocalResearchState MergeCards(LocalResearchState state, Guid account, string sourceId, string targetId, DateTimeOffset now)
    {
        var source = Themes(state, account).First(theme => theme.Id == sourceId);
        foreach (var member in source.Members) state = Place(state, account, member, targetId, now, pin: true);
        return state;
    }
}
