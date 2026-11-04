using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>One thing to embed: its vector key, the identity of its text (For), and the text (a removed file's is read from the store).</summary>
internal sealed record RankItem(string Key, string For, string? Text, PendingFile? File);

/// <summary>
/// Orders the project chooser by closeness. Research cards (title, summary, first details), Housekeeping groups (name,
/// thread titles and preview points), included Overleaf projects (paper title, abstract, the details they support) and
/// removed files (path and text) are embedded once by Foundry, and again only when that text changes; opening the chooser
/// only compares stored vectors and makes no call. Vectors stay on this computer.
/// </summary>
internal static class Ranking
{
    public const int Batch = 20, MaxText = 8000, DetailsUsed = 8, BestShown = 3;

    public static string CardKey(string id) => "card:" + id;
    public static string GroupKey(string id) => "group:" + id;
    public static string ProjectKey(string id) => "project:" + id;
    public static string FileKey(PendingFile file) => $"file:{file.ProjectId}|{file.Path}";

    /// <summary>What can be embedded now: settled cards (their summary matches their details), groups with threads, projects on a card, waiting files.</summary>
    public static IReadOnlyList<RankItem> Items(LocalResearchState state, Guid account)
    {
        var items = new List<RankItem>();
        var cards = ThemePlan.Active(state, account);
        foreach (var card in cards.Where(card => card.Profile is not null && card.Details is { Count: > 0 } details &&
            card.SummaryFor == DetailPlan.Fingerprint(details)))
            items.Add(Text(CardKey(card.Id), CardText(card)));
        foreach (var group in Housekeeping.Groups(state, account))
        {
            var members = Housekeeping.Members(state, group);
            if (members.Count != 0) items.Add(Text(GroupKey(group.Id), group.Name + "\n" + Housekeeping.Describe(members)));
        }
        foreach (var card in cards)
            foreach (var member in card.Members)
                if (state.Projects.FirstOrDefault(project => project.AccountId == account && project.ProjectId == member && project.IsOverleaf) is { } project &&
                    ProjectText(project, card) is { } text)
                    items.Add(Text(ProjectKey(member), text));
        foreach (var file in (state.PendingFiles ?? []).Where(file => file.AccountId == account && file.TargetCardId is null))
            items.Add(new(FileKey(file), file.Sha256 + "|" + file.Path, null, file));
        return items;
    }

    /// <summary>Items whose vector is missing or was made from other text.</summary>
    public static IReadOnlyList<RankItem> Due(LocalResearchState state, Guid account)
    {
        var vectors = (state.Vectors ?? []).Where(vector => vector.AccountId == account).ToDictionary(vector => vector.Key, StringComparer.Ordinal);
        return [.. Items(state, account).Where(item => !vectors.TryGetValue(item.Key, out var vector) || vector.For != item.For)];
    }

    /// <summary>Vectors of cards, groups, projects and files that are gone are dropped; null when nothing is to drop.</summary>
    public static LocalResearchState? Cleaned(LocalResearchState state, Guid account)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in ThemePlan.Themes(state, account)) keep.Add(CardKey(theme.Id));
        foreach (var group in Housekeeping.Groups(state, account)) keep.Add(GroupKey(group.Id));
        foreach (var project in state.Projects.Where(project => project.AccountId == account)) keep.Add(ProjectKey(project.ProjectId));
        foreach (var file in (state.PendingFiles ?? []).Where(file => file.AccountId == account)) keep.Add(FileKey(file));
        var vectors = state.Vectors ?? [];
        if (vectors.All(vector => vector.AccountId != account || keep.Contains(vector.Key))) return null;
        return state with { Vectors = [.. vectors.Where(vector => vector.AccountId != account || keep.Contains(vector.Key))] };
    }

    public static string CardText(ResearchTheme card) => Clip(string.Join('\n', new[] { card.Profile!.Title, card.Profile.Description }
        .Concat((card.Details ?? []).Take(DetailsUsed).Select(detail => detail.Text))));

    /// <summary>An Overleaf project's own text: its paper's title and abstract and the card details it is a source of.</summary>
    public static string? ProjectText(MonitoredProject project, ResearchTheme card)
    {
        var details = (card.Details ?? []).Where(detail => detail.Citations.Any(citation =>
            citation.Source.Kind == DetailSource.Overleaf && citation.Source.Container == project.ProjectId)).Take(DetailsUsed).Select(detail => detail.Text).ToArray();
        if (project.Paper is null && details.Length == 0) return null;
        return Clip(string.Join('\n', new[] { project.Name, project.Paper?.Title, project.Paper?.Abstract }.Where(text => !string.IsNullOrWhiteSpace(text))
            .Concat(details)!));
    }

    public static string FileText(PendingFile file, string? text) => Clip(file.Path + "\n" + (text ?? ""));

    /// <summary>A vector normalized to unit length and stored as 8-bit values.</summary>
    public static string Encode(IReadOnlyList<float> vector)
    {
        double norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        var bytes = new byte[vector.Count];
        for (int index = 0; index < vector.Count; index++)
            bytes[index] = unchecked((byte)(sbyte)Math.Clamp(Math.Round(norm == 0 ? 0 : vector[index] / norm * 127), -127, 127));
        return Convert.ToBase64String(bytes);
    }

    public static double[]? Decode(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        try { return [.. Convert.FromBase64String(data).Select(value => (sbyte)value / 127.0)]; }
        catch (FormatException) { return null; }
    }

    /// <summary>Cosine similarity of two unit vectors (dot product), or null when either is missing or they differ in size.</summary>
    public static double? Similarity(double[]? left, double[]? right)
    {
        if (left is null || right is null || left.Length != right.Length || left.Length == 0) return null;
        double dot = 0;
        for (int index = 0; index < left.Length; index++) dot += left[index] * right[index];
        return dot;
    }

    /// <summary>The average direction of several vectors (for several removed files at once).</summary>
    public static double[]? Mean(IEnumerable<double[]?> vectors)
    {
        var present = vectors.Where(vector => vector is not null).ToArray();
        if (present.Length == 0 || present.Any(vector => vector!.Length != present[0]!.Length)) return null;
        var sum = new double[present[0]!.Length];
        foreach (var vector in present) for (int index = 0; index < sum.Length; index++) sum[index] += vector![index];
        double norm = Math.Sqrt(sum.Sum(value => value * value));
        return norm == 0 ? null : [.. sum.Select(value => value / norm)];
    }

    /// <summary>
    /// The research projects to choose from for what is being added or moved (kind and id), closest first: a suggested one
    /// (from the group's check against the projects) leads, then those ranked by closeness (the first few marked best),
    /// then the rest by their latest change. Ranked is false when no closeness is known yet.
    /// </summary>
    public static ChoicesResponse Choices(LocalResearchState state, Guid account, string kind, string id)
    {
        var vectors = (state.Vectors ?? []).Where(vector => vector.AccountId == account).ToDictionary(vector => vector.Key, vector => vector, StringComparer.Ordinal);
        double[]? Vector(string key) => vectors.TryGetValue(key, out var vector) ? Decode(vector.Data) : null;
        var cards = ThemePlan.Active(state, account).Where(card => card.Profile is not null).ToList();
        double[]? source = null;
        string? suggested = null, exclude = null;
        HousekeepingGroup? GroupOf(string? groupId) => groupId is null ? null : Housekeeping.Groups(state, account).FirstOrDefault(group => group.Id == groupId);
        switch (kind)
        {
            case "group":
                source = Vector(GroupKey(id));
                suggested = GroupOf(id)?.SuggestedCardId;
                break;
            case "thread":
                var thread = (state.GmailThreads ?? []).FirstOrDefault(item => item.AccountId == account && $"{item.Address}|{item.Id}" == id);
                if (GroupOf(thread?.GroupId) is { } group)
                {
                    source = Vector(GroupKey(group.Id));
                    suggested = group.SuggestedCardId;
                }
                break;
            case "files":
                exclude = id;
                source = Mean((state.PendingFiles ?? []).Where(file => file.AccountId == account && file.FromCardId == id && file.TargetCardId is null)
                    .Select(file => Vector(FileKey(file))));
                break;
            case "project":
                source = Vector(ProjectKey(id));
                exclude = cards.FirstOrDefault(card => card.Members.Contains(id))?.Id;
                break;
            case "card":
                source = Vector(CardKey(id));
                exclude = id;
                break;
            default:
                throw new ResearchFailure(400, "invalid_choice_request", "Choose what to add or move.");
        }
        cards.RemoveAll(card => card.Id == exclude);
        var scored = cards.Select(card => (Card: card, Score: Similarity(source, Vector(CardKey(card.Id))), Changed: LastChanged(card))).ToList();
        bool ranked = scored.Any(item => item.Score is not null);
        var ordered = scored.OrderByDescending(item => item.Card.Id == suggested)
            .ThenByDescending(item => item.Score is not null).ThenByDescending(item => item.Score ?? 0)
            .ThenByDescending(item => item.Changed ?? DateTimeOffset.MinValue).ToList();
        int best = 0;
        return new([.. ordered.Select(item =>
        {
            bool isBest = item.Score is not null && item.Card.Id != suggested && best++ < BestShown;
            var members = item.Card.Members.Select(member => state.Projects.FirstOrDefault(project => project.AccountId == account && project.ProjectId == member)?.Name)
                .Where(name => name is not null).Select(name => name!).ToArray();
            return new ChoiceResponse(item.Card.Id, item.Card.Profile!.Title, item.Card.Profile.Description, members, item.Changed, isBest, item.Card.Id == suggested);
        })], ranked);
    }

    private static DateTimeOffset? LastChanged(ResearchTheme card) =>
        (card.Details ?? []).SelectMany(detail => detail.Citations).Select(citation => citation.Source.At).Where(at => at is not null).Max();

    private static RankItem Text(string key, string text) => new(key, ResearchEvidence.Hash(text), text, null);

    private static string Clip(string text) => text.Length <= MaxText ? text : text[..MaxText];
}
