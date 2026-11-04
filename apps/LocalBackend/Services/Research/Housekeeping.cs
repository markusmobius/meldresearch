using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// Housekeeping (formerly Private updates): email threads that fit none of the user's research projects wait in groups about
/// the same topic, people or kind of work, so the user decides on a whole group at once. Grouping is loose and costs one
/// small call per newly waiting thread plus one regrouping pass when groups gained threads; a group's name and preview
/// points come from the threads' first email check, so naming and describing a group costs nothing. Each group is also
/// compared (strictly) with the research projects, and a match is shown as a suggestion the user confirms. Groups the user
/// made or renamed, or that hold a thread the user moved there, are never combined by regrouping.
/// </summary>
internal static class Housekeeping
{
    public const int MaxName = 120, MaxDescription = 1200, ThreadsDescribed = 8, PointsPerThread = 3;

    public static IReadOnlyList<HousekeepingGroup> Groups(LocalResearchState state, Guid account) =>
        [.. (state.HousekeepingGroups ?? []).Where(group => group.AccountId == account)];

    public static IEnumerable<GmailThreadRecord> Waiting(LocalResearchState state, Guid account) =>
        (state.GmailThreads ?? []).Where(item => item.AccountId == account && item.Status == GmailThreadRecord.Pending);

    /// <summary>A group's waiting threads, newest first.</summary>
    public static IReadOnlyList<GmailThreadRecord> Members(LocalResearchState state, HousekeepingGroup group) =>
        [.. Waiting(state, group.AccountId).Where(item => item.GroupId == group.Id).OrderByDescending(item => item.LastSentAt)];

    public static string Key(GmailThreadRecord item) => item.Address.ToLowerInvariant() + "/" + item.Id;

    /// <summary>The user made, renamed or arranged this group: regrouping leaves it as it is.</summary>
    public static bool Held(LocalResearchState state, HousekeepingGroup group) =>
        group.Held == true || group.Named == true || Members(state, group).Any(item => item.GroupPinned == true);

    /// <summary>
    /// Threads that stopped waiting leave their group, a thread whose group is gone waits to be grouped again, and groups
    /// left without threads go. Null when nothing needs cleaning.
    /// </summary>
    public static LocalResearchState? Cleaned(LocalResearchState state, Guid account)
    {
        var groups = Groups(state, account);
        var ids = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        var threads = (state.GmailThreads ?? []).Select(item =>
        {
            if (item.AccountId != account || item.GroupId is null && item.GroupPinned is null) return item;
            if (item.Status == GmailThreadRecord.Pending && item.GroupId is { } id && ids.Contains(id)) return item;
            changed = true;
            return item with { GroupId = null, GroupPinned = null };
        }).ToArray();
        var used = threads.Where(item => item.AccountId == account && item.Status == GmailThreadRecord.Pending && item.GroupId is not null)
            .Select(item => item.GroupId!).ToHashSet(StringComparer.Ordinal);
        var kept = groups.Where(group => used.Contains(group.Id)).ToArray();
        changed |= kept.Length != groups.Count;
        return changed ? state with
        {
            GmailThreads = threads,
            HousekeepingGroups = [.. (state.HousekeepingGroups ?? []).Where(group => group.AccountId != account), .. kept]
        } : null;
    }

    /// <summary>The oldest waiting thread that is not in a group yet (and not held back after a failure).</summary>
    public static GmailThreadRecord? GroupDue(LocalResearchState state, Guid account, Func<GmailThreadRecord, bool>? ready = null) =>
        Waiting(state, account).Where(item => item.GroupId is null && (ready?.Invoke(item) ?? true)).OrderBy(item => item.LastSentAt).FirstOrDefault();

    /// <summary>Groups regrouping may combine: those the user did not make, rename or arrange.</summary>
    public static IReadOnlyList<HousekeepingGroup> Loose(LocalResearchState state, Guid account) =>
        [.. Groups(state, account).Where(group => !Held(state, group))];

    /// <summary>
    /// Groups should be compared once more: every waiting thread is grouped, at least two groups can be combined, and one of
    /// them gained threads since the last pass. Removing threads or groups never makes a pass due.
    /// </summary>
    public static bool RegroupDue(LocalResearchState state, Guid account)
    {
        if (GroupDue(state, account) is not null) return false;
        var loose = Loose(state, account);
        return loose.Count >= 2 && loose.Any(group => Members(state, group).Any(item => group.Checked?.Contains(Key(item)) != true));
    }

    /// <summary>What a group's suggestion was checked for: its threads (and their latest mail) and the user's research projects.</summary>
    public static string Fingerprint(LocalResearchState state, HousekeepingGroup group) =>
        ResearchEvidence.Hash(string.Join("|", Members(state, group).Select(item => Key(item) + "@" + item.LastMessageId).Order(StringComparer.Ordinal)) +
            "#" + ThemePlan.Signature(state, group.AccountId));

    /// <summary>
    /// A group whose suggestion is due: grouping and regrouping are done, the research projects are settled (no project is
    /// being read or placed), and its threads or the projects changed since it was last compared.
    /// </summary>
    public static HousekeepingGroup? SuggestDue(LocalResearchState state, Guid account, Func<HousekeepingGroup, bool>? ready = null)
    {
        if (GroupDue(state, account) is not null || RegroupDue(state, account)) return null;
        if (state.Projects.Any(project => ThemePlan.Member(state, account, project) && !ThemePlan.Settled(project) && project.FailureCount < 3) ||
            ThemePlan.SummaryDue(state, account) is not null || ThemePlan.MatchDue(state, account) is not null) return null;
        return Groups(state, account).FirstOrDefault(group => group.SuggestedFor != Fingerprint(state, group) && (ready?.Invoke(group) ?? true));
    }

    /// <summary>A thread's own name: the project its first email check suggested, else its headline or subject.</summary>
    public static string NameOf(GmailThreadRecord item)
    {
        string name = (item.Suggested ?? item.Headline ?? item.Subject).Trim();
        return Trim(name.Length == 0 ? "Email" : name, MaxName);
    }

    /// <summary>The threads' titles and latest preview points (no email text), as sent for grouping and suggestions.</summary>
    public static string Describe(IEnumerable<GmailThreadRecord> threads)
    {
        var parts = threads.Take(ThreadsDescribed).Select(item =>
        {
            string title = item.Headline ?? item.Subject;
            var points = item.Bullets.TakeLast(PointsPerThread).Select(bullet => bullet.Text).ToArray();
            return points.Length == 0 ? title + "." : title + ": " + string.Join(' ', points);
        });
        string text = string.Join('\n', parts).Trim();
        return Trim(text.Length == 0 ? "Email threads." : text, MaxDescription);
    }

    public static ProjectProfile Profile(string name, IEnumerable<GmailThreadRecord> threads) => new(Trim(name, MaxName), Describe(threads), [], []);

    public static ThemeCandidate Candidate(LocalResearchState state, HousekeepingGroup group) =>
        new(group.Id, Trim(group.Name, ProfileEvidence.HardMaxTitle), Describe(Members(state, group)), []);

    public static HousekeepingGroup NewGroup(Guid account, string name, DateTimeOffset now, bool held = false) =>
        new(account, Guid.NewGuid().ToString("N"), Trim(name.Trim().Length == 0 ? "Email" : name.Trim(), MaxName), now, now, Held: held ? true : null);

    /// <summary>Groups from two places merged: per group the later change wins (groups left empty are cleaned afterwards).</summary>
    public static IReadOnlyList<HousekeepingGroup>? Merge(IReadOnlyList<HousekeepingGroup>? local, IEnumerable<HousekeepingGroup>? remote, Guid account)
    {
        if (remote is null) return local;
        return [.. (local ?? []).Where(group => group.AccountId != account),
            .. (local ?? []).Where(group => group.AccountId == account).Concat(remote.Where(group => group.AccountId == account))
                .GroupBy(group => group.Id, StringComparer.Ordinal).Select(group => group.MaxBy(item => item.UpdatedAt)!)];
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}
