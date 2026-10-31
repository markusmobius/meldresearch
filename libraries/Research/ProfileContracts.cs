using System.Text;
using System.Text.Json;

namespace MeldResearch.Research;

/// <summary>The running summary of one Overleaf project, kept under <see cref="ProfileEvidence.MaxProfileCharacters"/> as JSON.</summary>
public sealed record ProjectProfile(string Title, string Description, IReadOnlyList<string> Contributions, IReadOnlyList<string> Topics);

/// <summary>One file's evidence for a summary step. An initial import sends only the current text; a deleted file only the previous.</summary>
public sealed record ProfileFileEvidence(string Path, string? PreviousText, string? CurrentText);

/// <summary>
/// One step of the file-by-file pipeline: fold one file into the project summary. Initial steps build the baseline and
/// never produce Updates; later steps also report what changed as cited Updates.
/// </summary>
public sealed record ProfileStepRequest(string ProjectId, string ProjectName, bool Initial, ProjectProfile? Profile,
    ProfileFileEvidence File);

/// <summary>A meaningful change to the project summary, grounded in exact quotations from the file.</summary>
public sealed record ProfileUpdate(string Title, string Summary, IReadOnlyList<ResearchCitation> Evidence);

/// <summary>The updated summary and its Updates; the remote service keeps no copy.</summary>
public sealed record ProfileStepResponse(ProjectProfile Profile, IReadOnlyList<ProfileUpdate> Updates, string Explanation,
    string ChatModel);

public static class ProfileEvidence
{
    public const int MaxProfileCharacters = 5000;
    public const int MaxUpdates = 5;
    public const int MaxContributions = 15;

    public static int Length(ProjectProfile profile) => JsonSerializer.Serialize(profile, ResearchJson.Options).Length;

    public static void ValidateRequest(ProfileStepRequest request)
    {
        if (request is null || !ResearchEvidence.IsProjectId(request.ProjectId) || !Short(request.ProjectName, 500) ||
            request.File is not { } file || !ResearchEvidence.IsSafePath(file.Path) ||
            !ResearchEvidence.TextExtensions.Contains(Path.GetExtension(file.Path)) ||
            (file.PreviousText is null && file.CurrentText is null) || !ValidText(file.PreviousText) || !ValidText(file.CurrentText) ||
            (request.Initial && (file.PreviousText is not null || file.CurrentText is null)) ||
            (!request.Initial && request.Profile is null) ||
            (request.Profile is not null && !ValidProfile(request.Profile)))
            throw new ResearchFailure(400, "invalid_research_evidence", "The research evidence is invalid or exceeds the supported source limits.");
    }

    public static void ValidateResponse(ProfileStepRequest request, ProfileStepResponse response)
    {
        if (response?.Profile is null || !ValidProfile(response.Profile)) throw Invalid("invalid_profile");
        if (!Short(response.Explanation, 2000) || response.Updates is null || response.Updates.Count > MaxUpdates ||
            (request.Initial && response.Updates.Count != 0))
            throw Invalid("invalid_update_count");
        foreach (var update in response.Updates)
        {
            if (update is null || !Short(update.Title, 240) || !Short(update.Summary, 1500) ||
                update.Evidence is null || update.Evidence.Count is 0 or > 4)
                throw Invalid("invalid_update_fields");
            foreach (var citation in update.Evidence)
            {
                string? text = citation?.Version switch
                {
                    EvidenceVersion.Current => request.File.CurrentText,
                    EvidenceVersion.Previous => request.File.PreviousText,
                    _ => null
                };
                if (citation is null || citation.Path != request.File.Path || !Short(citation.Quote, 8000) ||
                    text is null || !text.Contains(citation.Quote, StringComparison.Ordinal))
                    throw Invalid("citation_not_verbatim");
            }
            // Additions and revisions rest on the current text; only a deleted file is cited from its previous version.
            if (request.File.CurrentText is not null && !update.Evidence.Any(citation => citation.Version == EvidenceVersion.Current))
                throw Invalid("citation_not_current");
        }
    }

    public static bool ValidProfile(ProjectProfile profile) =>
        profile is not null && Short(profile.Title, 200) && Short(profile.Description, 1500) &&
        profile.Contributions is { Count: <= MaxContributions } && profile.Contributions.All(item => Short(item, 500)) &&
        profile.Topics is { Count: <= 12 } && profile.Topics.All(item => Short(item, 80)) &&
        Length(profile) <= MaxProfileCharacters;

    private static bool ValidText(string? text) => text is null ||
        (Encoding.UTF8.GetByteCount(text) <= ResearchEvidence.MaxFileTextBytes && !text.Contains('\0'));
    private static bool Short(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static ResearchFailure Invalid(string reason) => new(502, "ungrounded_research_analysis",
        $"Foundry returned an invalid or ungrounded summary step ({reason}). The previous project state was preserved.");
}
