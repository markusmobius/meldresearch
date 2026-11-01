using System.Text.Json;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Which research card work is due. A project joins a card once its first import has finished and it has no queued files;
/// a card is summarized again once all its members are settled and any member's summary changed. Excluded or forgotten
/// projects leave their card, and empty cards disappear. Projects already on a card are never moved automatically.
/// </summary>
internal static class ThemePlan
{
    public static string Fingerprint(ProjectProfile profile) => ResearchEvidence.Hash(JsonSerializer.Serialize(profile, ResearchJson.Options));

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

    /// <summary>A card whose combined summary no longer matches its settled members' summaries.</summary>
    public static (ResearchTheme Theme, MonitoredProject[] Members)? Stale(LocalResearchState state, Guid account)
    {
        var projects = state.Projects.Where(project => project.AccountId == account).ToDictionary(project => project.ProjectId, StringComparer.Ordinal);
        foreach (var theme in Themes(state, account))
        {
            var members = theme.Members.Select(id => projects.GetValueOrDefault(id)).OfType<MonitoredProject>().ToArray();
            if (members.Length == 0 || members.Length != theme.Members.Count || !members.All(Settled)) continue;
            if (theme.Profile is not null && Matches(theme, members)) continue;
            return (theme, members);
        }
        return null;
    }

    public static bool Matches(ResearchTheme theme, IReadOnlyList<MonitoredProject> members) =>
        theme.MergedFrom is { } merged && merged.Count == members.Count &&
        members.All(member => merged.TryGetValue(member.ProjectId, out var print) && print == Fingerprint(member.Profile!));

    public static bool HasWork(LocalResearchState state, Guid account) =>
        Cleaned(state, account) is not null || Unplaced(state, account) is not null || RegroupDue(state, account) ||
        Stale(state, account) is not null;

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
            if (moved.Count != 0) themes[themes.IndexOf(target)] = target with { Members = [.. target.Members, .. moved] };
        }
        return state with { Themes = themes };
    }

    /// <summary>
    /// Puts a project on the chosen card (or a new one), removing it from any other card of the account. A placement by the
    /// user pins the project there.
    /// </summary>
    public static LocalResearchState Place(LocalResearchState state, Guid account, string projectId, string? themeId, DateTimeOffset now,
        bool pin = false)
    {
        var themes = (state.Themes ?? []).Select(theme => theme.AccountId == account && theme.Members.Contains(projectId)
                ? theme with { Members = theme.Members.Where(member => member != projectId).ToArray(),
                    Pinned = theme.Pinned?.Where(member => member != projectId).ToArray() } : theme)
            .Where(theme => theme.AccountId != account || theme.Members.Count != 0).ToList();
        int index = themeId is null ? -1 : themes.FindIndex(theme => theme.AccountId == account && theme.Id == themeId);
        if (index >= 0)
            themes[index] = themes[index] with { Members = [.. themes[index].Members, projectId],
                Pinned = pin ? [.. themes[index].Pinned ?? [], projectId] : themes[index].Pinned };
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
