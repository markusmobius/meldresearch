using System.Text.Json.Serialization;

namespace MeldResearch.Research;

/// <summary>
/// An existing research card that a newly imported Overleaf project may belong to: only its short descriptors are sent.
/// </summary>
public sealed record ThemeCandidate(string Id, string Title, string Description, IReadOnlyList<string> Topics);

/// <summary>
/// Asks whether a newly imported project continues one of the user's existing research cards. Loose: the cards are work the
/// user skipped as not their own (for example helping a family member); sharing a person or a topic is enough there.
/// Waiting: the candidates are Housekeeping groups of email threads that wait for the user's decision, and the item is one
/// such thread; the same topic, people, event or kind of work is enough.
/// </summary>
public sealed record ThemeMatchRequest(string ProjectName, ProjectProfile Profile, IReadOnlyList<ThemeCandidate> Candidates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Loose = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Waiting = false);

/// <summary>The matching card's id, or null to start a new card.</summary>
public sealed record ThemeMatchResponse(string? ThemeId, string Explanation, string ChatModel);

public sealed record ThemeMember(string ProjectName, ProjectProfile Profile);

/// <summary>Combines the summaries of the Overleaf projects on one research card into one card summary.</summary>
public sealed record ThemeMergeRequest(IReadOnlyList<ThemeMember> Members);
public sealed record ThemeMergeResponse(ProjectProfile Profile, string ChatModel);

/// <summary>A research card's full summary, to be described in 4-5 sentences for the card itself.</summary>
public sealed record CardBlurbRequest(ProjectProfile Profile);
public sealed record CardBlurbResponse(string Blurb, string ChatModel);

/// <summary>
/// All of the user's research cards, to find cards that belong to one research programme. Waiting: the cards are Housekeeping
/// groups of waiting email threads, combined loosely (the same topic, people, event or kind of work).
/// </summary>
public sealed record ThemeRegroupRequest(IReadOnlyList<ThemeCandidate> Cards,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Waiting = false);
/// <summary>A set of cards to combine into one; cards in no group stay as they are.</summary>
public sealed record ThemeGroup(IReadOnlyList<string> CardIds, string Explanation);
public sealed record ThemeRegroupResponse(IReadOnlyList<ThemeGroup> Groups, string ChatModel);

/// <summary>
/// One Housekeeping group of waiting email threads (their titles and latest preview points, newest first), described in one
/// call: a short headline for the group, and the research project (of Projects, possibly none) it is the same work as.
/// </summary>
public sealed record WaitingGroupRequest(string Name, IReadOnlyList<WaitingThread> Threads, IReadOnlyList<ThemeCandidate> Projects);
public sealed record WaitingThread(string Title, IReadOnlyList<string> Points);
/// <summary>Headline: at most a few words naming what the threads are about. ProjectId: a matching project (strict rule), or null.</summary>
public sealed record WaitingGroupResponse(string Headline, string? ProjectId, string Explanation, string ChatModel);

/// <summary>
/// Research cards group Overleaf projects that belong to one research line (drafts, slides, follow-ups of the same work).
/// Only summaries of included projects are sent; the remote service keeps no copy.
/// </summary>
public static class ResearchThemes
{
    public const int MaxCandidates = 40;
    public const int MaxMembers = 12;

    public static void ValidateMatch(ThemeMatchRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProjectName) || request.ProjectName.Length > 500 ||
            request.Profile is null || !ProfileEvidence.ValidProfile(request.Profile) ||
            request.Candidates is null || request.Candidates.Count is 0 or > MaxCandidates ||
            request.Candidates.Select(candidate => candidate?.Id).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Count ||
            request.Candidates.Any(candidate => candidate is null || !IsThemeId(candidate.Id) || string.IsNullOrWhiteSpace(candidate.Title) ||
                candidate.Title.Length > ProfileEvidence.HardMaxTitle || candidate.Description is null ||
                candidate.Description.Length > ProfileEvidence.HardMaxDescription || candidate.Topics is not { Count: <= ProfileEvidence.HardMaxTopics } ||
                candidate.Topics.Any(topic => topic is null || topic.Length > ProfileEvidence.HardMaxTopic)))
            throw new ResearchFailure(400, "invalid_theme_request", "The project grouping request is invalid.");
    }

    public static void ValidateMerge(ThemeMergeRequest request)
    {
        if (request?.Members is not { Count: >= 2 and <= MaxMembers } members ||
            members.Any(member => member is null || string.IsNullOrWhiteSpace(member.ProjectName) || member.ProjectName.Length > 500 ||
                member.Profile is null || !ProfileEvidence.ValidProfile(member.Profile)))
            throw new ResearchFailure(400, "invalid_theme_request", "A combined summary needs 2-12 valid project summaries.");
    }

    public static void ValidateRegroup(ThemeRegroupRequest request)
    {
        if (request?.Cards is not { Count: >= 2 and <= MaxCandidates } cards)
            throw new ResearchFailure(400, "invalid_theme_request", "Regrouping needs 2-40 research cards.");
        ValidateMatch(new("regroup", new("t", "d", [], []), cards));
    }

    /// <summary>Groups of at least two offered cards, each card in at most one group.</summary>
    public static bool ValidGroups(ThemeRegroupRequest request, IReadOnlyList<ThemeGroup>? groups)
    {
        if (groups is null || groups.Count > MaxCandidates / 2) return false;
        var known = request.Cards.Select(card => card.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return groups.All(group => group?.CardIds is { Count: >= 2 } ids && ids.All(id => id is not null && known.Contains(id) && seen.Add(id)));
    }

    public const int MaxThreads = 12, MaxPoints = 3, MaxHeadline = 120;

    public static void ValidateWaiting(WaitingGroupRequest request)
    {
        bool Short(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
        if (request is null || !Short(request.Name, 500) || request.Threads is not { Count: > 0 and <= MaxThreads } ||
            request.Threads.Any(thread => thread is null || !Short(thread.Title, 500) || thread.Points is not { Count: <= MaxPoints } ||
                thread.Points.Any(point => !Short(point, 1000))) || request.Projects is not { Count: <= MaxCandidates })
            throw new ResearchFailure(400, "invalid_waiting_group", "The Housekeeping group request is invalid.");
        if (request.Projects.Count != 0) ValidateMatch(new("group", new("t", "d", [], []), request.Projects));
    }

    /// <summary>A headline is one plain line of at most <see cref="MaxHeadline"/> characters.</summary>
    public static bool ValidHeadline(string? headline) => !string.IsNullOrWhiteSpace(headline) && headline.Length <= MaxHeadline && !headline.Contains('\n');

    public const int MaxBlurb = 1400;

    public static bool ValidBlurb(string? blurb) => !string.IsNullOrWhiteSpace(blurb) && blurb.Length <= MaxBlurb && !blurb.Contains('\n');

    /// <summary>Card ids are 32 lowercase hex characters (a GUID without dashes).</summary>
    public static bool IsThemeId(string? id) =>
        id is { Length: 32 } && id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
