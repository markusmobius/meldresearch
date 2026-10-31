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
        Cleaned(state, account) is not null || Unplaced(state, account) is not null || Stale(state, account) is not null;

    /// <summary>Puts a project on the chosen card (or a new one), removing it from any other card of the account.</summary>
    public static LocalResearchState Place(LocalResearchState state, Guid account, string projectId, string? themeId, DateTimeOffset now)
    {
        var themes = (state.Themes ?? []).Select(theme => theme.AccountId == account && theme.Members.Contains(projectId)
                ? theme with { Members = theme.Members.Where(member => member != projectId).ToArray() } : theme)
            .Where(theme => theme.AccountId != account || theme.Members.Count != 0).ToList();
        int index = themeId is null ? -1 : themes.FindIndex(theme => theme.AccountId == account && theme.Id == themeId);
        if (index >= 0) themes[index] = themes[index] with { Members = [.. themes[index].Members, projectId] };
        else themes.Add(new ResearchTheme(account, Guid.NewGuid().ToString("N"), [projectId], null, null, now));
        return state with { Themes = themes };
    }
}
