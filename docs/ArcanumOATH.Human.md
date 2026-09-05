# OATH: A Human Guide to Arcanum's Memory Architecture

> **The short version:** OATH is Arcanum's promise that a memory cannot gain power merely because it was copied, summarized, retrieved often, or stated confidently.
>
> OATH stands for **Origin-Bound, Authority-Conserving Transactional History**. Its central rule is: **Memory cannot outrank its origin.**
>
> This is the approachable guide. [`Arcanum.OATH.md`](Arcanum.OATH.md) provides the complete technical explanation and source map. The broader shipped design remains authoritative in [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md).
>
> **Branch status.** None. This guide was written on `long-term-memory`, which has since been merged into `main` and deleted; everything it describes is on `main`. Section 11 is still the boundary between what ships and what does not.

---

## 1. OATH in one minute

An AI assistant can remember a great deal without knowing what any memory is allowed to mean.

Consider these three sentences:

- An operator says, "Production billing uses PostgreSQL."
- An agent infers, "Production billing probably uses PostgreSQL."
- A search index returns text containing, "Production billing uses PostgreSQL."

The words may be identical, but their authority is not.

OATH preserves that difference. Every durable memory travels with its papers: where it came from, who authorized it, where it may apply, which version is current, what sensitive information it contains, and what later artifacts were derived from it.

A memory may become shorter, easier to find, or more useful. It may not quietly become more powerful.

OATH is not one database or one feature. It is the shared set of rules that governs Arcanum's memory systems. The **Covenant** is the subsystem that supplies the governed claim and authority foundation. The **Grimoire** stores encrypted records. The **Lexicon**, **Saga**, **Tapestry**, **Weave**, Session history, and future **Long Rest** each serve different memory roles. OATH controls what happens when information crosses between them.

## 2. Why AI memory needs rules

Ordinary memory systems tend to collapse several different questions into one:

> "The search found this. Should I put it in the prompt?"

That is too simple for a durable agent. A useful memory system must ask at least:

1. Where did this information come from?
2. Who, if anyone, approved it?
3. Which Campaign is it allowed to influence?
4. Is it still current, or has it been corrected or retired?
5. Is it sensitive?
6. Can it fit in this particular model request?
7. Has it already been disclosed outside Arcanum?
8. What should happen to its summaries, indexes, and files if it is erased?

A vector match answers none of those questions. It only says that two pieces of text are similar.

Without stronger rules, familiar failures appear:

- A model guess is repeated until it looks like an established fact.
- A Campaign-specific instruction leaks into an unrelated Campaign.
- A summary loses the sensitivity or limitations of its sources.
- A retry observes new memory halfway through one logical turn.
- A search index becomes an accidental source of truth.
- A reset deletes a canonical row but leaves a derived file or embedding behind.
- An external disclosure is forgotten even though it cannot be recalled.

OATH treats these as architecture problems, not prompt-writing problems.

A **Campaign** is Arcanum's persistent authority scope for one body of work. It may reference a verified Workspace root, but a Campaign and a Workspace are not the same thing.

## 3. Follow one memory through OATH

Suppose an agent reads a versioned design note attached to a Session inside the **Northstar Campaign** and infers:

> Billing data is stored in PostgreSQL.

This example explains the target OATH integration. Most of the machinery below is built and tested, but none of it is reachable from a live turn yet, and the feature is off by default. Section 11 explains that boundary.

### 3.1 It begins as a source-bound proposal

The agent did not receive permission to declare a universal fact. It may create a **Proposed** Campaign memory, but that proposal remains bound to:

- the Northstar Campaign;
- the source attachment and its exact version or materialized range;
- the turn that produced the proposal;
- the agent-proposed authority lane;
- the content's sensitivity;
- an immutable revision and content identity.

The text itself cannot ask for Global scope, Confirmed status, tool access, or a weaker sensitivity label. Those properties come from platform policy and authenticated authority, not from model output.

### 3.2 It publishes only with the answer that produced it

During the model and tool loop, the proposal remains in temporary turn-local storage. It is not yet canonical memory.

If the assistant turn succeeds, Arcanum publishes the final assistant entry and its staged memory mutation together in one local transaction. If the turn fails, is cancelled, loses a race, or its branch is abandoned, neither side is published.

This prevents a failed answer from leaving behind a successful-looking memory.

### 3.3 Seeing it again does not increase its authority

On a later Northstar turn, Covenant can load the current scoped proposal directly. Its inspection index may also find it for an operator. Elsewhere in OATH, embeddings, the Lexicon, the Saga, and other discovery systems return candidates from their own stores.

Being loaded or discovered does not increase authority. OATH still checks:

- Does the current Session belong to Northstar?
- Is the source lineage intact?
- Is the proposal active rather than retired?
- Does its sensitivity permit this use?
- Does current policy allow Proposed memory on this execution surface?
- Is there space in the selected provider's concrete context window?

Discovery answers "What might be relevant?" OATH answers "What is this candidate allowed to do?"

### 3.4 It reaches the model as fenced data

If the proposal is eligible and fits, it enters a clearly marked **DATA** section. It does not enter the operator-authorized **CONTEXT** section and cannot grant tool permission.

If context becomes tight, Proposed material is the first OATH tier removed. Arcanum does not keep an arbitrary middle selection. It admits a deterministic prefix so the same inputs produce the same decision.

### 3.5 An operator action creates a separate Confirmed claim

Later, an authenticated operator may verify the architecture and independently set a matching Confirmed claim.

OATH does not rewrite the old proposal to pretend it was always authoritative. The operator action creates a new immutable Confirmed version and receipt. The earlier proposal and its lineage remain part of history. A streamlined review-and-confirm curation workflow is planned later, but it must preserve this same separation.

Even then, **Confirmed does not mean objectively true**. It means operator-authorized for the stated scope and revision.

### 3.6 Summaries inherit the source's constraints

Suppose the confirmed fact contributes to a Session summary or, in the future, a Campaign rollup. The shorter text does not become source-free. The derivative keeps lineage to every contributor and conservatively inherits their sensitivity. Tapestry is not a destination for protected Covenant-derived content.

If several sources contribute, the result cannot be more authoritative than its least-authorized source, cannot apply outside their permitted scope, and cannot use a sensitivity lower than the most sensitive contributor.

### 3.7 Correction, retirement, and erasure remain distinct

If Northstar later moves billing to another database, correction creates another immutable version. Retirement adds a tombstone so the old claim stops participating in current turns without erasing the fact that it once existed.

Erasure is a separate operation. It follows the owned dependency chain through labels, summaries, indexes, embeddings, and managed files. It can remove Arcanum's local copies when identity checks succeed. It cannot recall bytes already sent to an external provider or recipient.

```mermaid
flowchart LR
    Source["Campaign source"] --> Proposal["Proposed claim"]
    Proposal --> Publish["Atomic answer and memory publication"]
    Publish --> Discovery["Search and discovery candidates"]
    Discovery --> Policy["Scope, lineage, lifecycle, and sensitivity checks"]
    Policy --> Admission["Provider-specific admission"]
    Admission --> Call["Frozen model call"]
    Call --> History["Receipts and immutable history"]
    History --> Discovery
    Operator["Authenticated operator"] --> Confirmed["Separate Confirmed version"]
    Confirmed --> Discovery
```

## 4. The four promises in OATH

### 4.1 Origin-Bound: every memory travels with its papers

Origin is more than a source URL. Depending on the memory, it can include a Session, Campaign, attachment version, byte range, producing turn, model attempt, source revision, content digest, or transformation receipt.

OATH keeps this evidence immutable. Deleting a source may make it unavailable, but it does not rewrite surviving history so that a derivative appears to have no source.

If origin evidence is missing or malformed, the safe result is refusal, quarantine, repair, or erasure. Arcanum does not guess.

### 4.2 Authority-Conserving: processing cannot promote information

Summarization, extraction, translation, ranking, repetition, and model confidence do not create authority.

An ordinary transformation may narrow what a memory can do. It may not:

- turn Proposed data into Confirmed context;
- broaden one Campaign's memory into Global memory;
- remove inconvenient lineage;
- lower sensitivity;
- turn data into instructions;
- grant access to a tool;
- make a retired revision active again.

An authority increase is possible only through a new authenticated action, such as an operator confirmation. That action creates a new durable claim and receipt. It is not a hidden side effect of processing the old claim.

### 4.3 Transactional: related local results appear together

Within the Grimoire, OATH uses local transactions, immutable versions, guarded current pointers, and idempotency receipts. The key rule for an agent turn is simple:

> The assistant result and its staged memory changes publish together, or neither publishes.

This does not make the internet transactional. A provider call, tool process, filesystem rename, or message cannot be rolled back by SQLite.

For those effects, OATH records authorization before disclosure, freezes exact effect identities, uses idempotent operations where possible, and writes recovery journals around irreversible boundaries. Transactional therefore means atomic local publication plus evidence-backed recovery, not a magical distributed transaction.

### 4.4 History: correction adds a revision

OATH treats current state as a view over history, not as one mutable truth slot.

Claims have immutable versions. Retirements are tombstones. Dataset generations make a reset or restore a hard boundary. Receipts distinguish a replay from a different request. Derived artifacts bind the source revisions from which they were produced.

This avoids a dangerous pattern in which a record changes while retaining the same identity, making old work appear current. It also lets an operator see how a memory changed and why.

OATH is ready to grow into full bitemporal memory, where transaction time and real-world valid time are separate. That full valid-time model is roadmap work, not a claim about today's implementation.

## 5. Confirmed and Proposed are deliberately different

The Covenant has two independent authority lanes.

| Question | Confirmed | Proposed |
|---|---|---|
| How is the lane populated? | An authenticated operator action creates a claim | An eligible agent turn stages a suggestion without granting authority |
| What does it mean? | Operator-authorized context for its exact scope | Untrusted agent-produced data for review or bounded use |
| Where may it live? | Global or one Campaign | One Campaign only |
| How is it shown to the model? | Structured `CONTEXT` | Fenced `DATA` |
| Can it grant tool access? | No | No |
| How do same-key values interact? | Campaign Confirmed may shadow Global Confirmed within this lane | Proposed never shadows Confirmed and becomes review-only when effective Confirmed content has the same key |
| What happens under context pressure? | It is not selectively evicted; after optional material is exhausted, failure is safer than silent omission | It is the first OATH material removed, using deterministic prefix admission |
| Is it guaranteed true? | No | No |

Keeping the lanes independent matters. An agent can propose a correction without erasing or silently replacing an operator's current instruction. An operator can inspect both and create a new Confirmed revision if appropriate.

## 6. The five decisions OATH keeps separate

A single memory can receive five different answers:

1. **Retention:** Does Arcanum store it durably?
2. **Discovery:** Can an index or search system find it?
3. **Eligibility:** Does policy allow it to influence this execution?
4. **Admission:** Does it fit in this exact provider request?
5. **Authority:** What is it allowed to mean?

For example, the Northstar proposal may be retained and easy to discover, yet ineligible in another Campaign. It may be eligible in Northstar but excluded from one small-context provider attempt. If admitted, it still remains Proposed data.

This separation prevents ranking scores from becoming security decisions.

### 6.1 One stable plan per logical turn

At the start of an eligible top-level turn, Arcanum reads one bounded Covenant snapshot and builds one stable Covenant plan. Retries, tool continuations, fallbacks, and compression steps reuse that logical plan. Session history and other memory systems remain separately governed inputs rather than part of one cross-store snapshot.

Each actual model call measures its concrete model, prompt, tools, and context budget. Arcanum freezes the exact request and records what fit. A call that will send protected material also gets the required disclosure evidence; an unprotected call does no disclosure work.

This means a retry cannot silently pick up a memory that changed halfway through the turn.

### 6.2 Clean disabled calls remain clean and cheap

When Covenant injection is disabled and the Session has no protected Covenant history, the ordinary history path does no optional Covenant work. It produces no Covenant prompt bytes and no Covenant tools, and the resulting prompt is byte-for-byte what it was before any of this existed. That is a tested guarantee rather than an intention.

Disabling future injection does not erase history. If a Session already contains protected Covenant-derived material, its reads and derivatives remain protected even while the feature is disabled.

### 6.3 Protected disclosure is acknowledged before sending

Before protected material leaves Arcanum for a provider, external MCP server, process, network destination, message sink, or another content-bearing external effect, the system durably acknowledges the disclosure identity. A sensitive retirement additionally needs exact canonical preflight, Campaign scope, and a one-call capability. Its **Ward** pair is an informational tool audit record, not an approval prompt.

The receipt does not make disclosure reversible. It makes the decision visible, ordered, and recoverable.

## 7. How Arcanum's memory systems fit together

OATH does not replace Arcanum's existing memory systems or force them into one table. Each system has a different job.

| System | Plain-language role | OATH boundary |
|---|---|---|
| **Grimoire** | Encrypted local record book and transaction substrate | Stores canonical and derived records but does not decide their authority by itself |
| **Covenant** | Governed durable claims and operator or agent profile | Supplies Confirmed and Proposed lanes, immutable versions, policy, evidence, and publication rules |
| **Lexicon** | Explicit entity and fact memory | Stores untrusted data rather than automatic Confirmed context; attachment-derived facts retain their typed source provenance |
| **Saga** | Extracted associations and conclusions | Keeps source lineage and inherited sensitivity; retrieval does not prove truth |
| **Tapestry** | Rebuildable hierarchy of summaries | Helps navigate large corpora but remains a derived discovery structure |
| **Weave and Divination** | Embeddings, similarity, and ranking | Find candidates only; they do not establish authority |
| **Session history** | Episodic record of a conversation | Preserves turns and derived summaries with labels and revision evidence |
| **Long Rest** | Planned consolidation and adaptation | Will deduplicate, decay, reinforce, and supersede only with transformation and outcome evidence |

```mermaid
flowchart TD
    Corpus["Searchable workspace chunks, attachments, and history"] --> Discovery["Tapestry, Weave, Divination, and search"]
    Discovery -->|"candidates only"| Gate["OATH policy and authority gate"]
    Direct["Current Session history and materialized attachments"] --> Gate
    OtherMemory["Lexicon and Saga candidates"] --> Gate
    Covenant["Covenant canonical claims and history"] -->|"scope, lane, revision, evidence"| Gate
    Binding["Authenticated operator and canonical Campaign binding"] --> Gate
    Gate --> Plan["Stable turn plan"]
    Plan --> Model["Admitted model context"]
    Model --> Publication["Atomic result, labels, receipts, and staged mutations"]
    Publication --> Direct
```

The useful shorthand is:

> Search proposes candidates; OATH decides what they are allowed to mean.

## 8. Privacy follows the information

Sensitivity is attached to information flow, not just to the table where bytes first appeared.

If protected Session history contributes to a summary, title, fact, embedding, file, notification, or log projection, the result must either:

- carry the combined sensitivity and lineage;
- use a deliberately content-free projection;
- stay inside an approved destination that preserves the same protection;
- leave through the required exact capability and acknowledged disclosure when policy permits; or
- be refused.

The model is not allowed to label its own output as less sensitive than its inputs. Multiple inputs combine conservatively.

### 8.1 Local and external effects are different

Arcanum deletes database records through transactional ownership, revision, and dependency checks. For a managed file, it also proves that the file is still the physical object it created. A file identity or content mismatch becomes a manual blocker rather than permission to delete an unknown replacement.

An external disclosure is different. Provider logs, recipient messages, unmanaged filesystem copies, caches, and backups may outlive Arcanum's local record.

OATH records those effects as nonrevocable. A reset may remove local protected material, but it must not claim that a provider or recipient forgot it.

## 9. Correct, retire, reset, and erase mean different things

These words describe separate operations:

| Operation | What it does | What it does not claim |
|---|---|---|
| **Correct** | Adds a new immutable version and moves the current view | Does not rewrite the old version |
| **Retire** | Adds a tombstone so a claim stops current participation | Does not pretend the claim never existed |
| **Pin** | Marks one entry as the operator's, so the agent may not write over it or retire it | Does not stop the operator changing it, and does not exempt it from anything a timer does — nothing here runs on a timer |
| **Mask** | Stops an installation-wide preference applying inside one campaign, with nothing in its place | Does not delete it, does not touch any other campaign, and does not stop that campaign writing its own value for the same thing |
| **Backup** | Preserves encrypted state and the evidence needed to interpret it | Does not make protected data public or source-free |
| **Restore** | Creates a fresh dataset generation, reconciles imported state, and preserves valid labels and history | Does not import deletion authority for source-machine files or resume stale in-flight turns |
| **Reset** | Removes a governed local memory family and its owned local derivatives | Does not revoke past external disclosure, and is never something a timer does |
| **Erase** | Traverses a specific owned dependency closure and proves local deletion outcomes | Does not delete bytes whose current identity cannot be proven |

A restored database gets a fresh generation identity. That makes old work, old leases, and stale current pointers unable to masquerade as current after replacement.

Restoring an archive that carries governed memory is not something that happens by default. Putting that memory back is a decision only the operator can make, so the restore refuses until they make it explicitly — and refuses to keep it at all if the machine the backup came from could not prove it had never been exposed to unsandboxed tools. From there the only way forward is to say, explicitly, that the memory should be destroyed, which happens in the staged copy before anything is replaced. Either answer is asked for on its own, after the operator has been told what local deletion cannot reach and how much has already left. That answer is now something the operator types: the restore command takes the choice as an option, and a word it does not recognize is refused before anything at all is staged rather than quietly read as the safe default.

Bringing single sessions across from an archive works the same way. Once governed memory is switched on, a session the archive had filed under a campaign is never imported loose: the operator has to say which campaign on this machine it belongs to, by identity rather than by name, because two machines can use the same name for different campaigns and either name can be changed afterwards. With the feature off there is no governed memory to protect, the import works as it always has and files every session under no campaign, and asking for a campaign there is refused outright rather than quietly ignored. If they do not say, the import refuses and tells them which archived campaign is unaccounted for; if the campaign they name does not exist here, that is refused too, before anything is copied.

Leaving by the other door is refused outright. A session that holds governed material cannot be exported as plaintext — no JSON file, no Markdown file, no partial version with the protected parts taken out. The refusal lands before the transcript is even gathered, and there is no answer the operator can give that turns it into an export, because an encrypted archive can be kept under a passphrase or deleted while a plaintext file is simply out in the world once it exists. Destroying the session's governed artifacts afterwards does not re-open that door either: holding them is what is being remembered, not still holding them. A campaign bundle, which never carried that memory to begin with, now reports in plain numbers what it left out, so a campaign with governed memory no longer produces a file indistinguishable from one that never had any.

Housekeeping does not age governed memory out. It may report content-free row, file, session, artifact, and nonrevocable-disclosure counts, but an explicit family erasure is the only removal path.

That internal erasure lifecycle now exists end to end. One exact owner closes admission, revokes and drains old readers, stops the warm writer, and exhausts a bounded inventory on one consistent database snapshot. Damaged catalogs and missing, duplicate, mismatched, or malformed ownership evidence refuse before any effect. The canonical rows and file are erased and proved, an immutable reopen supplies the exact publication candidate, keys, authority, and availability move together, all six old token families stop working, and the same writer opens freshly against the new dataset.

Failure stays closed. Before effects, the old writer must be restored before rollback may reopen admission. After immutable proof, a client disconnect cannot cancel publication, restart, disposition, or durable failure recording. Startup adopts and resumes only one exact durable owner before readiness; a CLI sharing an already-running host does not scan, adopt, or freeze anything.

The public data-lifecycle entry points now use that graph. A Covenant reset first returns a full retention plan whose optional Covenant inventory object contains only the five counts above, while one read lease keeps the snapshot stable through the last response byte. The operator then sees the shared explanation of what local deletion cannot recall, the receipt-backed exact or lower-bound attempt count, and provider help links before confirming. The command sends the preview identity back with apply, so a changed plan is refused before effects. Apply records the exact owner before it closes admission and reports success only after the writer and general admission have reopened and the exclusive lease has been released. It does not turn retained disclosure evidence into deletion totals. A protected planning request that cannot carry its required live lease is refused; ordinary prune/workspace plans and feature-off status continue normally.

A healthy-catalog factory reset keeps its broader promise too: it first requires a measurable current inventory and the exclusive lifecycle, even when Covenant inference is disabled. If either cannot be proved, it stops before deleting ordinary data. Otherwise protected material is removed first, ordinary factory-owned data next, and only then do handle closure, WAL and file proof, publication, and reopen run. The ordinary stage sits between the durable `ManagedArtifactsProcessed` and `HandlesClosed` boundaries. If recovery sees the first, it safely repeats that stage; if it sees the second or anything later, it knows the stage already finished. The same exact owner is resumed, never guessed from the kind of operation.

For a global or all installation reset, nothing is shown or asked until the authenticated host's current protected inventory has been bound into the local plan. If the host, key, inventory, or exact match is missing, the operation stops before active state or shutdown; workspace reset remains offline. After confirmation the CLI carries a typed handoff in memory. The running host, while holding the exact installation lock, publishes an encrypted and authenticated V2 record bound to this profile, installation, operation, location, scope, and plan. It sends the operation identity to the factory engine only as a replay name, receives a different server operation identity, and advances the authenticated record with content-free proof before it responds. The CLI then shuts the host down, takes the maintenance lock, and passes that exact handle through offline continuation: prepare, host apply or replay, proof, shutdown, lock, continuation. An uncertain outcome keeps the replay evidence. Normal startup blocks every active or ambiguous state; only the owning proof-absent prepared V2—or an exact eligible older V1 migrated before effect—may briefly admit a recovery-only `serve`. That decision is now made over two records at once: the reset's own, and the database's separate record of a rebuild in progress. If neither is open, the start is ordinary. If they agree — the reset has not begun its rebuild yet, or it began one that has since finished and been cleared away — the pair raises no objection of its own and the start proceeds under the rule just given. If a rebuild is still open, the host stays closed and asks for a person, because an open rebuild means the database is part way through being remade and starting normally would do the very thing the rebuild shut the door to prevent. And if the two disagree in any way — one says a rebuild was started while the other is simply not there, one claims to belong to work that does not exist, or the two describe different runs — the start refuses, in the same words every time and without saying which disagreement it found. A finished retirement of the reset's own record first marks its anchor closed, then removes the exact record and anchor, and removes the reset key last. The rebuild record works the other way round: retiring it marks its anchor closed and deliberately keeps both of its credentials, since they are the only way an interrupted rebuild could ever be finished, and only the very end of a full reset takes them.

A host whose two host-tools taint markers say it was exposed to unsandboxed tools cannot use that ordinary path. Local confirmation, `--yes`, `--force`, and a document made on the same machine are not independent evidence and cannot clear the block. External remediation is admitted only by the exact CLI form `arcanum data factory-reset --all --apply --external-remediation-attestation <file>`. The file must be owner-controlled, no larger than 64 KiB, and one strict version-1 document. Arcanum checks its P-256/SHA-256 signature against a public root built into the separate Secrets component, binds it to this exact installation, reset operation, live matched marker pair, action, nonce, and a lifetime of no more than 24 hours, and preserves the signed operation identity through recovery. There is no HTTP or configuration equivalent, and the external private signing key is never present in Arcanum.

Even acceptance is deliberately modest. Arcanum stores only encrypted one-way digests proving that this exact operation was authorized; it does not store or print the statement, signature, issuer, nonce, file path, trust root, or key. A new statement must be within its time window. Once the exact claim has been authenticated and stored, that same operation may resume after expiry only if the complete statement and current marker evidence still match; expiry never turns a different statement into valid evidence. What that authorization then runs is exact and bounded. Both host-tools markers are removed through the live records they were read from, one after the other, with the work written down before the first deletion so a crash in between can never leave something that looks untouched. Every Campaign the reset wrote down beforehand ends in one of two states: its marker deleted through the directory that proved it was the right one, or left exactly where it is and reported as needing a person. Nothing is deleted that was not first proven to be the expected thing. The files the database still records outside itself are now accounted for too. Every unfinished write Arcanum was making is either proven cleaned up or reported as needing a person, every file it had finished and labelled is either removed after being proven to be the exact file recorded or left untouched and reported, and the operation refuses to go further unless the numbers add up against the list it wrote down before it started. Arcanum can also now prove, separately, that a profile's restore history is genuinely over — either it never restored, or its last restore closed — and it will not remove the three credentials that could finish an interrupted restore on anything less than that proof. The end now happens too, and only in this order. The database is deleted, which is the one and only thing that removes the joined record of what has ever left this installation — a record that is deliberately impossible to delete any other way. Only then, and only after checking the database file really is gone rather than trusting the sweep that removed it, are the credentials taken — five of them, in two groups, one at a time, each removal written down before the next is touched, so a machine that loses power in the middle picks up where it stopped instead of getting stuck. The three belonging to the restore history go first. The two belonging to the database's own interrupted-rebuild record go last, and only on a second proof of their own: no rebuild journal left on disk, and either a closing mark for this installation or no trace of the slot at all. A reset still holding a rebuild it started and never saw finish stops there rather than taking them, because they are the only thing that could ever finish it. Each group's proof is made while that group's credentials are all still present and is then kept, because once the first one in a group is gone Arcanum could never make that proof again — every removal is checked against what the proof recorded rather than against a set it is halfway through taking. Nothing mints a new identity: the identities live in the database and in credentials this reset removes, so the next start finds nothing to inherit and makes fresh ones. What Arcanum owes is the check that nothing was left behind, and it refuses to report the installation clean if anything was. So an externally authorized full reset can now finish, and say so. On Linux it still cannot: the operating-system marker there refuses to be deleted safely at all, and Arcanum would rather stay closed than claim a deletion it did not make.

Operators never need to interpret cryptographic details. A missing key, missing anchor, mismatched revision, wrong profile/installation/location, rolled-back envelope, malformed file, or ambiguous lookalike all produce the same content-free recovery refusal. Do not delete, replace, rename, or copy these artifacts manually: doing so removes evidence without proving the reset safe. Preserve the state and use the exact resume flow or a verified backup until the blocker is understood.

The durable factory operation also keeps one exact-owner heartbeat from the instant it starts through re-planning, catalog proof, checkpointing, protected erasure, ordinary cleanup, and its terminal result. Restart recovery uses the adopted owner for old and current factory checkpoints and current reset checkpoints. A lost or expired ownership claim stops work and reports only that attention is required; the replay name never becomes the gate or lease owner. The durable phases and checkpoint versions did not change.

The ordinary ways of deleting things — removing one message, compacting a long conversation, forgetting a fact, clearing the search indexes — now all go through the same door. That door checks whether what is about to be removed is governed memory, and if it is, hands it to the one piece of code that knows how to remove it properly: the derived copies go with it, the record that it was deleted is written, and a later retry is told the answer was erased rather than being quietly given a fresh one. Nothing protected is removed by a bulk statement that never looked at it — the bulk paths walk through in pages so every row is examined before anything goes. If a file cannot be removed safely, the whole operation stops and says so, rather than deleting most of it and reporting success. And before an operator is asked to confirm a reset, they are told what local deletion cannot reach and how much has already left the machine — read first, asked second.

## 10. What happens when something goes wrong

OATH fails closed when the missing fact is about authority rather than convenience.

- Missing or malformed origin evidence leads to refusal, quarantine, repair, or erasure.
- A broken full-text or vector index may reduce discovery quality, but it cannot become a substitute canonical source.
- If required Confirmed context cannot fit after evictable material is exhausted, the call fails rather than quietly omitting operator-authorized context.
- A stale generation cannot disclose protected bytes or commit a late result.
- A failed or abandoned agent branch publishes no staged proposal.
- Uncertain external dispatch preserves disclosure evidence and uses a new physical attempt identity for any retry.
- Destructive work closes the affected scope, waits for existing work to finish, and keeps it closed until the journaled operation completes or recovery safely resumes that exact operation.
- A file identity or hash mismatch leaves the bytes untouched and reports a manual blocker.
- A storage layout that is part-way through a declared upgrade reports itself unavailable until the upgrade finishes, rather than claiming a version whose work has not been done.

These rules favor an explicit unavailable state over a plausible but unauthorized result.

### 10.1 Recovery cannot invent authority

Before crossing a dangerous boundary, an operation records who owns it, what exact effect it intends, which scope it affects, and how far it has safely progressed. On restart, recovery may resume only that recorded operation.

Work started inside other work keeps its own record. A database rebuild that a reset set going is picked up again from the rebuild's own journal, while the reset's record stays the authority on the reset itself, and neither is allowed to be reconstructed from the other — which is why two records that disagree stop everything rather than one of them winning.

The operation eventually chooses one outcome:

- commit the change and reopen the scope;
- roll it back and reopen the scope; or
- keep the scope closed because safe completion is not yet proven.

This prevents a restart from treating half-finished work as permission for a different operation.

### 10.2 Upgrading storage is one of those operations

Arcanum's storage layout carries a version number. Moving an existing installation from one version to the next runs a short, ordered list of changes the build itself declares, and some of those changes need a pass over data that is too large to do all at once. Such a pass runs in small batches after startup, and each batch records how far it got in the same moment it records what it did — so a batch that never finished simply runs again, and nothing is ever counted as done twice or skipped once.

The version number moves only when the whole upgrade finishes and the result is checked against what the build expects. Until then the affected capability reports itself unavailable, which is the honest answer: the storage says version one, and version two's promises have not been kept yet. An installation the build cannot make sense of — one written by a newer build, one whose layout was changed by something else, or one whose half-finished upgrade this build cannot pick up — is refused and named, rather than upgraded on a guess.

Some of the shipped storage has moved past version one and some has not, and which is which is what the build itself declares rather than something recorded here. Where nothing has moved, the machinery still runs, finds nothing to do, and says so.

## 11. What exists now and what comes next

**Status as of 2026-09-04**, written on `long-term-memory` and since merged into `main`.

OATH combines built foundations with an activated data-lifecycle erasure surface and dedicated management work that remains unregistered. The feature is still off by default.

| Stage | Human summary |
|---|---|
| **Built** | The Covenant language, SQLCipher catalogs, canonical storage, generation-bound authority, disclosure controls, erasure kernels, retention identity, five-count reset preview, protected plan serialization, direct durable ten-phase reset, combined healthy-catalog factory erasure, authenticated global/all rebinding, owner-only replay handoff and proof-before-shutdown, independently rooted external-remediation verification, its encrypted one-way authorization claim, continuous exact-owner lease maintenance, ordinary-cleanup recovery boundary, canonical and local storage proof, bounded inventory, catalog guard, warm-writer lifecycle, atomic runtime publication, one-shot disposition, exact-owner startup adoption, shared pre-confirmation disclosure, and recovery handlers. Real SQLCipher acceptance proves same-process revocation/reopen and fresh-process adoption/resume. |
| **Activated lifecycle** | `arcanum data reset-memory --scope covenant` and the healthy-catalog global factory data phase enter the protected coordinator. They wait through final reopen/release, and no local result claims provider logs, caches, backups, unmanaged files, or other external disclosures were erased. |
| **Exact marker and Campaign cleanup** | A full reset now removes both host-tools markers through the live records they were read from, and ends every Campaign it wrote down beforehand either deleted or reported as needing a person. It is reachable only from the one command form that requires an externally signed statement. |
| **Managed file reconciliation** | After that, every file the database records outside itself is accounted for: unfinished writes are cleaned up or reported, finished ones are removed only after being proven to be the exact file recorded, and the reset refuses to go further unless the counts match the list written down first. |
| **Restore-credential proof** | Arcanum proves a profile's restore history is over and removes its three restore credentials in a fixed order, one at a time, checking each still holds what the proof said. It runs only after the database is provably deleted. The two credentials belonging to the database's own interrupted-rebuild record follow, on a separate proof that no rebuild is left to finish, and are the last thing the reset takes. |
| **A rebuild inside a reset** | A full reset that has to rebuild the database now writes down that it is starting one before it starts it, under a name the rebuild itself takes up, and writes down what came back when it ends. The rebuild works out that it belongs to that reset by reading the reset's own record, so a machine that restarts halfway through reaches the same conclusion it reached the first time. At startup the two records are read as one answer: agreement carries on, an unfinished rebuild keeps the host closed and asks for a person, and any disagreement refuses in the same words every time. |
| **A full reset that finishes** | An externally authorized full reset now deletes the database, removes what is left — five credentials in two groups, the rebuild pair last — checks nothing survived, and reports the installation clean. Every ordinary cleanup, and every erasure short of this one, still keeps all five. |
| **Storage upgrades** | An existing installation can now be carried from one storage version to the next through changes the build declares, including data passes that run in bounded batches and survive a restart. Upgrades have shipped since — how many is the build's own declaration rather than a number kept here — and an installation that has taken them is compared against the same tree a brand-new one installs — which is the thing that would otherwise go wrong silently, because a developer's own database is always brand new. |
| **Curating what is remembered** | An operator can now correct one preference in place, naming the exact version and the exact text they decided was wrong so a correction can never land on something they never read; pin one so the agent may not touch it; and stop an installation-wide preference applying inside a single campaign. Every one of those direct operator mutations says what will be true before it asks. An eligible agent retirement instead records its exact target, binds a one-call capability, acknowledges disclosure before the effect, and emits an informational Ward audit pair without opening a prompt. |
| **Still unregistered** | Covenant repair, rebuild, path, and Session-binding management routes and commands remain separate work. |
| **Curating what was remembered for you** | An operator can now read one automatically extracted memory in full, replace its text, take it out of what the assistant can recall, put it back, and mark it so the automatic clean-up will not remove it. Every change that speaks about the memory's text names the exact text the operator read, checked at the moment of the write, so a change can never land on something they never saw. Taking a memory out also leaves a small unreadable fingerprint of what was rejected, which is what stops the next automatic pass from quietly putting it back — and asking for something a memory already is — already out, already back — succeeds and says which happened rather than arguing. Marking a memory to be kept is the one change that makes no statement about its text, so it needs no proof of what was read and simply takes effect. |
| **Campaign-scoped recall** | Every Saga memory now records who owns it, and the Lexicon can hold one entity per Campaign. `features.campaignScopedMemory` makes retrieval, inspection, and reset honor that ownership together. It ships **off**, so an installation that never names it recalls exactly what it recalled before. |
| **Remaining roadmap** | Full-installation marker/Campaign compare-deletion, remaining managed-file cleanup, identity rotation, release qualification, and later rollup, consolidation, curation, evaluation, scoped-recall, caching, delegation, and bitemporal work. |

One practical caveat worth knowing: the shipping build targets `osx-arm64`, `win-x64`, and `win-arm64`. Only the macOS native asset is verified today; the two Windows assets are absent, and those builds intentionally fail rather than quietly falling back to a system library.

The formal [OATH architecture](Arcanum.OATH.md) carries the precise status. The reset/factory data-lifecycle machinery is composed and activated; the Covenant write, read, and curation surfaces are registered, and the repair, rebuild, path, and Session-binding surfaces remain separate.

## 12. What OATH does not claim

Clear boundaries make the architecture more useful.

OATH is not:

- **A truth oracle.** Confirmed means operator-authorized, not objectively correct.
- **Automatic promotion.** A model cannot make a proposal authoritative through confidence, repetition, summarization, or retrieval frequency.
- **One universal memory database.** Arcanum keeps distinct stores and projections with distinct roles.
- **A blockchain.** Digests and receipts provide local canonical identity and evidence, not public consensus.
- **A distributed transaction.** Local publication can be atomic; provider and filesystem effects require receipts and recovery.
- **Remote erasure.** Arcanum cannot reliably delete provider logs, recipient copies, caches, or unmanaged backups.
- **Full bitemporal reasoning today.** Immutable history and generations exist as foundations; full valid-time semantics remain planned.
- **Ambient subagent access.** Subagents, daemons, batches, and unattended work receive no protected memory by default. Future delegation must use explicit least-authority capsules.
- **Perfect operating-system isolation.** Same-user native code and explicitly trusted external tools remain part of the installation's trust boundary.

OATH does permit authority to become narrower. It also permits an authenticated operator to create a new, more authoritative claim. It forbids hidden amplification by the memory pipeline itself.

## 13. Plain-language glossary

| Term | Plain meaning |
|---|---|
| **Admission** | The decision about which eligible material fits one concrete model request |
| **Authority** | What a memory is allowed to mean or influence, independent of its wording |
| **Campaign** | Arcanum's persistent project and authority scope; it may reference a verified Workspace root but is not itself a Workspace |
| **Canonical** | The authoritative durable representation, rather than a rebuildable index or cache |
| **Claim** | A durable assertion with origin, scope, authority, lineage, and revision history |
| **Confirmed** | Operator-authorized Covenant context; not a guarantee of truth |
| **Derivative** | A summary, fact, embedding, file, or other artifact made from earlier information |
| **Discovery** | Finding a candidate through search, ranking, or hierarchy |
| **Generation** | A random dataset identity that separates state before and after reset or restore |
| **Grimoire** | Arcanum's encrypted local persistence and transaction substrate |
| **Lineage** | Evidence connecting a memory to its sources, versions, turns, and transformations |
| **Proposed** | Campaign-only, agent-suggested Covenant data that remains untrusted |
| **Receipt** | Durable evidence binding a particular mutation, provider attempt, disclosure authorization, or lifecycle outcome; its exact meaning depends on the receipt type |
| **Sensitivity** | The conservative protection level that follows information and its derivatives |
| **Tombstone** | An immutable retirement version that stops current use without erasing history |
| **Ward** | Informational per-tool audit record; retained active-record routes exist for compatibility |

## 14. Where to go next

- Read [`Arcanum.OATH.md`](Arcanum.OATH.md) for the complete technical architecture, invariants, implementation layers, recovery contracts, verification model, and source map.
- Read [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md) for the authoritative shipped architecture and persistence design.
- Read [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md) for a wider plain-language tour of the entire Arcanum system.
- Read [`Arcanum.CHAT-LOOP.md`](Arcanum.CHAT-LOOP.md) for the detailed shared model and tool loop that OATH's runtime rules extend.

The idea to carry forward is simple:

> Every memory travels with its papers. Search may make it easier to find. A model may make it shorter or more useful. Only authenticated authority can make a new claim more powerful.
