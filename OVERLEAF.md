# Overleaf ingest: documents, trees and details

This is the design for how Meld Research reads Overleaf projects into a research
project's profile in About me. It replaces the earlier file-by-file summary
pipeline and is implemented: the include graph and parts in
[LatexTree](libraries/Research/LatexTree.cs), the contracts and limits in
[DetailContracts](libraries/Research/DetailContracts.cs), planning and detail edits
in [DetailPlan](apps/LocalBackend/Services/Research/DetailPlan.cs), the cycle in
[ResearchCoordinator](apps/LocalBackend/Services/Research/ResearchCoordinator.cs) and
the model steps in [FoundryService](apps/RemoteBackend/Services/FoundryService.cs)
(`/api/research/detail-step`, `/detail-summary`, `/detail-merge`). Email (Gmail)
follows the same profile schema and is covered briefly in
[Email sources](#12-email-sources).

## 1. The profile schema

Every research project (a card in About me) has three parts:

| Part | What it is | When it changes |
| --- | --- | --- |
| **a) Summary** | Two sentences, about as long as a Project Meld summary. | Rewritten from b) at the **end of an ingest cycle**, and only if b) changed in that cycle. |
| **b) Details** | The current state of the project: at most **30 bullets**, each **one sentence**, each with its **sources**. | Edited by every update step (Overleaf or email). |
| **c) Changelog** | A chronological list of sources (newest first, infinitely scrollable), each with what it did to b). | One entry per update (a root tree for Overleaf, a thread's new messages for email). |

A **source** is a concrete piece of evidence at a fixed version:

- an Overleaf file at a content hash: `overleaf:<project>/<path>@<sha256>`;
- a Gmail thread up to a message: `gmail:<address>/<thread>@<lastMessageId>`.

Each bullet lists one or more sources. A bullet citation also keeps the cited
line range, so the server can copy the exact quote (as today). A bullet left
without sources is deleted.

The card title is the main paper's `\title` when there is one (see
[Papers](libraries/Research/PaperContracts.cs)), else the working title the
details imply.

## 2. Terms

- **Document**: a text file of an Overleaf project that Meld reads: `.tex`,
  `.md` or `.txt` (the analyzed extensions in
  [ResearchEvidence](libraries/Research/ResearchEvidence.cs)), at most 128 KB.
  `.bib`, `.bst`, `.cls`, `.sty`, images and other files are never documents.
- **Include edge**: document A includes document B (see §3).
- **Tree** of a document: the document and everything it includes, transitively.
- **Root**: a document that no other document includes (no parent).
- **Tree date**: the latest edit time of any document in a root's tree.
- **Window**: the last **six months** (183 days) before the ingest. This is the
  same single setting that decides which Overleaf projects need an active
  include/exclude choice (`ResearchCoordinator.RecentDays`, reported to the app as
  `recentDays`); there is no second window.
- **Cycle**: one ingest of one Overleaf project (initial or later).
- **Folded version**: for each document, the hash whose text the card's details
  already reflect.

## 3. Step i — the include graph

For every document, parse its include commands (comments removed first: text
after an unescaped `%`, and `\iffalse ... \fi` blocks):

| Command | Target |
| --- | --- |
| `\input{x}`, `\include{x}` | `x`, then `x.tex` |
| `\subfile{x}` | `x`, then `x.tex`, relative to the including file's folder |
| `\import{dir/}{x}`, `\subimport{dir/}{x}` | `dir/x`, then `dir/x.tex` (`\subimport` relative to the including file) |

Path resolution, in order: relative to the including file's folder, then
relative to the project root (LaTeX resolves `\input` from the compiling
document's folder, so both are tried). `./` prefixes are stripped. A target that
does not resolve to a document is ignored (it may be a figure or a deleted file).

Each resolved target becomes an edge **parent → child**. Every document therefore
has a list of children (in the order they appear in the text) and a list of
parents.

- **Cycles** (A includes B includes A) are broken with a visited set; an edge
  that would close a cycle is dropped.
- **Shared children**: a document included by several parents belongs to the
  tree of each.
- `\includeonly` is ignored: an excluded chapter is still part of the paper.

## 4. Step ii — roots

A **root** is a document with no parents. Typically:

- the paper's main file (`main.tex`), with its sections as children;
- memos and notes (their own `\documentclass`, or a standalone `notes.md`);
- slide decks (beamer documents) — treated like any other root;
- orphan fragments: a section file no document includes any more (an old draft
  or a scrap). An orphan without `\documentclass` counts as a root only if it has
  at least **1,000 characters**, so tiny scraps never cost a call; the window in
  §6 keeps old ones out.

## 5. Step iii — tree dates

The **tree date** of a root is the latest edit time of any document in its tree,
so editing a section (`sections/model.tex`) makes the paper's root recent even
if `main.tex` itself is untouched.

Edit times come from Overleaf's project history (the per-file times the browser
connector already reads, `ProjectManifest.EditTimes`). A file without a history
time (older than the available history, or no history at all) uses the
project's last-update time from Overleaf's project list.

## 6. Step iv — order and window

1. Keep the roots whose tree date is within the **window** (six months).
2. Order them by tree date, **oldest first**, so the most recent writing (usually
   the paper) is folded last and wins where documents disagree.
3. If no root is within the window (an included project that has been quiet for
   months), take the single newest root, so every included project still gets
   details.

The window is the same setting as the Projects tab's: projects updated within
it need an active include/exclude choice, and only roots within it are read.

## 7. Step v — feeding a tree into details

For each selected root, in the order of §6:

1. **Reading order.** Walk the tree depth-first in include order: the root, then
   each child where it is included, recursively. This gives the documents in
   the order a reader meets them.
2. **What is sent.**
   - On the **initial ingest**, every document of the tree is sent.
   - On **later ingests**, only documents whose hash differs from their folded
     version (changed or new) are sent.
   - A document already folded at the same hash, for example a section shared
     with a tree folded earlier in this cycle, is **not** sent again.
   - Every step also gets the tree **outline**: all paths in reading order,
     each marked *sent*, *already read* or *unchanged*, so the model knows the
     structure.
3. **Parts.** The sent documents are packed in reading order into parts of at
   most **60,000 characters** each (a constant, below the Foundry and Cloudflare
   request limits). A single document larger than that is split at
   `\section`/`\chapter` boundaries, and otherwise at paragraph boundaries. Each
   part is **one Foundry step**.
4. **A step's input:**
   - the card's current details (bullet ids, text and sources, each source
     with its date) and today's date, so the model can tell how old a plan or
     status is;
   - the project and root context (root path, paper title and abstract if any,
     the outline);
   - the part's documents with numbered lines.
5. **A step's output: edit operations on the details.**
   - `add` — a new bullet: text plus citations (`path`, `startLine`, `endLine`).
   - `revise` — an existing bullet (by id): new text, citations to add, sources
     to remove.
   - `remove` — an existing bullet (by id) that the new text contradicts or
     makes obsolete.

   The prompt asks the model to keep the details current: a plan, next step or
   "in progress" status that the new evidence shows was done, changed, dropped
   or overtaken is revised or removed rather than left next to the newer fact,
   and when the list is full the least important or most dated detail makes
   room. Edits are applied removals first, then revisions, then additions, so a
   full list can always take a new detail whatever order the model listed them
   in; an addition that still does not fit is not made (and not logged).
6. **Validation (server, then again locally):**
   - At most 30 bullets after the step, each one sentence of at most 300
     characters.
   - Every citation names a document sent in this step and a valid line range.
     The server copies the cited lines verbatim (as for Updates today).
   - Operations on ids that are not current bullets, and citations that fail
     these checks, are dropped; an `add` left without citations is dropped too.
   - A step that fails (the server, Foundry or a malformed answer) commits
     nothing: the project backs off (1 minute, doubling to a day), shows the
     error and retries the same part; **Check now** retries at once. Failed
     processing never advances the folded versions.
7. **Commit.** Each step commits atomically: the new details, and the folded
   version of every document in the part. A restart therefore continues with
   the next part, and an unchanged project makes **zero** model calls.

## 8. Step vi — continue, then summarize

Steps for the next root follow, until every selected root is folded. Only then,
once per card and only if its details changed in this cycle, one Foundry call
rewrites **a)** (two sentences) from the details, with the paper's title and
abstract as context.

Each root folded in the cycle adds one **changelog** entry:

- **date:** the tree date;
- **root:** the root's path, for example *Paper: main.tex*;
- **documents:** those read (new, changed), and those dropped (§9);
- **effect:** bullets added, revised and removed (with the bullet text before
  and after).

The initial ingest is therefore just the first sequence of such updates, dated
by the documents' own edit times.

The **Updates feed** shows the changelog entries that changed details, newest
first.

## 8a. Periodic review

Details only change when evidence arrives, so a plan stated in an old email
("Lukas will draft the preregistration") could stay after the work moved on
without anyone writing it down. A **review** guards against that:

- **When:** a card of the profile (not skipped) with at least 3 details, whose
  details changed since its last review, once its projects are settled (as for
  the summary), and at most once every **30 days**
  (`ResearchCoordinator.ReviewInterval`). It runs before the summary, so a) is
  written once from the reviewed details. An initial ingest is reviewed right
  away.
- **Input:** only the details, each source's date, today's date and the paper's
  title and abstract (`/api/research/detail-review`); no source text.
- **Output:** the details to keep, each naming the details it came from (it keeps
  all their sources). The model merges overlapping details, rewords a status
  that newer details moved on, and retires plans, next steps and "in progress"
  statuses that newer details overtook or that are many months older than the
  project's newest sources with nothing newer supporting them. It never retires
  the question, approach, model, data or results for their age and adds no new
  facts. At least half of the details must survive; otherwise the review is
  rejected.
- **Commit:** only if the details did not change meanwhile; one changelog entry
  of kind *review*. Reviews are housekeeping, so they are in the changelog but
  not in the Updates feed. A review the server rejects is recorded as done for
  those details (it never blocks the summary); a connection problem is retried.

## 9. Later ingests

Later ingests run the same algorithm (§3–§8), with two differences:

- **Which roots.** A root is processed when its tree changed since the last
  ingest: a document's hash differs from its folded version, a document was
  added, or a document left the tree. Hashes rather than times decide this
  (Overleaf history times can lag), and times only order the roots.
- **What is sent.** Only the changed and new documents of that tree (see §7.2).

**Removed documents** (deleted from the project, or no longer included by any
selected tree) lose their sources: every citation of them is removed, and a
bullet left without sources is deleted. No model call is needed; the changelog
entry records it.

A **renamed** document is a removal plus an addition.

## 10. When the user removes a source

The user can click a bullet, see its sources and remove one.

- The citation is removed from the bullet; a bullet without sources is deleted.
- An Overleaf **file** source goes to **Housekeeping** (like an email thread):
  *"sections/model.tex of overleaf_pcm"*. There the user can:
  - **add it to another project** — its current version is folded into that
    card once, as an update;
  - **skip it** — it is left out.

  Until then this card ignores that document: it is not sent again, even when
  it changes.
- Afterwards the summary a) is rewritten from the new details (one call).

## 11. Projects, cards and moves

Details belong to a **card**, the research project in About me. A card can hold
several Overleaf projects (a paper project and a slides project) and email
threads; their sources all edit the same 30 bullets.

- **A newly included project** is ingested into a card of its own. After its
  first cycle, strict matching (same paper or same core question) may combine
  it with an existing card. Combining two cards is one Foundry call that merges
  both detail lists into at most 30 bullets, keeping only the sources of the
  bullets it merges. Every merged bullet must cite sources of the bullets it
  came from.
- **Moving a project** to another card removes its sources from the old card's
  details (bullets without sources are deleted, and the summary is rewritten).
  The project's trees are then folded into the target card as a fresh cycle,
  where every document counts as new.
- **Skipped** cards keep their details but pause, as today.

## 12. Email sources

Email follows the same schema. A thread assigned to a card is a source,
`gmail:<address>/<thread>@<lastMessageId>`. Its new messages are one update step
on the card's details, using the same operations as §7.5, and one changelog
entry. Unassigned threads stay in Housekeeping with their short preview, grouped
loosely with related waiting threads, until the user decides on the thread or its
whole group (README, Gmail → Housekeeping). Email streams (the `gmail-…` card members) are no longer
needed: threads attach to cards directly.

## 12a. Collaborators

Each research project also lists its **collaborators**: one entry per person,
with a name and the email addresses they use (either may be missing), and how
they appear on the project ([CollaboratorPlan](apps/LocalBackend/Services/Research/CollaboratorPlan.cs),
[CollaboratorContracts](libraries/Research/CollaboratorContracts.cs)). About me
shows them in the card's **Collaborators** tab.

1. **People, read without a model.** From each Overleaf project of the card:
   who edited it (name, email and latest edit, from the history the connector
   already reads, connector 0.6.11) and who owns it (from the project list); from
   its main paper: the `\author` blocks as plain text with any email addresses
   in them or in `\email`/`\thanks` (`PaperInfo.Authors`, `Papers.Version` 4);
   from every email thread on the card: the From, To and Cc names and addresses
   of messages that are not bulk mail (`GmailThreadRecord.People`). Projects and
   threads read before this keep a one-time read of their editors or headers. A
   history without editors (an older connector, a failed read) does not count as
   "nobody": the next check asks again (`MonitoredProject.EditorsFor`).
2. **The user is left out**: their Meld and Gmail addresses, their Meld name, and
   the owner of the Overleaf projects they own.
2a. **Only real collaborators.** A person counts when they are an **author** in
   the author blocks, **edited** one of the card's Overleaf projects, or their
   **email shows substantive work** on the research. For the last, each person on
   email is sent with up to three short quotes of what they wrote that Meld used
   (cited by the card's details or a thread's preview points, matched through the
   ids of the messages each person sent, `MailPerson.Sent`); the model sets
   `involved` only for work on the research itself, not scheduling, forwarding,
   acknowledging or receiving slides. Recipients only (To/Cc), owners who never
   edited and people without used quotes are set aside (`CollaboratorSet.Minor`)
   but still help merging; a newcomer who cannot count on their own is set aside
   without a call, and someone set aside is judged again only when what they wrote
   that was used changes.
3. **Merging.** People with the same address are one mention. When the mentions
   or author blocks change (`CollaboratorSet.For`), new mentions that match a
   known collaborator by address or exactly by name join them without a model.
   Anything else makes **one** Foundry call for the card
   (`/api/research/collaborators`): the mentions (ids, names, addresses, how
   and how often they appear), the author blocks and the user's own names and
   addresses. The model returns groups of mention ids, a name per group exactly
   as written in a mention or the author blocks (or none), an author flag, and
   mentions to drop (the user, lists, role addresses). Locally, every mention is
   used at most once, a name that is not written anywhere falls back to a
   mention's name, mentions the model left out stay as their own people, and
   addresses come only from the mentions. A rejected answer falls back to
   merging by address and exact name, so it is not asked again until the people
   change.
4. **Cost.** Unchanged people make zero calls, including after a restart; more
   mail from known people only updates counts. At most 150 mentions per call.
   `CollaboratorPlan.Version` 2 (substantive involvement) makes every card's
   collaborators once more.
4a. **Who each change rests on.** Every change in the changelog records who it
   rests on (`DetailChange.By`, person keys or `self`), with no model call: for
   email, the senders of the messages its quotes cite; for Overleaf, who edited
   the cited files since the version Meld read before (the connector reports when
   each editor last edited each file, `ProjectManifest.FileEditors`; else the
   file's latest editor). Removals rest on everyone behind the step. The changelog
   shows them as "You", collaborators' names, and "Others" for anyone who is no
   collaborator; email changes recorded before this are traced through the
   messages their details cite now.
4b. **Credit is fixed when a detail is written** (`DetailCitation.By`). A new
   Overleaf quote is credited to who wrote its lines in the version read: the
   connector (0.6.14) reads the file's history diff once and passes on, per line,
   a short hash and the characters each person wrote (`FileAuthorship`); people
   with at least 25% of a quote's characters are credited (at least the largest),
   and text without an author (an upload, a copy, history Overleaf no longer
   keeps) counts for who added the file, else the project's owner. A revision that
   keeps the wording only re-quotes the source after someone rewrote it, so its new
   quotes keep the earlier credit: rewording earns none. An email quote is credited
   to the message's sender. When the lines cannot be found or the history is too
   long to rebuild, the quote falls back to who edited its file since the last
   read. Existing details are re-credited by their quoted lines once
   (`ResearchTheme.CreditedFor`). Building details does not change.
4c. **Shares of the current details.** Each citation records who its source
   rests on when it is cited (`DetailCitation.By`); older citations are traced
   through the cited message's sender, or everyone who edited the cited file
   (`Baseline.FileEditors`, read once more with `EditorsVersion` 3). That read
   runs on its own (history only, at most every ten minutes per project until
   it succeeds), not only at a check of Overleaf, because with sync another
   computer may do the checks; a synced copy without per-file editors keeps the
   local ones for the same version. A detail rests on everyone behind its
   citations, and the Details tab shows them under each detail. The Collaborators tab shows, for you
   and each collaborator, the share of the card's **current** details (the
   snapshot kept now, not the changelog's history) that rest on them, over the
   details whose people are known; shares do not add up to 100%. The tab is a list
   (you first, then by share); a click on a share lists the details that rest on
   that person, with their sources.
5. Collaborators sync with the card; skipped cards are not processed.

## 13. Fresh start

The new format starts from scratch, once (`ResearchCoordinator.ResetEpoch` 2).
Epoch 3 repeats it once more, after the first build of this format ran briefly
without dated citations and reviews.

**Kept:**

- the Meld sign-in on this computer;
- the encrypted-sync key and passphrase (the key file in the cloud is kept);
- connected Gmail accounts;
- include/exclude choices for Overleaf projects;
- what was skipped as not the user's research: Overleaf project ids and Gmail
  thread ids, which are skipped again after the rebuild;
- the OpenAlex match and publications.

**Erased:**

- projects' summaries, processing state, cards, details, changelog and Updates;
- recorded threads and stored texts;
- every synced research copy in the cloud (project and profile bundles).

The new bundles carry a new format version in their blob names (format 3:
`profile3.bin`, `p3/...`; [SyncCrypto](libraries/Research/SyncCrypto.cs)).
Bundles of older formats (`profile.bin`, `p/...`, `profile2.bin`, `p2/...`)
are ignored and deleted by the first upload in the current format, so a
computer still on an older version cannot bring them back. Meld's server must
accept the current names before clients use them, so it is deployed first. The rebuild then runs the initial ingest (§3–§8) for every included
project, and the email import for every connected account.

## 14. Cost and limits

- Calls per project on the initial ingest: the sum over selected roots of
  ⌈characters sent ÷ 60,000⌉, plus one summary call per card. Shared and
  already-read documents are not counted twice.
- Later ingests: one call per part of changed documents, plus at most one
  summary call per changed card. An unchanged project makes zero calls.
- Reviews: at most one call per changed card every 30 days (§8a), plus one
  right after a card's initial ingest.
- Collaborators: one call per card when someone new appears who is not known by
  address or exact name (§12a); none otherwise.
- Documents over 128 KB are not read (unchanged from today). Every model request
  stays under the 100-second Cloudflare limit.

## 15. Edge cases

| Case | Handling |
| --- | --- |
| No Overleaf history | Every document uses the project's last-update time; all roots share one date and are folded in path order. |
| No root within the window | The newest root is folded (§6.3). |
| Only orphan fragments recent | They are roots and are folded; the old paper is not, unless it is the newest root. |
| A section included by two roots | Read once, with the first tree that reaches it; later trees list it as *already read*. |
| Include cycle | The closing edge is dropped (§3). |
| A document over 60,000 characters | Split at sections, then at paragraphs (§7.3). |
| A document over 128 KB | Not read; listed in the outline as *too large*. |
| Root deleted | Its documents leave the tree; their sources are removed (§9). |
| Model failure | Nothing is committed; the part is retried with backoff and the error is shown (§7.6). |

## 16. Decisions

1. One window for include/exclude choices and for ingest: six months (183 days).
2. Orphan fragments without `\documentclass` need at least 1,000 characters to
   count as roots.
3. Slide decks are roots like any other document.
4. Removing a source removes it from the whole card (every bullet that cites it)
   and sends it to Housekeeping. **Add to project** folds that file's current
   version into the chosen card once; the file stays out of its own project's
   card. **Skip** leaves it out.
5. A failed step is retried rather than skipped, so a model or server failure
   never loses a document's content (§7.6).
6. The feed's Updates are the changelog entries that changed details, including
   the user's own removals; the changelog is ordered by the sources' dates.
7. The changelog pages by entry (`after` = the last entry shown), so entries that
   share a date are never skipped between pages.
8. Details are capped at 30. Steps see each source's date, edits apply removals
   first, and a monthly review retires overtaken plans (§7.5, §8a), so details
   do not go stale.
