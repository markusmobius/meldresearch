using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeldResearch.Research;

public static class ResearchJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
}

public sealed class ResearchFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public enum FactOperation { Add, Revise, Remove }
public enum EvidenceVersion { Current, Previous }
public enum ResearchUpdateKind { InitialImport, Change, Correction, Removal }

/// <summary>A hashed archive member; only supported UTF-8 text sources have text content.</summary>
public sealed record ResearchFile(string Path, string Sha256, long Bytes, string? Text);

/// <summary>A complete observed file manifest and the bounded, supported text from one Overleaf project.</summary>
public sealed record ResearchSnapshot(string ProjectId, string ProjectName, string Revision,
    DateTimeOffset ObservedAt, DateTimeOffset? SourceUpdatedAt, IReadOnlyList<ResearchFile> Files);

/// <summary>An exact quotation from a named file in the current or previous project snapshot.</summary>
public sealed record ResearchCitation(string Path, string Quote, EvidenceVersion Version);

/// <summary>Existing project knowledge presented to the model for semantic matching and correction.</summary>
public sealed record ResearchFactContext(Guid Id, string Title, string Summary, IReadOnlyList<string> Tags,
    IReadOnlyList<ResearchCitation> Evidence);

/// <summary>A source-neutral fact with Overleaf provenance and a Foundry embedding.</summary>
public sealed record ResearchFact(Guid Id, string Title, string Summary, IReadOnlyList<string> Tags,
    IReadOnlyList<ResearchCitation> Evidence, string Revision, string EmbeddingModel, IReadOnlyList<float> Embedding);

/// <summary>Evidence for a baseline import or a content delta; it is data, never executable instructions.</summary>
public sealed record AnalyzeResearchRequest(ResearchSnapshot? Previous, ResearchSnapshot Current,
    string? PreviousSummary, IReadOnlyList<ResearchFactContext> KnownFacts);

/// <summary>A model-proposed addition, revision, or removal of existing research knowledge.</summary>
public sealed record ResearchChange(FactOperation Operation, Guid? ExistingFactId, string Title,
    string Summary, IReadOnlyList<string> Tags, IReadOnlyList<ResearchCitation> Evidence);

/// <summary>Structured Foundry analysis of the project and its meaningful changes.</summary>
public sealed record ResearchAnalysis(string ProjectSummary, string Explanation, IReadOnlyList<ResearchChange> Changes);

/// <summary>Validated analysis and embeddings; the remote service does not persist project evidence.</summary>
public sealed record AnalyzeResearchResponse(ResearchAnalysis Analysis, string ChatModel, string EmbeddingModel,
    IReadOnlyList<IReadOnlyList<float>> Embeddings);

/// <summary>Bounded text to embed using the server's configured Foundry embedding deployment.</summary>
public sealed record ResearchEmbeddingRequest(IReadOnlyList<string> Input);

/// <summary>Foundry vectors in exactly the same order as the input texts.</summary>
public sealed record ResearchEmbeddingResponse(string Model, IReadOnlyList<IReadOnlyList<float>> Embeddings);

/// <summary>An Updates-only feed item. ObservedAt is observation/import time, not an invented research-event date.</summary>
public sealed record ResearchUpdate(Guid Id, string ProjectId, string ProjectName, ResearchUpdateKind Kind,
    string Title, string Summary, DateTimeOffset ObservedAt, DateTimeOffset? SourceUpdatedAt,
    string? PreviousRevision, string Revision, Guid FactId, IReadOnlyList<ResearchCitation> Evidence,
    string? PreviousSummary);

/// <summary>The remote account to which this local installation is linked.</summary>
public sealed record MeldAccountResponse(Guid Id, string Name, string Email);

/// <summary>
/// Redeems the one-time code that the website delivered to the local installation's loopback callback. The verifier
/// proves that this installation started the sign-in (PKCE): the website only saw its SHA-256 challenge.
/// </summary>
public sealed record RedeemSignInRequest(string Code, string Verifier);

/// <summary>The installation credential issued for the signed-in Meld account.</summary>
public sealed record RedeemSignInResponse(string AccessToken, DateTimeOffset ExpiresAt, MeldAccountResponse Account);

/// <summary>Non-secret capabilities of the remote service.</summary>
public sealed record RemoteStatusResponse(bool Dummy, bool GoogleConfigured, bool FoundryConfigured,
    string PrivacyUrl, string ExtensionId, string ExtensionStoreUrl);
