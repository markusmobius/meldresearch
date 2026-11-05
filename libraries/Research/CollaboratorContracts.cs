using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MeldResearch.Research;

/// <summary>
/// One way a person appears on a research project: a name and/or an email address, how they appear (Kinds: editor or owner
/// of an Overleaf project, from/to/cc on its email threads) and how often (Count). Points: up to three short quotes of what
/// they wrote in the project's email that Meld used (cited in its details or a thread's preview), to judge their involvement.
/// </summary>
public sealed record PersonMention(string Id, string? Name, string? Email, IReadOnlyList<string> Kinds, int Count,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Points = null);

/// <summary>
/// The people of one research project, to be merged into collaborators (one call per project, only when its people changed).
/// Self: the researcher's own names and email addresses. Authors: the author blocks of the project's papers as plain text.
/// </summary>
public sealed record CollaboratorsRequest(string Project, IReadOnlyList<string> Self, string? Authors, IReadOnlyList<PersonMention> Mentions);

/// <summary>
/// One person: the mentions that are this person, their name as written (or null), whether they are an author, and whether
/// their email shows substantive work on the research (Involved; not scheduling, forwarding, acknowledging or receiving).
/// </summary>
public sealed record CollaboratorGroup(string? Name, IReadOnlyList<string> Mentions, bool Author, bool Involved = false);

/// <summary>People: the collaborators. Dropped: mentions of the researcher or of no collaborator (lists, role addresses).</summary>
public sealed record CollaboratorsResponse(IReadOnlyList<CollaboratorGroup> People, IReadOnlyList<string> Dropped, string ChatModel);

/// <summary>
/// Collaborators of a research project (OVERLEAF.md §12a): the people from its Overleaf projects (editors, owner, paper
/// authors) and its email threads (from, to, cc), with duplicates merged. Names and email addresses come only from the
/// input: the model groups mentions and picks a name as written; it never invents either.
/// </summary>
public static partial class Collaborators
{
    public const int MaxMentions = 150, MaxSelf = 20, MaxAuthors = 4000, MaxName = 200, MaxEmail = 320, MaxProject = 300, MaxPoints = 3, MaxPoint = 300;
    public const string Editor = "editor", Owner = "owner", Author = "author", From = "from", To = "to", Cc = "cc";
    private static readonly HashSet<string> MentionKinds = [Editor, Owner, From, To, Cc];

    public static void ValidateRequest(CollaboratorsRequest request)
    {
        bool Valid(PersonMention mention) => mention is not null && IsId(mention.Id) &&
            (mention.Name is not null || mention.Email is not null) &&
            (mention.Name is null || !string.IsNullOrWhiteSpace(mention.Name) && mention.Name.Length <= MaxName) &&
            (mention.Email is null || mention.Email.Length <= MaxEmail && mention.Email.Contains('@')) &&
            mention.Kinds is { Count: > 0 and <= 5 } && mention.Kinds.All(kind => kind is not null && MentionKinds.Contains(kind)) && mention.Count >= 1 &&
            (mention.Points is null || mention.Points.Count <= MaxPoints && mention.Points.All(point => !string.IsNullOrWhiteSpace(point) && point.Length <= MaxPoint));
        if (request is null || string.IsNullOrWhiteSpace(request.Project) || request.Project.Length > MaxProject ||
            request.Self is not { Count: <= MaxSelf } || request.Self.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > MaxEmail) ||
            request.Authors is { Length: > MaxAuthors } || request.Mentions is not { Count: <= MaxMentions } ||
            request.Mentions.Count == 0 && string.IsNullOrWhiteSpace(request.Authors) || !request.Mentions.All(Valid) ||
            request.Mentions.Select(mention => mention.Id).Distinct(StringComparer.Ordinal).Count() != request.Mentions.Count)
            throw new ResearchFailure(400, "invalid_collaborators", "The collaborators request is invalid.");
    }

    /// <summary>
    /// The model's answer made safe: each mention in at most one person (later claims ignored), unknown ids ignored, a name
    /// kept only if it is one of the person's mention names or appears in the author blocks (else the fullest mention name),
    /// and mentions the model left out become people of their own. A person without mentions must be a named author.
    /// </summary>
    public static (IReadOnlyList<CollaboratorGroup> People, IReadOnlyList<string> Dropped) Clean(CollaboratorsRequest request, CollaboratorsResponse response)
    {
        var mentions = request.Mentions.ToDictionary(mention => mention.Id, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        string authors = Normalize(request.Authors ?? "");
        var dropped = (response.Dropped ?? []).Where(id => id is not null && mentions.ContainsKey(id) && used.Add(id)).ToList();
        var people = new List<CollaboratorGroup>();
        foreach (var group in response.People ?? [])
        {
            if (group is null) continue;
            var ids = (group.Mentions ?? []).Where(id => id is not null && mentions.ContainsKey(id) && used.Add(id)).ToArray();
            string? fallback = ids.Select(id => mentions[id].Name).OfType<string>().MaxBy(name => name.Length);
            string? name = group.Name is { } chosen && chosen.Trim() is { Length: > 0 and <= MaxName } trimmed &&
                (ids.Any(id => mentions[id].Name is { } known && Normalize(known) == Normalize(trimmed)) ||
                 authors.Length != 0 && Normalize(trimmed) is { Length: >= 3 } normalized && $" {authors} ".Contains($" {normalized} ", StringComparison.Ordinal))
                ? trimmed : fallback;
            if (ids.Length == 0 && (!group.Author || name is null)) continue;
            if (ids.Length == 0 && people.Any(person => person.Name is not null && Normalize(person.Name) == Normalize(name!))) continue;
            people.Add(new(name, ids, group.Author && authors.Length != 0, group.Involved));
        }
        people.AddRange(request.Mentions.Where(mention => !used.Contains(mention.Id)).Select(mention => new CollaboratorGroup(mention.Name, [mention.Id], false, false)));
        return (people, dropped);
    }

    /// <summary>A name or text for comparison: lower case, without accents and punctuation, single spaces.</summary>
    public static string Normalize(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (char character in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return Spaces().Replace(plain.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Mention ids are "m" and a number.</summary>
    public static bool IsId(string? id) => id is { Length: >= 2 and <= 8 } && id[0] == 'm' && id[1..].All(char.IsAsciiDigit);
}
