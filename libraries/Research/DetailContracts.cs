using System.Text.Json.Serialization;

namespace MeldResearch.Research;

/// <summary>
/// Where a detail comes from (OVERLEAF.md §1): an Overleaf file at a content hash (Kind "overleaf": Container is the project id,
/// Item the path, Version the SHA-256) or an email of a Gmail thread (Kind "gmail": Container is the address, Item the thread
/// id, Version the message id). Label is what the user sees (the path, or the subject); At when it was written.
/// </summary>
public sealed record DetailSource(string Kind, string Container, string Item, string Version, string Label, DateTimeOffset? At)
{
    public const string Overleaf = "overleaf", Gmail = "gmail";
    /// <summary>The source regardless of version: one file of a project, or one thread.</summary>
    [JsonIgnore] public string Key => $"{Kind}:{Container}/{Item}";
}

/// <summary>A quotation (copied verbatim from the source) that supports a detail.</summary>
public sealed record DetailCitation(DetailSource Source, string Quote);

/// <summary>One detail of a research project: one sentence, with its sources.</summary>
public sealed record DetailBullet(string Id, string Text, IReadOnlyList<DetailCitation> Citations);

/// <summary>A detail as sent to a step: each citation has a Ref the step may remove; Outdated when its document changed.</summary>
public sealed record DetailInput(string Id, string Text, IReadOnlyList<DetailCitationInput> Citations);
public sealed record DetailCitationInput(string Ref, string Label, bool Outdated);

/// <summary>An email of a thread for a detail step (numbered lines are added by the server).</summary>
public sealed record DetailMessage(string Id, string From, DateTimeOffset SentAt, bool FromUser, string Text);

/// <summary>
/// One update of a research project's details (OVERLEAF.md §7): Overleaf documents of one root tree (Documents, with the tree
/// Outline) or new emails of one thread (Messages, with earlier Context). Title, PaperTitle and Abstract give context.
/// </summary>
public sealed record DetailStepRequest(string Kind, string Title, string? PaperTitle, string? Abstract, IReadOnlyList<DetailInput> Details,
    IReadOnlyList<string> Outline, IReadOnlyList<PartDocument> Documents, string? Subject = null,
    IReadOnlyList<DetailMessage>? Messages = null, IReadOnlyList<DetailMessage>? Context = null);

/// <summary>A citation chosen by the model and resolved by the server: the cited lines of a sent document or message, verbatim.</summary>
public sealed record DetailQuote(string Item, int StartLine, int EndLine, string Quote);

/// <summary>
/// An edit of the details: "add" (Text and Add citations), "revise" (Id; new Text and/or Add citations and/or Remove refs)
/// or "remove" (Id).
/// </summary>
public sealed record DetailOp(string Op, string? Id, string? Text, IReadOnlyList<DetailQuote> Add, IReadOnlyList<string> Remove);
public sealed record DetailStepResponse(IReadOnlyList<DetailOp> Ops, string Explanation, string ChatModel);

/// <summary>A research project's two-sentence summary from its details; the paper's title, when known, is the title.</summary>
public sealed record DetailSummaryRequest(string? PaperTitle, string? Abstract, IReadOnlyList<string> Details);
public sealed record DetailSummaryResponse(string Title, string Summary, string ChatModel);

/// <summary>Combines the details of research projects that are one project; each merged detail names the details it came from.</summary>
public sealed record DetailMergeCard(string Title, IReadOnlyList<DetailInput> Details);
public sealed record DetailMergeRequest(IReadOnlyList<DetailMergeCard> Cards);
public sealed record DetailMerged(string Text, IReadOnlyList<string> From);
public sealed record DetailMergeResponse(IReadOnlyList<DetailMerged> Details, string ChatModel);

public static class DetailRules
{
    public const int MaxDetails = 25, MaxText = 300, MaxOps = 40, MaxCitations = 4, MaxSummary = 450, MaxTitle = 200,
        MaxMergeCards = 12, MaxRequestCharacters = 90000;
    public const string Add = "add", Revise = "revise", Remove = "remove";

    public static void ValidateRequest(DetailStepRequest request)
    {
        bool Short(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
        bool overleaf = request?.Kind == DetailSource.Overleaf, email = request?.Kind == DetailSource.Gmail;
        if (request is null || !(overleaf || email) || !Short(request.Title, ProfileEvidence.HardMaxTitle) ||
            request.PaperTitle is { Length: > Papers.MaxTitle + 1 } || request.Abstract is { Length: > Papers.MaxAbstract + 1 } ||
            request.Details is not { Count: <= MaxDetails } || !ValidInputs(request.Details) ||
            request.Outline is not { Count: <= ResearchEvidence.MaxFiles } || request.Documents is null ||
            (overleaf && (request.Documents.Count == 0 || request.Documents.Any(document => document is null || !Short(document.Path, 1000) ||
                document.Text is null || document.FirstLine < 1) || request.Messages is not null)) ||
            (email && (request.Documents.Count != 0 || !Short(request.Subject, 500) || request.Messages is not { Count: > 0 and <= EmailSteps.MaxMessages } ||
                request.Messages.Concat(request.Context ?? []).Any(message => message is null || !EmailSteps.IsGmailId(message.Id) ||
                    !Short(message.From, 320) || message.Text is not { Length: > 0 and <= EmailSteps.MaxMessageCharacters }))) ||
            Size(request) > MaxRequestCharacters)
            throw new ResearchFailure(400, "invalid_detail_request", "The project update is invalid or exceeds the supported limits.");
    }

    /// <summary>The ops refer only to current details and citation refs, cite only what was sent, and leave at most 25 details.</summary>
    public static void ValidateResponse(DetailStepRequest request, DetailStepResponse response)
    {
        if (response?.Ops is not { Count: <= MaxOps } ops || response.Explanation is null) throw Failed("invalid_detail_step");
        var ids = request.Details.Select(detail => detail.Id).ToHashSet(StringComparer.Ordinal);
        var refs = request.Details.SelectMany(detail => detail.Citations.Select(citation => (detail.Id, citation.Ref))).ToHashSet();
        var touched = new HashSet<string>(StringComparer.Ordinal);
        int count = ids.Count;
        foreach (var op in ops)
        {
            if (op is null || op.Add is null || op.Remove is null || op.Add.Count > MaxCitations) throw Failed("invalid_detail_step");
            switch (op.Op)
            {
                case Add when op.Id is null && ValidText(op.Text) && op.Add.Count > 0 && op.Remove.Count == 0:
                    count++;
                    break;
                case Revise when op.Id is not null && ids.Contains(op.Id) && touched.Add(op.Id) && (op.Text is null || ValidText(op.Text)) &&
                                 op.Remove.All(item => refs.Contains((op.Id, item))):
                    break;
                case Remove when op.Id is not null && ids.Contains(op.Id) && touched.Add(op.Id):
                    count--;
                    break;
                default: throw Failed("invalid_detail_op");
            }
            foreach (var quote in op.Add)
                if (quote is null || !Cited(request, quote)) throw Failed("citation_not_verbatim");
        }
        if (count > MaxDetails) throw Failed("too_many_details");
    }

    public static bool ValidText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= MaxText && !text.Contains('\n');

    public static bool ValidSummary(DetailSummaryResponse? response) =>
        response is not null && !string.IsNullOrWhiteSpace(response.Title) && response.Title.Length <= MaxTitle &&
        !string.IsNullOrWhiteSpace(response.Summary) && response.Summary.Length <= MaxSummary && !response.Summary.Contains('\n');

    public static void ValidateSummary(DetailSummaryRequest request)
    {
        if (request?.Details is not { Count: > 0 and <= MaxDetails } || !request.Details.All(ValidText) ||
            request.PaperTitle is { Length: > Papers.MaxTitle + 1 } || request.Abstract is { Length: > Papers.MaxAbstract + 1 })
            throw new ResearchFailure(400, "invalid_detail_summary", "A summary needs 1-25 valid details.");
    }

    public static void ValidateMerge(DetailMergeRequest request)
    {
        if (request?.Cards is not { Count: >= 2 and <= MaxMergeCards } cards ||
            cards.Any(card => card is null || string.IsNullOrWhiteSpace(card.Title) || card.Title.Length > ProfileEvidence.HardMaxTitle ||
                card.Details is not { Count: <= MaxDetails } || !ValidInputs(card.Details)) ||
            cards.SelectMany(card => card.Details).Select(detail => detail.Id).Distinct(StringComparer.Ordinal).Count() !=
                cards.Sum(card => card.Details.Count))
            throw new ResearchFailure(400, "invalid_detail_merge", "Combining projects needs 2-12 projects with valid details.");
    }

    public static void ValidateMerged(DetailMergeRequest request, DetailMergeResponse response)
    {
        var known = request.Cards.SelectMany(card => card.Details).Select(detail => detail.Id).ToHashSet(StringComparer.Ordinal);
        if (response?.Details is not { Count: <= MaxDetails } merged ||
            merged.Any(item => item is null || !ValidText(item.Text) || item.From is not { Count: > 0 } || !item.From.All(known.Contains)))
            throw Failed("invalid_detail_merge");
    }

    private static bool ValidInputs(IReadOnlyList<DetailInput> details) =>
        details.All(detail => detail is not null && !string.IsNullOrWhiteSpace(detail.Id) && detail.Id.Length <= 64 && ValidText(detail.Text) &&
            detail.Citations is { Count: <= 12 } && detail.Citations.All(citation => citation is not null && !string.IsNullOrWhiteSpace(citation.Ref) &&
                citation.Ref.Length <= 16 && citation.Label is { Length: <= 1000 })) &&
        details.Select(detail => detail.Id).Distinct(StringComparer.Ordinal).Count() == details.Count;

    /// <summary>A quote is a verbatim line range of a document segment or a new message that was sent.</summary>
    private static bool Cited(DetailStepRequest request, DetailQuote quote)
    {
        if (string.IsNullOrWhiteSpace(quote.Quote) || quote.Quote.Length > 8000 || quote.StartLine < 1 || quote.EndLine < quote.StartLine) return false;
        if (request.Kind == DetailSource.Overleaf)
            return request.Documents.Any(document => document.Path == quote.Item && quote.StartLine >= document.FirstLine &&
                quote.EndLine < document.FirstLine + document.Text.Split('\n').Length && document.Text.Contains(quote.Quote, StringComparison.Ordinal));
        return request.Messages!.Any(message => message.Id == quote.Item && message.Text.Contains(quote.Quote, StringComparison.Ordinal));
    }

    private static int Size(DetailStepRequest request) =>
        request.Documents.Sum(document => document.Text.Length) + (request.Messages ?? []).Concat(request.Context ?? []).Sum(message => message.Text.Length) +
        request.Details.Sum(detail => detail.Text.Length + 40 * detail.Citations.Count) + request.Outline.Sum(line => line.Length) + (request.Abstract?.Length ?? 0);

    private static ResearchFailure Failed(string code) => new(502, code, "Foundry returned an unsupported project update. Nothing was committed.");
}
