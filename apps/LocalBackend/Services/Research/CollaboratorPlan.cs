using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// A research project's collaborators (OVERLEAF.md §12a). Its people come from its Overleaf projects (who edited and who owns
/// them, read from Overleaf without a model) and its paper's author blocks, and from the senders and recipients of its email
/// threads; the user is left out by their own names and addresses. People who are known already (the same address, or
/// exactly the same name) are merged without a model; anything else new is merged by one Foundry call for the project, which
/// groups the people and picks each name as written. Unchanged people make no call.
/// </summary>
internal static class CollaboratorPlan
{
    /// <summary>How collaborators are made; a newer way makes every project's collaborators again.</summary>
    public const int Version = 1;
    /// <summary>At most this many people are kept per email thread.</summary>
    public const int MaxThreadPeople = 50;

    /// <summary>One person as a source names them, combined over sources by Key (an address, else a normalized name).</summary>
    internal sealed record Mention(string Key, string? Name, string? Email, IReadOnlySet<string> Kinds, int Count, DateTimeOffset? At);

    /// <summary>What a project's collaborators are made from, and its fingerprint.</summary>
    internal sealed record Inputs(IReadOnlyList<Mention> Mentions, string? Authors, IReadOnlyList<string> Self, string Print, string AuthorsFor);

    public static string Key(string? name, string? email) => email is not null ? "e:" + email : "n:" + Collaborators.Normalize(name ?? "");

    /// <summary>The senders and recipients of a thread other than the user, from messages that are not bulk mail (unless the user wrote them).</summary>
    public static IReadOnlyList<MailPerson> PeopleOf(GmailThread thread, IReadOnlySet<string> self)
    {
        var people = new Dictionary<string, MailPerson>(StringComparer.Ordinal);
        foreach (var message in thread.Messages.Where(message => !message.Bulk || message.FromUser))
            foreach (var (header, role) in new[] { (message.From, 0), (message.To, 1), (message.Cc, 2) })
                foreach (var (name, email) in EmailText.People(header))
                {
                    if (self.Contains(email)) continue;
                    var known = people.GetValueOrDefault(email);
                    people[email] = new(known?.Name ?? name, email, (known?.From ?? 0) + (role == 0 ? 1 : 0), (known?.To ?? 0) + (role == 1 ? 1 : 0),
                        (known?.Cc ?? 0) + (role == 2 ? 1 : 0), known is null || message.SentAt > known.At ? message.SentAt : known.At);
                }
        return [.. people.Values.OrderByDescending(person => person.From).ThenByDescending(person => person.To + person.Cc)
            .ThenBy(person => person.Email, StringComparer.Ordinal).Take(MaxThreadPeople)];
    }

    /// <summary>The user's own names and addresses: their Meld account, Gmail accounts and the owner of their Overleaf projects.</summary>
    public static (IReadOnlySet<string> Emails, IReadOnlySet<string> Names) Self(LocalResearchState state, Guid account)
    {
        var emails = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (state.Credential?.Account is { } me && me.Id == account)
        {
            if (!string.IsNullOrWhiteSpace(me.Email)) emails.Add(me.Email.Trim().ToLowerInvariant());
            if (Collaborators.Normalize(me.Name ?? "") is { Length: > 0 } name) names.Add(name);
        }
        foreach (var mail in (state.GmailAccounts ?? []).Where(mail => mail.AccountId == account && !mail.Removed)) emails.Add(mail.Address.ToLowerInvariant());
        foreach (var owner in state.Projects.Where(project => project.AccountId == account && project.Owned == true).Select(project => project.Owner).OfType<Contracts.OverleafPerson>())
        {
            if (owner.Email is { } email) emails.Add(email);
            if (owner.Name is { } ownerName && Collaborators.Normalize(ownerName) is { Length: > 0 } normalized) names.Add(normalized);
        }
        return (emails, names);
    }

    public static Inputs InputsOf(LocalResearchState state, Guid account, ResearchTheme theme)
    {
        var (selfEmails, selfNames) = Self(state, account);
        var members = theme.Members.Select(id => state.Projects.FirstOrDefault(project => project.AccountId == account && project.ProjectId == id))
            .OfType<MonitoredProject>().ToArray();
        var found = new List<Mention>();
        foreach (var project in members)
        {
            found.AddRange((project.Editors ?? []).Select(editor => new Mention(Key(editor.Name, editor.Email), editor.Name, editor.Email,
                new HashSet<string> { Collaborators.Editor }, 1, editor.At)));
            if (project.Owned != true && project.Owner is { } owner)
                found.Add(new(Key(owner.Name, owner.Email), owner.Name, owner.Email, new HashSet<string> { Collaborators.Owner }, 1, null));
        }
        foreach (var thread in (state.GmailThreads ?? []).Where(thread => thread.AccountId == account && thread.CardId == theme.Id &&
            thread.Status == GmailThreadRecord.Assigned))
            foreach (var person in thread.People ?? [])
            {
                var kinds = new HashSet<string>();
                if (person.From > 0) kinds.Add(Collaborators.From);
                if (person.To > 0) kinds.Add(Collaborators.To);
                if (person.Cc > 0) kinds.Add(Collaborators.Cc);
                if (kinds.Count != 0) found.Add(new(Key(person.Name, person.Email), person.Name, person.Email, kinds, person.From + person.To + person.Cc, person.At));
            }
        var mentions = found.Where(mention => mention.Key.Length > 2)
            .GroupBy(mention => mention.Key, StringComparer.Ordinal)
            .Select(group => new Mention(group.Key, group.Select(mention => mention.Name).OfType<string>().MaxBy(name => name.Length), group.First().Email,
                group.SelectMany(mention => mention.Kinds).ToHashSet(), group.Sum(mention => mention.Count), group.Max(mention => mention.At)))
            .Where(mention => !(mention.Email is { } email && selfEmails.Contains(email)) && !(mention.Name is { } name && selfNames.Contains(Collaborators.Normalize(name))))
            .OrderByDescending(mention => mention.Kinds.Contains(Collaborators.Editor) || mention.Kinds.Contains(Collaborators.Owner))
            .ThenByDescending(mention => mention.Kinds.Contains(Collaborators.From)).ThenByDescending(mention => mention.Count)
            .ThenByDescending(mention => mention.At).ThenBy(mention => mention.Key, StringComparer.Ordinal)
            .Take(Collaborators.MaxMentions).ToArray();
        var anchor = ThemePlan.AnchorOf(state, theme);
        string authors = string.Join('\n', members.OrderByDescending(member => member == anchor).Select(member => member.Paper?.Authors)
            .OfType<string>().Distinct(StringComparer.Ordinal));
        if (authors.Length > Collaborators.MaxAuthors) authors = authors[..Collaborators.MaxAuthors].TrimEnd();
        var self = selfEmails.Order(StringComparer.Ordinal).Concat(selfNames.Order(StringComparer.Ordinal)).Take(Collaborators.MaxSelf).ToArray();
        string authorsFor = ResearchEvidence.Hash(authors);
        string print = ResearchEvidence.Hash($"v{Version}|{authorsFor}|{string.Join(",", self)}|" + string.Join("|", mentions.Select(mention =>
            $"{mention.Key}:{mention.Name}:{string.Join(",", mention.Kinds.Order(StringComparer.Ordinal))}:{mention.Count}")));
        return new(mentions, authors.Length == 0 ? null : authors, self, print, authorsFor);
    }

    /// <summary>
    /// A project of the profile whose people changed since its collaborators were made, once what it is made from was read:
    /// its Overleaf projects' cycles finished (with their editors), its email threads folded and their people read.
    /// </summary>
    public static ResearchTheme? Due(LocalResearchState state, Guid account) =>
        ThemePlan.Active(state, account).FirstOrDefault(theme => Ready(state, account, theme) && theme.Collaborators?.For != InputsOf(state, account, theme).Print);

    private static bool Ready(LocalResearchState state, Guid account, ResearchTheme theme) =>
        theme.Members.All(member => state.Projects.FirstOrDefault(project => project.AccountId == account && project.ProjectId == member) is not { } project ||
            ThemePlan.Settled(project) && (project.Editors is not null || !project.IsOverleaf) && project.PaperFor == (project.Baseline is { } baseline ? Papers.ReadFor(baseline.Revision) : project.PaperFor)) &&
        !ThemePlan.FoldPending(state, theme) &&
        !(state.GmailThreads ?? []).Any(thread => thread.AccountId == account && thread.CardId == theme.Id && thread.Status == GmailThreadRecord.Assigned &&
            thread.People is null && (state.GmailAccounts ?? []).Any(mail => mail.AccountId == account && !mail.Removed && mail.Status == "connected" &&
                string.Equals(mail.Address, thread.Address, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The request for one Foundry call: the people (ids m1, m2, ...), the author blocks and the user's own names and addresses.</summary>
    public static CollaboratorsRequest Request(Inputs inputs, string project) =>
        new(project.Length > Collaborators.MaxProject ? project[..Collaborators.MaxProject].TrimEnd() : project, inputs.Self, inputs.Authors,
            [.. inputs.Mentions.Select((mention, index) => new PersonMention($"m{index + 1}", mention.Name, mention.Email,
                [.. mention.Kinds.Order(StringComparer.Ordinal)], Math.Max(1, mention.Count)))]);

    /// <summary>
    /// The collaborators when everyone new is known already: each person keeps the people merged into them, and a new person
    /// joins one with the same address or exactly the same name. Null when someone new needs Foundry, or the authors changed.
    /// </summary>
    public static CollaboratorSet? Incremental(CollaboratorSet? previous, Inputs inputs)
    {
        if (previous is null || previous.AuthorsFor != inputs.AuthorsFor) return null;
        var dropped = previous.Dropped.ToHashSet(StringComparer.Ordinal);
        var people = previous.People.Select(person => (Person: person, Mentions: new List<Mention>())).ToList();
        foreach (var mention in inputs.Mentions)
        {
            if (dropped.Contains(mention.Key)) continue;
            int index = people.FindIndex(item => item.Person.Keys.Contains(mention.Key));
            if (index < 0 && mention.Email is { } email) index = people.FindIndex(item => item.Person.Emails.Contains(email));
            if (index < 0 && mention.Name is { } name)
                index = people.FindIndex(item => item.Person.Name is { } known && Collaborators.Normalize(known) == Collaborators.Normalize(name));
            if (index < 0) return null;
            people[index].Mentions.Add(mention);
        }
        var result = people.Select(item => item.Mentions.Count == 0
                ? item.Person.Kinds.Contains(Collaborators.Author) ? item.Person with { Emails = [], Kinds = [Collaborators.Author], Count = 0, At = null, Keys = [] } : null
                : Person(item.Person.Name, item.Mentions, item.Person.Kinds.Contains(Collaborators.Author)))
            .OfType<Collaborator>().ToArray();
        return new(inputs.Print, inputs.AuthorsFor, Order(result), previous.Dropped);
    }

    /// <summary>The collaborators from Foundry's (cleaned) grouping of the request's people.</summary>
    public static CollaboratorSet Build(Inputs inputs, IReadOnlyList<CollaboratorGroup> groups, IReadOnlyList<string> dropped)
    {
        var byId = inputs.Mentions.Select((mention, index) => (Id: $"m{index + 1}", Mention: mention)).ToDictionary(item => item.Id, item => item.Mention, StringComparer.Ordinal);
        var people = groups.Select(group => Person(group.Name, [.. group.Mentions.Select(id => byId[id])], group.Author)).ToArray();
        return new(inputs.Print, inputs.AuthorsFor, Order(people), [.. dropped.Select(id => byId[id].Key)]);
    }

    /// <summary>Without Foundry: people with the same address or exactly the same name are one collaborator.</summary>
    public static CollaboratorSet Plain(Inputs inputs)
    {
        var groups = new List<List<Mention>>();
        foreach (var mention in inputs.Mentions)
        {
            var group = groups.FirstOrDefault(items => items.Any(item => item.Email is not null && item.Email == mention.Email ||
                item.Name is not null && mention.Name is not null && Collaborators.Normalize(item.Name) == Collaborators.Normalize(mention.Name)));
            if (group is null) groups.Add([mention]);
            else group.Add(mention);
        }
        return new(inputs.Print, inputs.AuthorsFor, Order([.. groups.Select(group => Person(null, group, false))]), []);
    }

    private static Collaborator Person(string? name, IReadOnlyList<Mention> mentions, bool author)
    {
        var kinds = mentions.SelectMany(mention => mention.Kinds).Concat(author ? [Collaborators.Author] : []).Distinct(StringComparer.Ordinal);
        return new(name ?? mentions.Select(mention => mention.Name).OfType<string>().MaxBy(known => known.Length),
            [.. mentions.Select(mention => mention.Email).OfType<string>().Distinct(StringComparer.Ordinal)],
            [.. kinds.OrderBy(kind => Array.IndexOf(KindOrder, kind))], mentions.Sum(mention => mention.Count),
            mentions.Max(mention => mention.At), [.. mentions.Select(mention => mention.Key)]);
    }

    private static readonly string[] KindOrder = [Collaborators.Author, Collaborators.Owner, Collaborators.Editor, Collaborators.From, Collaborators.To, Collaborators.Cc];

    /// <summary>Authors, owners and editors first, then by how often they appear, then by name.</summary>
    private static IReadOnlyList<Collaborator> Order(IEnumerable<Collaborator> people) =>
        [.. people.OrderByDescending(person => person.Kinds.Any(kind => kind is Collaborators.Author or Collaborators.Owner or Collaborators.Editor))
            .ThenByDescending(person => person.Kinds.Contains(Collaborators.From)).ThenByDescending(person => person.Count)
            .ThenBy(person => person.Name ?? person.Emails.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase)];
}
