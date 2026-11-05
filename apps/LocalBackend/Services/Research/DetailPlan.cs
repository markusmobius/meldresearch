using System.Text.Json;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// The Overleaf ingest of OVERLEAF.md: which root trees an ingest cycle folds (§3-§6), the next part to send (§7), and how a
/// step's edits, removed documents and removed sources change a research project's details (§7-§10).
/// </summary>
internal static class DetailPlan
{
    public const int MaxOutline = 300, MaxChangelog = 5000;

    /// <summary>The analyzed documents of a manifest that are readable and not excluded, by path.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> TextsAsync(IResearchStore store, MonitoredProject project, ProjectManifest manifest, CancellationToken ct)
    {
        var excluded = (project.ExcludedDocs ?? []).ToHashSet(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files.Where(file => file.Analyzed && !excluded.Contains(file.Path)))
            if (await store.ReadTextAsync(file.Sha256, ct) is { } text) texts[file.Path] = text;
        return texts;
    }

    /// <summary>
    /// The trees a cycle folds, oldest first: roots within the window (on a project with nothing folded yet, the newest root
    /// when none is), each with the documents not yet folded at their current version; a document shared by trees is sent once.
    /// </summary>
    public static IReadOnlyList<PendingTree> Trees(MonitoredProject project, ProjectManifest manifest, IReadOnlyDictionary<string, string> texts,
        DateTimeOffset now, TimeSpan window)
    {
        var hashes = Hashes(manifest);
        DateTimeOffset Edited(string path) =>
            manifest.EditTimes is { } times && times.TryGetValue(path, out var time) ? time : manifest.SourceUpdatedAt ?? manifest.ObservedAt;
        var roots = LatexTree.Roots(LatexTree.Graph(texts), texts, Edited);
        var selected = LatexTree.Selected(roots, now, window, fallback: project.Processed is not { Count: > 0 });
        var planned = new HashSet<string>(StringComparer.Ordinal);
        var trees = new List<PendingTree>();
        foreach (var root in selected)
        {
            var send = root.ReadingOrder.Where(path => hashes.TryGetValue(path, out var hash) && !Folded(project, path, hash) && planned.Add(path)).ToArray();
            if (send.Length != 0) trees.Add(new(root.Root, root.Date, root.ReadingOrder, send));
        }
        return trees;
    }

    public sealed record Part(PendingTree Tree, IReadOnlyList<PartDocument> Documents, IReadOnlySet<string> Completes, IReadOnlyList<string> Outline);

    /// <summary>The next part of the cycle: the first tree with documents left, its next sections up to the part size.</summary>
    public static Part? Next(MonitoredProject project, IReadOnlyDictionary<string, string> texts)
    {
        if (project.Pending is not { } manifest) return null;
        var hashes = Hashes(manifest);
        foreach (var tree in project.PendingTrees ?? [])
        {
            var documents = tree.Send.Where(path => texts.ContainsKey(path) && hashes.TryGetValue(path, out var hash) && !Folded(project, path, hash)).ToArray();
            if (documents.Length == 0) continue;
            var segments = new List<PartDocument>();
            var last = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var path in documents)
            {
                var pieces = LatexTree.Pack([(path, texts[path])]).SelectMany(part => part).ToArray();
                last[path] = pieces.Max(piece => piece.FirstLine);
                int done = project.Partial is { } partial && partial.TryGetValue(path, out var marker) && marker.Split(':') is [var hash, var line] &&
                    hash == hashes[path] && int.TryParse(line, out var parsed) ? parsed : 0;
                segments.AddRange(pieces.Where(piece => piece.FirstLine > done));
            }
            var part = new List<PartDocument>();
            int size = 0;
            foreach (var segment in segments)
            {
                if (part.Count != 0 && size + segment.Text.Length > LatexTree.PartCharacters) break;
                part.Add(segment);
                size += segment.Text.Length;
            }
            var sent = part.Select(segment => segment.Path).ToHashSet(StringComparer.Ordinal);
            var outline = tree.Order.Take(MaxOutline).Select(path =>
                $"{path} [{(sent.Contains(path) ? "sent" : !hashes.ContainsKey(path) ? "removed" : Folded(project, path, hashes[path]) ? "already read" : tree.Send.Contains(path) ? "later" : "unchanged")}]").ToArray();
            return new(tree, part, part.Where(segment => last[segment.Path] == segment.FirstLine).Select(segment => segment.Path).ToHashSet(StringComparer.Ordinal), outline);
        }
        return null;
    }

    /// <summary>Documents and their parts still to fold in this cycle (for progress).</summary>
    public static (int Done, int Total) Progress(MonitoredProject project)
    {
        if (project.Pending is not { } manifest || project.PendingTrees is not { } trees) return (0, 0);
        var hashes = Hashes(manifest);
        var send = trees.SelectMany(tree => tree.Send).Distinct().ToArray();
        return (send.Count(path => !hashes.TryGetValue(path, out var hash) || Folded(project, path, hash)), send.Length);
    }

    public static IReadOnlyDictionary<string, string> Hashes(ProjectManifest manifest) =>
        manifest.Files.Where(file => file.Analyzed).ToDictionary(file => file.Path, file => file.Sha256, StringComparer.Ordinal);

    private static bool Folded(MonitoredProject project, string path, string hash) =>
        project.Processed is { } processed && processed.TryGetValue(path, out var folded) && folded == hash;

    /// <summary>
    /// Details as sent to a step: each citation gets a ref (c0, c1, ...) and its source's date; Outdated marks an older version
    /// of a sent document.
    /// </summary>
    public static IReadOnlyList<DetailInput> Inputs(IReadOnlyList<DetailBullet> details, Func<DetailSource, bool>? outdated = null) =>
        [.. details.Select(detail => new DetailInput(detail.Id, detail.Text, [.. detail.Citations.Select((citation, index) =>
            new DetailCitationInput($"c{index}", Label(citation.Source), outdated?.Invoke(citation.Source) == true, citation.Source.At))]))];

    public static string Label(DetailSource source) => source.Kind == DetailSource.Overleaf ? source.Label : "email: " + source.Label;

    /// <summary>
    /// Applies a step's edits, removals first (so a full list makes room before additions); a detail left without citations
    /// is removed, and an addition beyond <see cref="DetailRules.MaxDetails"/> is not made. Returns the details and what changed.
    /// With <paramref name="by"/>, each quote records who it is credited to (who wrote it), and each change who it rests on:
    /// the people behind the quotes it adds, else (a removal, or a rewording without new quotes) everyone behind the step
    /// (<paramref name="everyone"/>). A revision that keeps the wording only quotes the source again (after someone rewrote
    /// it): its new quotes keep the credit of the detail's earlier quotes, so rewording earns no credit (OVERLEAF.md §12a).
    /// </summary>
    public static (IReadOnlyList<DetailBullet> Details, IReadOnlyList<DetailChange> Changes) Apply(IReadOnlyList<DetailBullet> details,
        DetailStepResponse response, Func<DetailQuote, DetailSource> source, Func<DetailQuote, IEnumerable<string>?>? by = null,
        IReadOnlyList<string>? everyone = null)
    {
        var result = details.ToList();
        var changes = new List<DetailChange>();
        foreach (var op in response.Ops.OrderBy(op => DetailRules.Order(op.Op)))
        {
            int before0 = changes.Count;
            var citations = op.Add.Select(quote => new DetailCitation(source(quote), quote.Quote,
                by?.Invoke(quote) is { } people ? [.. people.Distinct(StringComparer.Ordinal)] : null)).ToArray();
            switch (op.Op)
            {
                case DetailRules.Add when result.Count < DetailRules.MaxDetails:
                    var added = new DetailBullet(NewId(), op.Text!, Distinct(citations));
                    result.Add(added);
                    changes.Add(new(DetailRules.Add, added.Id, null, added.Text));
                    break;
                case DetailRules.Revise:
                    int index = result.FindIndex(detail => detail.Id == op.Id);
                    if (index < 0) break;
                    var before = result[index];
                    if (by is not null && (op.Text is null || op.Text == before.Text) && Inherited(before, citations) is { Count: > 0 } kept0)
                        citations = [.. citations.Select(citation => citation with { By = kept0 })];
                    var removed = op.Remove.Select(item => int.TryParse(item.AsSpan(1), out var position) ? position : -1).ToHashSet();
                    var kept = before.Citations.Where((_, position) => !removed.Contains(position)).Concat(citations).ToArray();
                    if (kept.Length == 0)
                    {
                        result.RemoveAt(index);
                        changes.Add(new(DetailRules.Remove, before.Id, before.Text, null));
                        break;
                    }
                    var after = before with { Text = op.Text ?? before.Text, Citations = Distinct(kept) };
                    result[index] = after;
                    changes.Add(new(DetailRules.Revise, after.Id, before.Text, after.Text));
                    break;
                case DetailRules.Remove:
                    int gone = result.FindIndex(detail => detail.Id == op.Id);
                    if (gone < 0) break;
                    changes.Add(new(DetailRules.Remove, result[gone].Id, result[gone].Text, null));
                    result.RemoveAt(gone);
                    break;
            }
            if (by is null || changes.Count == before0) continue;
            var people = citations.SelectMany(citation => citation.By ?? []).Distinct(StringComparer.Ordinal).ToArray();
            var change = changes[^1];
            changes[^1] = change with { By = change.Op != DetailRules.Remove && people.Length != 0 ? people : everyone is { Count: > 0 } ? everyone : null };
        }
        return (result, changes);
    }

    /// <summary>
    /// The credit a re-quote of a detail keeps: that of its earlier quotes from the same sources as the new ones, else of all
    /// its earlier quotes; empty when none was recorded.
    /// </summary>
    private static IReadOnlyList<string> Inherited(DetailBullet before, IReadOnlyList<DetailCitation> added)
    {
        var sources = added.Select(citation => citation.Source.Key).ToHashSet(StringComparer.Ordinal);
        var same = before.Citations.Where(citation => sources.Contains(citation.Source.Key) && citation.By is { Count: > 0 }).ToArray();
        return [.. (same.Length != 0 ? same : before.Citations).SelectMany(citation => citation.By ?? []).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The changes, with those that do not record who they rest on credited to everyone behind the step.</summary>
    public static IReadOnlyList<DetailChange> By(IReadOnlyList<DetailChange> changes, IReadOnlyList<string> everyone) =>
        everyone.Count == 0 ? changes : [.. changes.Select(change => change.By is null ? change with { By = everyone } : change)];

    /// <summary>
    /// Applies a review (OVERLEAF.md §8a): each kept detail keeps the id and place of the first detail it came from and the
    /// sources of all of them; details it names none of are retired. Returns the details and what changed.
    /// </summary>
    public static (IReadOnlyList<DetailBullet> Details, IReadOnlyList<DetailChange> Changes) Review(IReadOnlyList<DetailBullet> details,
        DetailReviewResponse response)
    {
        var byId = details.ToDictionary(detail => detail.Id, StringComparer.Ordinal);
        var position = details.Select((detail, index) => (detail.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var kept = new List<(int Position, DetailBullet Detail)>();
        var changes = new List<DetailChange>();
        foreach (var item in response.Details)
        {
            var from = item.From.Where(byId.ContainsKey).ToArray();
            if (from.Length == 0) continue;
            var first = byId[from[0]];
            var merged = first with
            {
                Text = item.Text,
                Citations = [.. from.SelectMany(id => byId[id].Citations).DistinctBy(citation => (citation.Source.Key, citation.Source.Version, citation.Quote)).Take(12)]
            };
            kept.Add((position[first.Id], merged));
            if (merged.Text != first.Text) changes.Add(new(DetailRules.Revise, first.Id, first.Text, merged.Text));
            foreach (var id in from.Skip(1)) changes.Add(new(DetailRules.Remove, id, byId[id].Text, null));
        }
        var used = response.Details.SelectMany(item => item.From).ToHashSet(StringComparer.Ordinal);
        foreach (var detail in details.Where(detail => !used.Contains(detail.Id))) changes.Add(new(DetailRules.Remove, detail.Id, detail.Text, null));
        return ([.. kept.OrderBy(item => item.Position).Select(item => item.Detail)], changes);
    }

    /// <summary>Removes citations matching <paramref name="drop"/>; details left without citations are removed.</summary>
    public static (IReadOnlyList<DetailBullet> Details, IReadOnlyList<DetailChange> Changes) Without(IReadOnlyList<DetailBullet> details,
        Func<DetailSource, bool> drop)
    {
        var result = new List<DetailBullet>();
        var changes = new List<DetailChange>();
        foreach (var detail in details)
        {
            var kept = detail.Citations.Where(citation => !drop(citation.Source)).ToArray();
            if (kept.Length == detail.Citations.Count) result.Add(detail);
            else if (kept.Length == 0) changes.Add(new(DetailRules.Remove, detail.Id, detail.Text, null));
            else result.Add(detail with { Citations = kept });
        }
        return (result, changes);
    }

    /// <summary>
    /// What the details say and rest on, without who they are credited to: crediting (OVERLEAF.md §12a) never makes a
    /// summary, review or ranking due. Details never credited hash as they always did.
    /// </summary>
    public static string Fingerprint(IReadOnlyList<DetailBullet>? details) =>
        ResearchEvidence.Hash(JsonSerializer.Serialize((details ?? []).Select(detail => detail with
        {
            Citations = [.. detail.Citations.Select(citation => citation.By is null ? citation : citation with { By = null })]
        }).ToArray(), ResearchJson.Options));

    /// <summary>
    /// Whether a stored fingerprint is of these details: the current way, or the way that also hashed their credit (written
    /// by the version before), so cards credited back then are not summarized or reviewed again.
    /// </summary>
    public static bool Matches(string? stored, IReadOnlyList<DetailBullet>? details) =>
        stored is not null && (stored == Fingerprint(details) ||
            (details ?? []).Any(detail => detail.Citations.Any(citation => citation.By is not null)) &&
            stored == ResearchEvidence.Hash(JsonSerializer.Serialize(details ?? [], ResearchJson.Options)));

    /// <summary>Adds a changelog entry, or merges it into the entry with the same key (parts of one root tree in one cycle).</summary>
    public static IReadOnlyList<ChangelogEntry> Log(IReadOnlyList<ChangelogEntry>? log, ChangelogEntry entry)
    {
        var entries = (log ?? []).ToList();
        int index = entry.Key is null ? -1 : entries.FindIndex(item => item.Key == entry.Key && item.CardId == entry.CardId && item.AccountId == entry.AccountId);
        if (index >= 0)
            entries[index] = entries[index] with
            {
                Documents = [.. entries[index].Documents.Concat(entry.Documents).Distinct(StringComparer.Ordinal)],
                Changes = [.. entries[index].Changes, .. entry.Changes], RecordedAt = entry.RecordedAt
            };
        else entries.Add(entry);
        // The oldest entries of an account go first beyond the limit.
        int excess = entries.Count(item => item.AccountId == entry.AccountId) - MaxChangelog;
        if (excess > 0)
        {
            var dropped = entries.Where(item => item.AccountId == entry.AccountId).OrderBy(item => item.At).Take(excess).ToHashSet();
            entries.RemoveAll(dropped.Contains);
        }
        return entries;
    }

    private static IReadOnlyList<DetailCitation> Distinct(IEnumerable<DetailCitation> citations) =>
        [.. citations.DistinctBy(citation => (citation.Source.Key, citation.Source.Version, citation.Quote)).Take(8)];

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
