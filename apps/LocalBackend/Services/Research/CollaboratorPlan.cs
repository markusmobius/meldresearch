using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

/// <summary>
/// A research project's collaborators (OVERLEAF.md §12a). Its people come from its Overleaf projects (who edited and who owns
/// them, read from Overleaf without a model) and its paper's author blocks, and from the senders and recipients of its email
/// threads; the user is left out by their own names and addresses. A collaborator is an author, someone who edited the
/// project, or someone whose email shows substantive work on the research (judged by the model from what they wrote that Meld
/// used); recipients, administrators and owners who never edited are not. People who are known already (the same address,
/// or exactly the same name) are merged without a model; anything else new that could matter is merged and judged by one
/// Foundry call for the project. Unchanged people make no call.
/// </summary>
internal static class CollaboratorPlan
{
    /// <summary>How collaborators are made; a newer way makes every project's collaborators again (Version 2: substantive involvement only).</summary>
    public const int Version = 2;
    /// <summary>
    /// How Overleaf editors are read (connector 0.6.11 reports them); a project read another way reads its history again.
    /// Version 2: editors stored as "none" by an older connector during the update are read again. Version 3: who edited
    /// each file (connector 0.6.12), for the shares of details read before. Version 4: who added each file (connector 0.6.14).
    /// </summary>
    public const int EditorsVersion = 4;
    /// <summary>At most this many people are kept per email thread, and this many of each person's sent messages.</summary>
    public const int MaxThreadPeople = 50, MaxSent = 30;

    /// <summary>
    /// One person as a source names them, combined over sources by Key (an address, else a normalized name). Points: quotes of
    /// what they wrote in the project's email that Meld used.
    /// </summary>
    internal sealed record Mention(string Key, string? Name, string? Email, IReadOnlySet<string> Kinds, int Count, DateTimeOffset? At,
        IReadOnlyList<string> Points)
    {
        public string PointsFor => ResearchEvidence.Hash(string.Join("\n", Points));
        /// <summary>Can be a collaborator without being an author: edited the project, or wrote something Meld used.</summary>
        public bool Matters => Kinds.Contains(Collaborators.Editor) || Kinds.Contains(Collaborators.From) && Points.Count != 0;
    }

    /// <summary>What a project's collaborators are made from, and its fingerprint.</summary>
    internal sealed record Inputs(IReadOnlyList<Mention> Mentions, string? Authors, IReadOnlyList<string> Self, string Print, string AuthorsFor);

    public static string Key(string? name, string? email) => email is not null ? "e:" + email : "n:" + Collaborators.Normalize(name ?? "");
    /// <summary>The key of the user in DetailChange.By.</summary>
    public const string SelfKey = "self";
    /// <summary>How Overleaf quotes are credited: version 1 by who wrote the quoted lines (Overleaf's history diff).</summary>
    public const int CreditVersion = 1;
    /// <summary>A person who wrote less than this share of a quote's characters (for example a typo fix) is not credited.</summary>
    public const double MinCreditShare = 0.25;

    /// <summary>A line's key in FileAuthorship: the first 6 bytes of the SHA-256 of its trimmed text, in hex (as the connector computes it).</summary>
    public static string LineHash(string line) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(line.Trim())))[..12];

    /// <summary>
    /// Who wrote a quote (OVERLEAF.md §12a): its lines' characters by author, with text without an author credited to
    /// <paramref name="adder"/> (who added the file, else the project's owner). Everyone with at least
    /// <see cref="MinCreditShare"/> of the characters is credited, and at least the one who wrote the most. Null when none
    /// of the quote's lines are found (the file changed since, or the authorship could not be read).
    /// </summary>
    public static IReadOnlyList<string>? LineCredit(string quote, FileAuthorship authorship, OverleafPerson? adder,
        (IReadOnlySet<string> Emails, IReadOnlySet<string> Names) self)
    {
        var chars = new Dictionary<string, int>(StringComparer.Ordinal);
        string? Who(int index)
        {
            var person = index >= 0 && index < authorship.Users.Count ? authorship.Users[index] : adder;
            if (person is null || person.Name is null && person.Email is null) return null;
            return IsSelf(self, person.Name, person.Email) ? SelfKey : Key(person.Name, person.Email);
        }
        bool found = false;
        foreach (string line in quote.Split('\n').Select(line => line.Trim()).Where(line => line.Length != 0))
        {
            if (!authorship.Lines.TryGetValue(LineHash(line), out var counts)) continue;
            found = true;
            foreach (var (index, count) in counts)
                if (Who(index) is { } key) chars[key] = chars.GetValueOrDefault(key) + count;
        }
        if (!found || chars.Count == 0) return null;
        int total = chars.Values.Sum(), most = chars.Values.Max();
        return [.. chars.Where(item => item.Value == most || item.Value >= MinCreditShare * total)
            .OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Key)];
    }

    /// <summary>Who added a file of a project (credited with text without an author): the history's adder, else the project's owner.</summary>
    public static OverleafPerson? Adder(MonitoredProject project, ProjectManifest? manifest, string path) =>
        manifest?.FileAdders?.GetValueOrDefault(path) ?? project.Baseline?.FileAdders?.GetValueOrDefault(path) ?? project.Owner;

    /// <summary>
    /// How a change's people are shown: "You" first, then the card's collaborators by name (else address), then "Others" for
    /// anyone who is no collaborator. Null when nobody is known.
    /// </summary>
    public static IReadOnlyList<string>? Names(IReadOnlyList<string>? keys, ResearchTheme? card)
    {
        if (keys is not { Count: > 0 }) return null;
        var people = card?.Collaborators?.People ?? [];
        var names = new List<string>();
        bool you = false, others = false;
        foreach (var key in keys)
        {
            if (key == SelfKey) { you = true; continue; }
            var person = people.FirstOrDefault(item => item.Keys.Contains(key) || key.StartsWith("e:", StringComparison.Ordinal) && item.Emails.Contains(key[2..]));
            if ((person?.Name ?? person?.Emails.FirstOrDefault()) is { } name) { if (!names.Contains(name)) names.Add(name); }
            else others = true;
        }
        if (you) names.Insert(0, "You");
        if (others) names.Add("Others");
        return names;
    }

    /// <summary>
    /// Who an email update recorded before changes kept it rests on: the senders of the messages its detail cites now (a
    /// message no other person on the thread sent is the user's). Null when unknown.
    /// </summary>
    public static IReadOnlyList<string>? EmailBy(DetailChange change, ChangelogEntry entry, ResearchTheme? card, LocalResearchState state)
    {
        if (entry.Kind != ChangelogEntry.Gmail || change.Op == DetailRules.Remove || card?.Details?.FirstOrDefault(detail => detail.Id == change.DetailId) is not { } detail)
            return null;
        var keys = new List<string>();
        foreach (var source in detail.Citations.Select(citation => citation.Source).Where(source => source.Kind == DetailSource.Gmail))
        {
            var thread = (state.GmailThreads ?? []).FirstOrDefault(item => item.AccountId == card.AccountId && item.Id == source.Item &&
                string.Equals(item.Address, source.Container, StringComparison.OrdinalIgnoreCase));
            if (thread is null || PeopleUnread(thread)) continue;
            var sender = thread.People!.FirstOrDefault(person => person.Sent?.Contains(source.Version) == true);
            string key = sender is null ? SelfKey : Key(sender.Name, sender.Email);
            if (!keys.Contains(key)) keys.Add(key);
        }
        return keys.Count == 0 ? null : keys;
    }

    /// <summary>
    /// Who one source of a detail rests on: as recorded when it was cited, else for an email the sender of the cited message
    /// (a message no other person on the thread sent is the user's), and for an Overleaf file everyone who edited it. Empty
    /// when unknown.
    /// </summary>
    public static IReadOnlyList<string> CitedBy(DetailCitation citation, LocalResearchState state, Guid account,
        (IReadOnlySet<string> Emails, IReadOnlySet<string> Names) self)
    {
        if (citation.By is { Count: > 0 } by) return by;
        var source = citation.Source;
        if (source.Kind == DetailSource.Gmail)
        {
            var thread = (state.GmailThreads ?? []).FirstOrDefault(item => item.AccountId == account && item.Id == source.Item &&
                string.Equals(item.Address, source.Container, StringComparison.OrdinalIgnoreCase));
            if (thread is null || PeopleUnread(thread)) return [];
            var sender = thread.People!.FirstOrDefault(person => person.Sent?.Contains(source.Version) == true);
            return [sender is null ? SelfKey : Key(sender.Name, sender.Email)];
        }
        var project = state.Projects.FirstOrDefault(item => item.AccountId == account && item.ProjectId == source.Container);
        return project?.Baseline?.FileEditors?.GetValueOrDefault(source.Item) is { } editors
            ? [.. editors.Select(person => IsSelf(self, person.Name, person.Email) ? SelfKey : Key(person.Name, person.Email)).Distinct(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// Each person's contributions to a project's current details (OVERLEAF.md §12a): of the details whose people are known
    /// (Known), the ids of those that rest on the user (You) and on each collaborator (People, by position in
    /// Collaborators.People). A detail rests on everyone behind its sources, so shares do not add up to 100%.
    /// </summary>
    public static (int Known, IReadOnlyList<string> You, IReadOnlyList<IReadOnlyList<string>> People) Shares(LocalResearchState state, ResearchTheme card)
    {
        var people = card.Collaborators?.People ?? [];
        var details = people.Select(_ => new List<string>()).ToArray();
        var you = new List<string>();
        int known = 0;
        var self = Self(state, card.AccountId);
        foreach (var detail in card.Details ?? [])
        {
            var keys = detail.Citations.SelectMany(citation => CitedBy(citation, state, card.AccountId, self)).ToHashSet(StringComparer.Ordinal);
            if (keys.Count == 0) continue;
            known++;
            if (keys.Contains(SelfKey)) you.Add(detail.Id);
            for (int index = 0; index < people.Count; index++)
                if (keys.Any(key => people[index].Keys.Contains(key) || key.StartsWith("e:", StringComparison.Ordinal) && people[index].Emails.Contains(key[2..])))
                    details[index].Add(detail.Id);
        }
        return (known, you, details);
    }

    /// <summary>Who wrote an email, as a key for DetailChange.By: the user, or the sender's address (empty when unknown).</summary>
    public static string SenderKey((IReadOnlySet<string> Emails, IReadOnlySet<string> Names) self, GmailMessage message)
    {
        if (message.FromUser) return SelfKey;
        var people = EmailText.People(message.From);
        if (people.Count == 0) return "";
        var (name, email) = people[0];
        return IsSelf(self, name, email) ? SelfKey : Key(name, email);
    }

    /// <summary>
    /// The senders and recipients of a thread other than the user, from messages that are not bulk mail (unless the user wrote
    /// them), with the ids of the messages each sent.
    /// </summary>
    public static IReadOnlyList<MailPerson> PeopleOf(GmailThread thread, IReadOnlySet<string> self)
    {
        var people = new Dictionary<string, MailPerson>(StringComparer.Ordinal);
        foreach (var message in thread.Messages.Where(message => !message.Bulk || message.FromUser))
            foreach (var (header, role) in new[] { (message.From, 0), (message.To, 1), (message.Cc, 2) })
                foreach (var (name, email) in EmailText.People(header))
                {
                    if (self.Contains(email)) continue;
                    var known = people.GetValueOrDefault(email);
                    var sent = known?.Sent ?? [];
                    if (role == 0) sent = [.. sent.Append(message.Id).TakeLast(MaxSent)];
                    people[email] = new(known?.Name ?? name, email, (known?.From ?? 0) + (role == 0 ? 1 : 0), (known?.To ?? 0) + (role == 1 ? 1 : 0),
                        (known?.Cc ?? 0) + (role == 2 ? 1 : 0), known is null || message.SentAt > known.At ? message.SentAt : known.At, sent);
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

    /// <summary>Whether a person is the user: one of their addresses, or exactly their name.</summary>
    public static bool IsSelf((IReadOnlySet<string> Emails, IReadOnlySet<string> Names) self, string? name, string? email) =>
        email is not null && self.Emails.Contains(email) || name is not null && self.Names.Contains(Collaborators.Normalize(name));

    /// <summary>What each person on a card's threads wrote that Meld used: quotes cited by the card's details or by the threads' previews.</summary>
    private static Dictionary<string, List<string>> Quotes(ResearchTheme theme, IEnumerable<GmailThreadRecord> threads)
    {
        var quotes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string email, string quote)
        {
            string line = string.Join(' ', quote.Split((char[])['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0) return;
            if (line.Length > Collaborators.MaxPoint) line = line[..(Collaborators.MaxPoint - 1)].TrimEnd() + "\u2026";
            var list = quotes.TryGetValue(email, out var known) ? known : quotes[email] = [];
            if (list.Count < Collaborators.MaxPoints && !list.Contains(line)) list.Add(line);
        }
        foreach (var thread in threads)
        {
            var senders = (thread.People ?? []).SelectMany(person => (person.Sent ?? []).Select(id => (id, person.Email)))
                .GroupBy(item => item.id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Email, StringComparer.Ordinal);
            foreach (var citation in (theme.Details ?? []).SelectMany(detail => detail.Citations)
                .Where(citation => citation.Source.Kind == DetailSource.Gmail && citation.Source.Item == thread.Id))
                if (senders.TryGetValue(citation.Source.Version, out var sender)) Add(sender, citation.Quote);
            foreach (var evidence in thread.Bullets.SelectMany(bullet => bullet.Evidence).Where(evidence => evidence.Email is not null))
                Add(EmailText.Address(evidence.Email!.From), evidence.Quote);
        }
        return quotes;
    }

    public static Inputs InputsOf(LocalResearchState state, Guid account, ResearchTheme theme)
    {
        var self = Self(state, account);
        var members = theme.Members.Select(id => state.Projects.FirstOrDefault(project => project.AccountId == account && project.ProjectId == id))
            .OfType<MonitoredProject>().ToArray();
        var threads = (state.GmailThreads ?? []).Where(thread => thread.AccountId == account && thread.CardId == theme.Id &&
            thread.Status == GmailThreadRecord.Assigned).ToArray();
        var quotes = Quotes(theme, threads);
        var found = new List<Mention>();
        foreach (var project in members)
        {
            found.AddRange((project.Editors ?? []).Select(editor => new Mention(Key(editor.Name, editor.Email), editor.Name, editor.Email,
                new HashSet<string> { Collaborators.Editor }, 1, editor.At, [])));
            if (project.Owned != true && project.Owner is { } owner)
                found.Add(new(Key(owner.Name, owner.Email), owner.Name, owner.Email, new HashSet<string> { Collaborators.Owner }, 1, null, []));
        }
        foreach (var person in threads.SelectMany(thread => thread.People ?? []))
        {
            var kinds = new HashSet<string>();
            if (person.From > 0) kinds.Add(Collaborators.From);
            if (person.To > 0) kinds.Add(Collaborators.To);
            if (person.Cc > 0) kinds.Add(Collaborators.Cc);
            if (kinds.Count != 0) found.Add(new(Key(person.Name, person.Email), person.Name, person.Email, kinds, person.From + person.To + person.Cc, person.At,
                quotes.GetValueOrDefault(person.Email) ?? []));
        }
        var mentions = found.Where(mention => mention.Key.Length > 2)
            .GroupBy(mention => mention.Key, StringComparer.Ordinal)
            .Select(group => new Mention(group.Key, group.Select(mention => mention.Name).OfType<string>().MaxBy(name => name.Length), group.First().Email,
                group.SelectMany(mention => mention.Kinds).ToHashSet(), group.Sum(mention => mention.Count), group.Max(mention => mention.At),
                [.. group.SelectMany(mention => mention.Points).Distinct(StringComparer.Ordinal).Take(Collaborators.MaxPoints)]))
            .Where(mention => !IsSelf(self, mention.Name, mention.Email))
            .OrderByDescending(mention => mention.Kinds.Contains(Collaborators.Editor) || mention.Kinds.Contains(Collaborators.Owner))
            .ThenByDescending(mention => mention.Points.Count != 0).ThenByDescending(mention => mention.Kinds.Contains(Collaborators.From))
            .ThenByDescending(mention => mention.Count).ThenByDescending(mention => mention.At).ThenBy(mention => mention.Key, StringComparer.Ordinal)
            .Take(Collaborators.MaxMentions).ToArray();
        var anchor = ThemePlan.AnchorOf(state, theme);
        string authors = string.Join('\n', members.OrderByDescending(member => member == anchor).Select(member => member.Paper?.Authors)
            .OfType<string>().Distinct(StringComparer.Ordinal));
        if (authors.Length > Collaborators.MaxAuthors) authors = authors[..Collaborators.MaxAuthors].TrimEnd();
        var own = self.Emails.Order(StringComparer.Ordinal).Concat(self.Names.Order(StringComparer.Ordinal)).Take(Collaborators.MaxSelf).ToArray();
        // The way collaborators are made is part of the authors' fingerprint, so a new way makes them again with Foundry.
        string authorsFor = ResearchEvidence.Hash($"v{Version}|{authors}");
        string print = ResearchEvidence.Hash($"{authorsFor}|{string.Join(",", own)}|" + string.Join("|", mentions.Select(mention =>
            $"{mention.Key}:{mention.Name}:{string.Join(",", mention.Kinds.Order(StringComparer.Ordinal))}:{mention.Count}:{mention.PointsFor}")));
        return new(mentions, authors.Length == 0 ? null : authors, own, print, authorsFor);
    }

    /// <summary>
    /// A project of the profile whose people changed since its collaborators were made, once what it is made from was read:
    /// its Overleaf projects' cycles finished, its email threads folded and their people read. Editors are not waited for (a
    /// project whose history cannot be read would never get collaborators); when they arrive, known people merge without a call.
    /// </summary>
    public static ResearchTheme? Due(LocalResearchState state, Guid account) =>
        ThemePlan.Active(state, account).FirstOrDefault(theme => Ready(state, account, theme) && theme.Collaborators?.For != InputsOf(state, account, theme).Print);

    private static bool Ready(LocalResearchState state, Guid account, ResearchTheme theme) =>
        theme.Members.All(member => state.Projects.FirstOrDefault(project => project.AccountId == account && project.ProjectId == member) is not { } project ||
            ThemePlan.Settled(project) && project.PaperFor == (project.Baseline is { } baseline ? Papers.ReadFor(baseline.Revision) : project.PaperFor)) &&
        !ThemePlan.FoldPending(state, theme) &&
        !(state.GmailThreads ?? []).Any(thread => thread.AccountId == account && thread.CardId == theme.Id && thread.Status == GmailThreadRecord.Assigned &&
            PeopleUnread(thread) && (state.GmailAccounts ?? []).Any(mail => mail.AccountId == account && !mail.Removed && mail.Status == "connected" &&
                string.Equals(mail.Address, thread.Address, StringComparison.OrdinalIgnoreCase)));

    /// <summary>A thread whose people were never read, or read before the messages each person sent were kept.</summary>
    public static bool PeopleUnread(GmailThreadRecord thread) => thread.People is null || thread.People.Any(person => person.From > 0 && person.Sent is null);

    /// <summary>The request for one Foundry call: the people (ids m1, m2, ...), the author blocks and the user's own names and addresses.</summary>
    public static CollaboratorsRequest Request(Inputs inputs, string project) =>
        new(project.Length > Collaborators.MaxProject ? project[..Collaborators.MaxProject].TrimEnd() : project, inputs.Self, inputs.Authors,
            [.. inputs.Mentions.Select((mention, index) => new PersonMention($"m{index + 1}", mention.Name, mention.Email,
                [.. mention.Kinds.Order(StringComparer.Ordinal)], Math.Max(1, mention.Count), mention.Points.Count == 0 ? null : mention.Points))]);

    private static string Minor(Mention mention) => $"{mention.Key}|{mention.PointsFor}";

    /// <summary>
    /// The collaborators when nothing new needs Foundry: each person keeps the people merged into them, and a new person joins
    /// one with the same address or exactly the same name. Someone new who cannot be a collaborator on their own (only a
    /// recipient, or nothing they wrote was used) stays aside without a call. Null when someone new could be a collaborator,
    /// someone set aside wrote something new that was used, or the authors changed.
    /// </summary>
    public static CollaboratorSet? Incremental(CollaboratorSet? previous, Inputs inputs)
    {
        if (previous is null || previous.AuthorsFor != inputs.AuthorsFor) return null;
        var dropped = previous.Dropped.ToHashSet(StringComparer.Ordinal);
        var minorBefore = (previous.Minor ?? []).Select(item => item.Split('|')).GroupBy(item => item[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ElementAtOrDefault(1) ?? "", StringComparer.Ordinal);
        var minor = new List<string>();
        var people = previous.People.Select(person => (Person: person, Mentions: new List<Mention>())).ToList();
        foreach (var mention in inputs.Mentions)
        {
            if (dropped.Contains(mention.Key)) continue;
            int index = people.FindIndex(item => item.Person.Keys.Contains(mention.Key));
            if (index < 0 && minorBefore.TryGetValue(mention.Key, out var points))
            {
                if (points != mention.PointsFor && mention.Matters) return null;
                minor.Add(Minor(mention));
                continue;
            }
            if (index < 0 && mention.Email is { } email) index = people.FindIndex(item => item.Person.Emails.Contains(email));
            if (index < 0 && mention.Name is { } name)
                index = people.FindIndex(item => item.Person.Name is { } known && Collaborators.Normalize(known) == Collaborators.Normalize(name));
            if (index < 0 && !mention.Matters) { minor.Add(Minor(mention)); continue; }
            if (index < 0) return null;
            people[index].Mentions.Add(mention);
        }
        var result = people.Select(item => item.Mentions.Count == 0
                ? item.Person.Kinds.Contains(Collaborators.Author) ? item.Person with { Emails = [], Kinds = [Collaborators.Author], Count = 0, At = null, Keys = [] } : null
                : Person(item.Person.Name, item.Mentions, item.Person.Kinds.Contains(Collaborators.Author)))
            .OfType<Collaborator>().ToArray();
        return new(inputs.Print, inputs.AuthorsFor, Order(result), previous.Dropped, minor.Count == 0 ? null : minor);
    }

    /// <summary>
    /// The collaborators from Foundry's (cleaned) grouping of the request's people: a group is a collaborator when it is an
    /// author, edited the project, or wrote email the model judged substantive; the others are set aside.
    /// </summary>
    public static CollaboratorSet Build(Inputs inputs, IReadOnlyList<CollaboratorGroup> groups, IReadOnlyList<string> dropped)
    {
        var byId = inputs.Mentions.Select((mention, index) => (Id: $"m{index + 1}", Mention: mention)).ToDictionary(item => item.Id, item => item.Mention, StringComparer.Ordinal);
        var people = new List<Collaborator>();
        var minor = new List<string>();
        foreach (var group in groups)
        {
            var mentions = group.Mentions.Select(id => byId[id]).ToArray();
            bool kept = group.Author || mentions.Any(mention => mention.Kinds.Contains(Collaborators.Editor)) ||
                group.Involved && mentions.Any(mention => mention.Kinds.Contains(Collaborators.From) && mention.Points.Count != 0);
            if (kept) people.Add(Person(group.Name, mentions, group.Author));
            else minor.AddRange(mentions.Select(Minor));
        }
        return new(inputs.Print, inputs.AuthorsFor, Order(people), [.. dropped.Select(id => byId[id].Key)], minor.Count == 0 ? null : minor);
    }

    /// <summary>
    /// Without Foundry: people with the same address or exactly the same name are one person, and a person is a collaborator
    /// when they edited the project or wrote something Meld used.
    /// </summary>
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
        var kept = groups.Where(group => group.Any(mention => mention.Matters)).ToArray();
        var minor = groups.Except(kept).SelectMany(group => group).Select(Minor).ToArray();
        return new(inputs.Print, inputs.AuthorsFor, Order([.. kept.Select(group => Person(null, group, false))]), [], minor.Length == 0 ? null : minor);
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
