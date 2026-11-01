using System.Text.RegularExpressions;

namespace MeldResearch.Research;

/// <summary>
/// One email as sent for analysis: Gmail's message id, the sender, when it was sent, whether the user wrote it, and its
/// cleaned text (quoted replies and signatures removed).
/// </summary>
public sealed record EmailMessage(string Id, string From, DateTimeOffset SentAt, bool FromUser, string Text);

/// <summary>An existing Gmail workstream the thread may continue.</summary>
public sealed record EmailWorkstreamCandidate(string Id, ProjectProfile Profile);

/// <summary>
/// One thread step: fold the thread's new Messages into one of the user's Gmail workstreams (or start one, or skip the
/// thread). Context holds earlier messages of the thread that were already analyzed (background only, never re-reported).
/// PreviousWorkstreamId is where earlier messages of this thread went.
/// </summary>
public sealed record EmailStepRequest(string ThreadId, string Subject, string UserAddress, IReadOnlyList<EmailMessage> Messages,
    IReadOnlyList<EmailMessage> Context, string? PreviousWorkstreamId, IReadOnlyList<EmailWorkstreamCandidate> Candidates);

/// <summary>
/// Decision: "skip" (not work the user takes part in), "existing" (WorkstreamId is a candidate) or "new". Profile is the
/// workstream's updated summary (null when skipped); Updates are new, cited progress (email evidence).
/// </summary>
public sealed record EmailStepResponse(string Decision, string? WorkstreamId, ProjectProfile? Profile, IReadOnlyList<ProfileUpdate> Updates,
    string Explanation, string ChatModel);

public static partial class EmailSteps
{
    public const int MaxMessages = 30, MaxContext = 4, MaxCandidates = 10, MaxMessageCharacters = 8000, MaxThreadCharacters = 40000;
    public const string Skip = "skip", Existing = "existing", New = "new";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    public static bool IsGmailId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static void ValidateRequest(EmailStepRequest request)
    {
        bool Valid(EmailMessage message) => message is not null && IsGmailId(message.Id) && Short(message.From, 320) &&
            message.Text is { Length: > 0 and <= MaxMessageCharacters };
        if (request is null || !IsGmailId(request.ThreadId) || !Short(request.Subject, 500) || !Short(request.UserAddress, 320) ||
            request.Messages is not { Count: > 0 and <= MaxMessages } || !request.Messages.All(Valid) ||
            request.Context is not { Count: <= MaxContext } || !request.Context.All(Valid) ||
            request.Messages.Concat(request.Context).Sum(message => message.Text.Length) > MaxThreadCharacters ||
            request.Messages.Concat(request.Context).Select(message => message.Id).Distinct(StringComparer.Ordinal).Count() !=
                request.Messages.Count + request.Context.Count ||
            request.Candidates is not { Count: <= MaxCandidates } ||
            !request.Candidates.All(candidate => candidate is not null && ResearchSources.IsGmailWorkstreamId(candidate.Id) &&
                ProfileEvidence.ValidProfile(candidate.Profile)) ||
            request.Candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Count ||
            (request.PreviousWorkstreamId is { } previous && !request.Candidates.Any(candidate => candidate.Id == previous)))
            throw new ResearchFailure(400, "invalid_email_evidence", "The email evidence is invalid or exceeds the supported limits.");
    }

    public static void ValidateResponse(EmailStepRequest request, EmailStepResponse response)
    {
        if (response is null || !Short(response.Explanation, 2000) || response.Updates is null) throw Invalid("invalid_email_step");
        switch (response.Decision)
        {
            case Skip when response.WorkstreamId is null && response.Profile is null && response.Updates.Count == 0:
                return;
            case Existing when request.Candidates.Any(candidate => candidate.Id == response.WorkstreamId):
            case New when response.WorkstreamId is null:
                break;
            default: throw Invalid("invalid_email_decision");
        }
        if (response.Profile is null || !ProfileEvidence.ValidProfile(response.Profile)) throw Invalid("invalid_profile");
        if (response.Updates.Count > ProfileEvidence.MaxUpdates) throw Invalid("invalid_update_count");
        var messages = request.Messages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        foreach (var update in response.Updates)
        {
            if (update is null || !Short(update.Title, 240) || !Short(update.Summary, 1500) || update.Evidence is not { Count: > 0 and <= 4 })
                throw Invalid("invalid_update_fields");
            // Updates rest on the new messages only: earlier (context) messages were reported before.
            foreach (var citation in update.Evidence)
                if (citation?.Email is not { } email || !messages.TryGetValue(email.MessageId, out var message) ||
                    email.From != message.From || email.SentAt != message.SentAt || citation.Path != request.Subject ||
                    citation.Version != EvidenceVersion.Current || !Short(citation.Quote, 8000) ||
                    !message.Text.Contains(citation.Quote, StringComparison.Ordinal))
                    throw Invalid("citation_not_verbatim");
        }
    }

    private static bool Short(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static ResearchFailure Invalid(string code) => new(502, code, "Foundry returned an unsupported email analysis. Nothing was committed.");
}
