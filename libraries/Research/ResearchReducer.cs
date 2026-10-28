namespace MeldResearch.Research;

public sealed record ResearchReduction(IReadOnlyList<ResearchFact> Facts, IReadOnlyList<ResearchUpdate> Updates);

public static class ResearchReducer
{
    public static ResearchReduction Apply(AnalyzeResearchRequest request, AnalyzeResearchResponse response,
        IReadOnlyList<ResearchFact> existing)
    {
        ResearchEvidence.ValidateAnalysis(request, response.Analysis);
        int count = response.Analysis.Changes.Count(change => change.Operation != FactOperation.Remove);
        ResearchEvidence.ValidateEmbeddings(response.Embeddings, count);
        if (string.IsNullOrWhiteSpace(response.ChatModel) || string.IsNullOrWhiteSpace(response.EmbeddingModel))
            throw new ResearchFailure(502, "missing_model_identity", "The inference response did not identify its model deployments.");
        var facts = existing.ToDictionary(fact => fact.Id);
        var updates = new List<ResearchUpdate>();
        int vector = 0;
        foreach (var change in response.Analysis.Changes)
        {
            ResearchFact? previous = null;
            if (change.ExistingFactId is { } existingId && !facts.TryGetValue(existingId, out previous))
                throw new ResearchFailure(409, "research_state_changed", "The project state changed while this update was being processed.");
            Guid id = previous?.Id ?? Guid.NewGuid();
            if (change.Operation == FactOperation.Remove)
                facts.Remove(id);
            else
                facts[id] = new(id, change.Title, change.Summary, change.Tags, change.Evidence,
                    request.Current.Revision, response.EmbeddingModel, response.Embeddings[vector++]);
            var kind = request.Previous is null ? ResearchUpdateKind.InitialImport : change.Operation switch
            {
                FactOperation.Revise => ResearchUpdateKind.Change,
                FactOperation.Remove => ResearchUpdateKind.Removal,
                _ => ResearchUpdateKind.Change
            };
            updates.Add(new(Guid.NewGuid(), request.Current.ProjectId, request.Current.ProjectName, kind,
                change.Title, change.Summary, request.Current.ObservedAt, request.Current.SourceUpdatedAt,
                request.Previous?.Revision, request.Current.Revision, id, change.Evidence, previous?.Summary));
        }
        if (facts.Count > ResearchEvidence.MaxFacts)
            throw new ResearchFailure(413, "project_knowledge_limit", "This project exceeds the supported knowledge size. Its previous state was preserved.");
        return new(facts.Values.ToArray(), updates);
    }

    public static IReadOnlyList<ResearchFactContext> Candidates(ResearchSnapshot previous, ResearchSnapshot current,
        IReadOnlyList<ResearchFact> facts, ResearchEmbeddingResponse query)
    {
        ResearchEvidence.ValidateEmbeddings(query.Embeddings, 1);
        if (facts.Any(fact => fact.EmbeddingModel != query.Model))
            throw new ResearchFailure(409, "embedding_model_changed",
                "The configured embedding model changed. Rebuild this project's index before processing another delta.");
        var changed = ResearchEvidence.ChangedPaths(previous, current).ToHashSet(StringComparer.Ordinal);
        return facts.OrderByDescending(fact => ResearchEvidence.Cosine(fact.Embedding, query.Embeddings[0])).Take(12)
            .Concat(facts.Where(fact => fact.Evidence.Any(citation => changed.Contains(citation.Path))))
            .DistinctBy(fact => fact.Id)
            .Select(fact => new ResearchFactContext(fact.Id, fact.Title, fact.Summary, fact.Tags, fact.Evidence)).ToArray();
    }
}
