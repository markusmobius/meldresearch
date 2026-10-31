namespace MeldResearch.Research;

/// <summary>
/// An existing research card that a newly imported Overleaf project may belong to: only its short descriptors are sent.
/// </summary>
public sealed record ThemeCandidate(string Id, string Title, string Description, IReadOnlyList<string> Topics);

/// <summary>Asks whether a newly imported project continues one of the user's existing research cards.</summary>
public sealed record ThemeMatchRequest(string ProjectName, ProjectProfile Profile, IReadOnlyList<ThemeCandidate> Candidates);

/// <summary>The matching card's id, or null to start a new card.</summary>
public sealed record ThemeMatchResponse(string? ThemeId, string Explanation, string ChatModel);

public sealed record ThemeMember(string ProjectName, ProjectProfile Profile);

/// <summary>Combines the summaries of the Overleaf projects on one research card into one card summary.</summary>
public sealed record ThemeMergeRequest(IReadOnlyList<ThemeMember> Members);
public sealed record ThemeMergeResponse(ProjectProfile Profile, string ChatModel);

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
                candidate.Title.Length > 200 || candidate.Description is null || candidate.Description.Length > 1500 ||
                candidate.Topics is not { Count: <= 12 } || candidate.Topics.Any(topic => topic is null || topic.Length > 80)))
            throw new ResearchFailure(400, "invalid_theme_request", "The project grouping request is invalid.");
    }

    public static void ValidateMerge(ThemeMergeRequest request)
    {
        if (request?.Members is not { Count: >= 2 and <= MaxMembers } members ||
            members.Any(member => member is null || string.IsNullOrWhiteSpace(member.ProjectName) || member.ProjectName.Length > 500 ||
                member.Profile is null || !ProfileEvidence.ValidProfile(member.Profile)))
            throw new ResearchFailure(400, "invalid_theme_request", "A combined summary needs 2-12 valid project summaries.");
    }

    /// <summary>Card ids are 32 lowercase hex characters (a GUID without dashes).</summary>
    public static bool IsThemeId(string? id) =>
        id is { Length: 32 } && id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
