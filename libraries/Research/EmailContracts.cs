using System.Text.RegularExpressions;

namespace MeldResearch.Research;

/// <summary>
/// One email as sent for analysis: Gmail's message id, the sender, when it was sent, whether the user wrote it, and its
/// cleaned text (quoted replies and signatures removed).
/// </summary>
public sealed record EmailMessage(string Id, string From, DateTimeOffset SentAt, bool FromUser, string Text);

/// <summary>
/// One thread step. A thread seen for the first time (Known false) is placed: skipped, assigned to one of the user's
/// research Projects (cards) or left unassigned for the user to decide. A known thread (Known true) only continues: its new
/// Messages add bullets to the ones already recorded (Bullets). Context holds earlier messages, as background only. Project is
/// the card a known thread belongs to (null while the user has not decided).
/// </summary>
public sealed record EmailStepRequest(string ThreadId, string Subject, string UserAddress, IReadOnlyList<EmailMessage> Messages,
    IReadOnlyList<EmailMessage> Context, bool Known, IReadOnlyList<string> Bullets, ThemeCandidate? Project,
    IReadOnlyList<ThemeCandidate> Projects);

/// <summary>One fact a thread established, citing lines of its new messages (copied verbatim by the server).</summary>
public sealed record EmailBullet(string Text, IReadOnlyList<ResearchCitation> Evidence);

/// <summary>
/// Decision: "skip" (not the user's work), "project" (ProjectId is one of the offered cards), "unassigned" (work, but no
/// card fits; SuggestedProject names it) or, for a known thread, "continue". Bullets are new facts; Newsworthy marks a
/// meaningful step forward, with Headline as the thread's feed title.
/// </summary>
public sealed record EmailStepResponse(string Decision, string? ProjectId, string? SuggestedProject, IReadOnlyList<EmailBullet> Bullets,
    bool Newsworthy, string? Headline, string Explanation, string ChatModel);

/// <summary>A thread of an email stream: its subject, feed title and bullets.</summary>
public sealed record EmailStreamThread(string Subject, string? Headline, IReadOnlyList<string> Bullets);

/// <summary>
/// Summarizes an email stream (the threads of one Gmail account on one research card) as a project summary, so the card's
/// combined summary includes what the email shows. Name is kept as the title; Profile is the previous summary, if any.
/// </summary>
public sealed record EmailStreamRequest(string Name, ProjectProfile? Profile, IReadOnlyList<EmailStreamThread> Threads);
public sealed record EmailStreamResponse(ProjectProfile Profile, string ChatModel);

public static partial class EmailSteps
{
    public const int MaxMessages = 30, MaxContext = 4, MaxMessageCharacters = 8000, MaxThreadCharacters = 40000;
    public const int MaxBullets = 6, MaxBulletCharacters = 400, MaxKnownBullets = 60, MaxHeadline = 160, MaxStreamThreads = 60,
        MaxStreamCharacters = 40000;
    public const string Skip = "skip", Project = "project", Unassigned = "unassigned", Continue = "continue";

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
            request.Bullets is not { Count: <= MaxKnownBullets } || !request.Bullets.All(bullet => Short(bullet, MaxBulletCharacters)) ||
            request.Projects is not { Count: <= ResearchThemes.MaxCandidates } ||
            (request.Known ? request.Projects.Count != 0 : request.Project is not null || request.Bullets.Count != 0))
            throw Invalid();
        try
        {
            if (request.Projects.Count != 0) ResearchThemes.ValidateMatch(new("thread", new("t", "d", [], []), request.Projects));
            if (request.Project is { } project) ResearchThemes.ValidateMatch(new("thread", new("t", "d", [], []), [project]));
        }
        catch (ResearchFailure) { throw Invalid(); }
    }

    public static void ValidateResponse(EmailStepRequest request, EmailStepResponse response)
    {
        if (response is null || !Short(response.Explanation, 2000) || response.Bullets is null) throw Failed("invalid_email_step");
        bool valid = response.Decision switch
        {
            Skip => !request.Known && response.ProjectId is null && response.Bullets.Count == 0 && !response.Newsworthy,
            Project => !request.Known && request.Projects.Any(project => project.Id == response.ProjectId),
            Unassigned => !request.Known && response.ProjectId is null && Short(response.SuggestedProject, ProfileEvidence.HardMaxTitle),
            Continue => request.Known && response.ProjectId is null,
            _ => false
        };
        if (!valid) throw Failed("invalid_email_decision");
        if (response.Bullets.Count > MaxBullets || response.Newsworthy && (response.Bullets.Count == 0 || !Short(response.Headline, MaxHeadline)))
            throw Failed("invalid_email_bullets");
        var messages = request.Messages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        foreach (var bullet in response.Bullets)
        {
            if (bullet is null || !Short(bullet.Text, MaxBulletCharacters) || bullet.Evidence is not { Count: > 0 and <= 4 })
                throw Failed("invalid_email_bullets");
            // Bullets rest on the new messages only: earlier (context) messages were recorded before.
            foreach (var citation in bullet.Evidence)
                if (citation?.Email is not { } email || !messages.TryGetValue(email.MessageId, out var message) ||
                    email.From != message.From || email.SentAt != message.SentAt || citation.Path != request.Subject ||
                    citation.Version != EvidenceVersion.Current || !Short(citation.Quote, 8000) ||
                    !message.Text.Contains(citation.Quote, StringComparison.Ordinal))
                    throw Failed("citation_not_verbatim");
        }
    }

    public static void ValidateStream(EmailStreamRequest request)
    {
        if (request is null || !Short(request.Name, ProfileEvidence.HardMaxTitle) ||
            (request.Profile is not null && !ProfileEvidence.ValidProfile(request.Profile)) ||
            request.Threads is not { Count: > 0 and <= MaxStreamThreads } ||
            request.Threads.Any(thread => thread is null || !Short(thread.Subject, 500) || thread.Headline is { Length: > MaxHeadline } ||
                thread.Bullets is not { Count: > 0 and <= MaxKnownBullets } || !thread.Bullets.All(bullet => Short(bullet, MaxBulletCharacters))) ||
            request.Threads.Sum(thread => thread.Subject.Length + (thread.Headline?.Length ?? 0) + thread.Bullets.Sum(bullet => bullet.Length)) > MaxStreamCharacters)
            throw new ResearchFailure(400, "invalid_email_stream", "The email stream summary request is invalid or too large.");
    }

    private static bool Short(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static ResearchFailure Invalid() => new(400, "invalid_email_evidence", "The email evidence is invalid or exceeds the supported limits.");
    private static ResearchFailure Failed(string code) => new(502, code, "Foundry returned an unsupported email analysis. Nothing was committed.");
}
