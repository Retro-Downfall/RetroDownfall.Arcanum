# Issue #96: Covenant exact-version correction, pinning, unpinning, and scope masks

**Status:** Approved in chat on 2026-08-26; implementation follows the plan of the same date.

**Branch:** `codex/issue-96-covenant-version-operations`, cut from `long-term-memory`, merged back with `--no-ff`.

**Issue:** #96, an XL delivery slice under #78, blocked by #88 (closed) and #102 (closed), blocking #99 and #100.

**Names settled on the issue:** a **correction** appends a version to an existing head while naming the exact version it believes it is replacing. A **pin** marks one scoped lane head as operator-durable. A **scope mask** suppresses a Global key inside one Campaign. Together, pin, unpin, mask and unmask are the **curation** protocol; `covenant_curation_*` is their table prefix.

## 1. Objective

Extend the mutation kernel so protected curation names exactly what it is about to change — the version, the branch, the revision, and the compiled hash — and refuses everything it cannot prove it was shown. Correction appends an immutable version. Pin, unpin, and scope-mask changes run the same prepare-and-apply, operator authority, compare-and-swap, and idempotent-receipt protocol the existing mutations do, over storage of their own.

The same change lands the agent half the tool surface has been waiting for: the retirement preflight resolved outside the inference hot path, the Ward that shows the operator the exact content about to disappear, the disclosure that commits before any effect, and the capability that binds all three. `retire_covenant` stops being a tool that can only refuse.

## 2. Scope, settled before design

Four framing questions were put to the operator and answered before any code was designed.

- **Delivery shape.** Delivered whole rather than decomposed into children. One branch, one merge, one issue closed.
- **What a pin does.** The Covenant is the one retention class with no time rule, so the parent issue's "exempt from consolidation, decay, and retention pruning" has no sweep to be exempt from here. A pin refuses **agent-authored** mutation of the head it marks. The operator's own set, correct and retire still work: a pin the operator has to fight is a pin they stop using.
- **What a scope mask is.** An operator masks Global key `K` for Campaign `X`: `K` reaches no turn in `X`, and no fallback content replaces it. It is the counterpart of retirement, which lets the Global entry resurface. A mask suppresses only the **Global candidate** — if `X` later sets its own `K`, that entry applies normally.
- **How far the slice reaches.** The agent half is in scope: the Ward flow, the retirement preflight disclosure, the egress guard, and un-withholding `retire_covenant` from `tools/list`.

Two further decisions were ratified when the design was presented.

- **Correction is Confirmed-lane only,** matching the existing rule that an operator authors the Confirmed lane and only the Confirmed lane. A correction naming a Proposed version is the wrong-branch refusal. An operator who wants an agent's proposal promotes it with `set`.
- **A mask suppresses only the Global candidate,** never the key wholesale, so a later Campaign-scoped `set` for the same key is not silently inert.

## 3. Governing constraints

- **SQLite cannot `ALTER` a `CHECK`.** Three shipped tables bake in the current vocabulary: `covenant_versions.OperationCode CHECK (IN (1, 2))`, `covenant_heads.CurrentOperationCode CHECK (IN (1, 2))`, and `covenant_mutation_receipts.MutationKindCode CHECK (IN (1, 2, 3, 4))`. No new `CovenantOperation` and no new `CovenantMutationKind` member may be declared, because each would force a rebuild of tables that carry foreign keys, guard triggers, and every operator's history.
- The Covenant canonical tier's version-1 source fingerprint is `7F906C4C832FDF824EC3B6A56431E9E6098DC9BB83EDA5BAE02EC62CE3B4E105`, read out of the head tree before any object file was added. Nothing can recompute it afterwards. A fixture reconstructs the version-1 tree by removing the curation objects from the shipped list, and a test hashes that reconstruction, so a wrong pin fails there rather than against every operator's installation.
- A version step that only **adds** objects avoids the ALTER shape trap entirely: `CREATE TABLE` stores its statement verbatim, so a fresh installation and an evolved one agree as long as each transition file carries its head file's statement character for character.
- Raw SQL through the declarative schema tree, one object per file, `CREATE ... IF NOT EXISTS`. No EF entity, no numbered migration, no compiled-model regeneration.
- Native AOT throughout: no reflection-based serialization, no dynamic type loading.
- Every new durable table joins the existing lifecycle: backup protected-state inventory, retention inventory, factory reset, memory reset, and Campaign cleanup.
- Every new public request and response shape is declared in `CovenantPublicContractInventory` and validates itself through `CovenantWireValidation`.
- Documentation in `docs/` names capabilities, never issues.

## 4. Considered approaches

### 4.1 New operation and mutation-kind codes, with a three-table rebuild — rejected

Model correction, pin, unpin, mask and unmask as new `CovenantOperation` members so every curation change appends a `covenant_versions` row, and as new `CovenantMutationKind` members so every one of them lands in `covenant_mutation_receipts`. One history, one receipt ledger, one vocabulary.

Rejected on cost and risk. It requires rebuilding `covenant_versions`, `covenant_heads`, and `covenant_mutation_receipts` — three tables joined by a composite foreign key, guarded by nine triggers, and holding the only copy of an operator's standing agreement — through the twelve-step SQLite table-rebuild dance, inside a tier that has never run a version step. It also breaks the content invariant `covenant_versions` enforces: a pin carries neither compiled content nor a tombstone, so the table's `OperationCode = 1 ⇒ content NOT NULL` / `OperationCode = 2 ⇒ content NULL` pair would have to grow a third arm whose only member stores nothing, which is a row shape the table exists to prevent.

### 4.2 Pin and mask as columns on `covenant_heads` — rejected

Add `IsPinned` and `IsMasked` columns to `covenant_heads` with `ALTER TABLE ... ADD COLUMN`, and record the change nowhere else.

Rejected on two counts. `ALTER` splices its column definition in front of the closing paren without the preceding newline, so the head `.sql` file must be written in the exact shape SQLite leaves behind or every evolved installation reports `DefinitionDrift` while every fresh one passes — the hardest shape of that failure to reproduce, because a developer's own database is always fresh. More decisively, a mask must be able to name a key the Campaign has no head for: masking Global `K` inside Campaign `X` is precisely the case where `X` holds no entry, no head, and no version for `K`. A column on `covenant_heads` cannot express it without manufacturing a head row that points at a version nobody authored.

### 4.3 Correction as a bound `Set`, curation as add-only tables — selected

Correction is not a new operation. `Set` already appends an immutable version, links `PredecessorVersionId`, and preserves provenance, sensitivity, and disclosure evidence by construction. What the issue asks for is the **binding**, and a binding is a comparison rather than a row shape.

Pin, unpin, mask and unmask get their own append-only tables in a new Covenant canonical version, added and never altered. They keep the protocol — typed preflight, five-minute bound token, operator authority, compare-and-swap, durable idempotent receipt — and share none of the storage, because what they record is not a version of the operator's text.

## 5. Correction

### 5.1 The contract

`arcanum memory covenant correct <key>` and the route pair `POST /api/memory/covenant/correct/prepare` and `POST /api/memory/covenant/correct`. Authored content arrives through `--file` or piped standard input, exactly as `set` takes it, so a preference never lands in shell history or in the process list of a shared machine.

The request names four target facts beyond what `set` carries:

| Field | What it proves |
|---|---|
| `TargetVersionId` | the operator is correcting the version they looked at, not whatever is current now |
| `TargetLane` | the branch that version belongs to — the two lanes are independent revision chains over one entry |
| `ExpectedRevision` | the compare-and-swap the kernel already performs |
| `TargetRenderedHash` | the operator saw this **content**, which a revision number alone cannot establish |

### 5.2 What is refused before mutation

| Condition | Class |
|---|---|
| the named version does not exist | stale target |
| the named version is not that lane's current head | stale target — an older revision is a guess about what is current |
| the named lane is Proposed | wrong branch — an operator authors Confirmed and only Confirmed |
| the rendered hash disagrees with the head's | guessed target — the operator never saw this content |
| the head is a tombstone | refused; reinstating a retired key is `set --reactivate`, which is a different sentence |
| the head's compiler or renderer policy is unsupported | quarantined target |

### 5.3 Three-way equality, and why the token alone is not enough

Commit compares the request's stated target fields against the token body's, and the token body's against live state. Enforcing only body-against-live would let a commit name target `X` while carrying a token bound to `Y`, succeed against `Y`, and report success to a client that believes it corrected `X` — the exact split between what an operator was shown and what they committed that the two-step protocol exists to close.

The binding travels in the preflight token rather than in the request digest. `CovenantOperatorPreflightBody` moves to format version 2 and gains `TargetVersionId` and `TargetRenderedHash` as presence-byte optionals; `PreflightBodyDigestInput` gains the same two. That preimage is ephemeral — a token lives five minutes and is never stored — so no durable digest moves.

`MutationRequestDigestInput` is deliberately **not** touched. Its output is stored in `covenant_versions.RequestIdempotencyDigest` and `covenant_mutation_receipts.RequestIdempotencyDigest`, and commit recomputes it to resolve an already-committed identity. Changing the preimage would make a client retrying a mutation it committed before the upgrade receive an idempotency conflict instead of its own receipt.

A correction commits as `OperatorSet`, so the receipts table's kind vocabulary is untouched. The distinction between a correction and an ordinary write is what the caller had to prove, not what the installation ends up holding — and the version chain records the lineage either way.

## 6. The curation protocol

### 6.1 Subject

`(ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch)`. That is a head's tuple plus the key's reclamation epoch, and it deliberately does not require a head to exist: masking Global `K` inside Campaign `X` is exactly the case where `X` holds nothing for `K`.

The epoch is what stops a curation row outliving its subject. A key that is retired, reclaimed, and later re-created is a different key wearing an old name; a pin recorded against the earlier epoch is inert rather than silently applying to content the operator never saw.

### 6.2 Storage — Covenant canonical schema version 2

Three tables and their guard triggers, all added and none altered.

- **`covenant_curation_versions`** — append-only history. One row per accepted change: the subject tuple, the curation kind, its revision in that subject's own chain, its predecessor, the mutation identity, and the three digests every mutation carries. Immutable by trigger, exactly as `covenant_versions` is.
- **`covenant_curation_heads`** — the guarded current pointer per subject: whether it is pinned, whether it is masked, and the version that last said so. The nullable Campaign identity is keyed by the two partial unique indexes `covenant_entries` already uses, because a `NULL` inside a SQLite primary key does not enforce uniqueness.
- **`covenant_curation_receipts`** — the idempotency ledger, mirroring `covenant_mutation_receipts` so a replayed commit resolves through its stored outcome rather than running a second time.

Two shapes are unrepresentable rather than refused. A mask requires `ScopeCode = 2`, because a Global mask has no broader scope to fall back from. A mask requires `LaneCode = 1`, because the Proposed lane is review-only beside effective Confirmed content and masking it would change nothing an operator could observe. A Global row requires `CampaignId IS NULL`, on the same terms as every other Covenant table.

### 6.3 What a pin binds

An agent-authored mutation targeting a pinned head is refused: a proposal that would supersede it, and an approved retirement that would tombstone it. Enforcement runs in the write authority inside the publication transaction, which is the one place that cannot be bypassed, and is reported early through the staging head probe so a model receives a typed refusal rather than costing the turn its answer.

**A Global pin binds nothing an agent can reach today,** and the design says so rather than letting a test imply otherwise. Agent staging requires a canonical Campaign binding by the capability's own constructor, so the Proposed lane and agent retirement are Campaign-scoped by construction. A Global pin is durable state the bulk-action and erasure surfaces will consult; here it is recorded, reported, and enforced at the one place agent authorship could ever arrive.

### 6.4 What a mask binds

The turn snapshot loads the evaluating Campaign's masks alongside its heads. The linker drops a Global Confirmed candidate whose key is masked for that Campaign, and reports it as `CovenantPlanDecision.Masked` rather than folding it into `Shadowed`: a shadow names the entry that replaced it, and a mask names nothing. The mask joins the snapshot digest, because two snapshots holding identical candidates under different masks produce different plans, and a shared digest would make those two states indistinguishable to every staleness comparison downstream.

A mask suppresses the Global candidate alone. A Campaign that masks `K` and then sets its own `K` gets its own value, because the alternative makes a later `set` silently inert.

### 6.5 Explaining the fallback before commit

The effect preview says which of the two things is about to happen, in the operator's own words, before the confirmation is put:

- retiring or masking a **Campaign** entry lets the Global Confirmed entry resurface, which the mutation effect already reports;
- masking a **Global** key inside a Campaign stops it applying there and puts nothing in its place.

Both sentences are read off the server's measurement rather than off what the client believes, for the same reason the compiled hash and the affected-Campaign count are.

## 7. The agent side

The intent has been recorded as deferred since the tool surface shipped: the pipeline that resolves the retirement preflight outside the inference hot path, raises the Ward, mints the capability, and drives the egress guard. Every contract it needs already exists and is reachable by nothing. The proposal half went live earlier; this is the retirement half.

1. **Preflight resolution.** `CovenantRetirementPreflight` is resolved from canonical state for one Campaign, key and lane: the entry, the head version, its revision, the compiler-sanitized fragment the operator will read, the rendered hash, whether Global content starts applying in its place, the key epoch, and the target-bound token digest the staged tombstone carries as evidence.
2. **The Ward.** Raised where every other tool Ward is raised, resolved through `CovenantEgressWardPolicy` rather than the generic classifier, so switching Wards off **denies** a retirement instead of executing it unwarded. `retire_covenant` is already an intrinsic Ward tool. The operator is shown the resolved preflight — the content about to disappear, its lane, its revision, and whether Global content will start applying — not the model's arguments.
3. **The carve-out on auto-approval.** A guessed, pressured, review-only, quarantined, stale, or unseen proposal cannot gain **auto-approved** retirement authority. Configured auto-approval is the only thing that carve-out removes: such a target still reaches the interactive Ward, because the operator may legitimately want to retire a proposal the turn never carried. What it may not do is self-approve.
4. **The egress guard.** `CovenantToolEgressGuard.DiscloseThenAsync` commits the `McpToolUse` receipt before the effect and stops the effect entirely when it cannot.
5. **The capability.** The turn's staging material carries the per-call retirement preflight and Ward receipt, so the mint can build a retirement capability. It already refuses to build one without both.
6. **Advertisement.** `retire_covenant` leaves the withheld list, because a capability can now be minted for it. A pinned head is refused before the Ward is raised, so the operator is never asked to approve something that cannot be applied.

## 8. Reading it back

Pin and mask state is reported wherever lifecycle already is: the detail and list projections carry it, the CLI renders it beside the lane heads, and the retention dry-run states the exemption the parent issue requires it to state. A curation state nobody can see is a curation state an operator cannot trust.

## 9. Lifecycle

The three tables join `BackupRestoreProtectedStateInspector.CanonicalContentTables`, from which the retention inventory derives its list rather than restating it. Campaign cleanup deletes a Campaign's curation rows in the transaction that deletes its heads, so a mask cannot outlive the Campaign it applied to. Family reinitialize and factory reset reach them through the catalog.

## 10. Testing

Every acceptance test enters through the outermost production entry point — the mapped route or the registered CLI verb for the operator half, and a turn that actually emits a `retire_covenant` call for the agent half — and seeds nothing it asserts. A precondition is reached by driving the production write path, never by writing rows: an entry to be corrected is written with `set` first.

Before the slice is called done, the behaviours the acceptance criteria name are broken in the source one at a time and the suite is confirmed to fail: the rendered-hash comparison, the wrong-branch refusal, the pin's refusal of agent authorship, the mask's effect on the linker, and the Ward's denial when Wards are disabled. For every branch a test proves, the production call site that reaches it is identified, because an optional parameter defaulting to null is where reachability quietly dies.

## 11. Closed inventories this change grows

- `CovenantPublicContractInventory` — every new request and response shape.
- `GrimoireSchemaTransitionResourceTests` — every new transition statement, pinned by name in install order.
- `GrimoireSchemaVersionChainTests` — the Covenant canonical head version literal.
- A Covenant canonical version-1 fixture, peeling back the curation objects, proving the pin in §3.
- `CovenantArchitectureBoundaryTests` — any component that writes Covenant state or deletes a retained table.
- `SettingDescriptors` and its coverage count, if a configuration key is added. None is planned.
