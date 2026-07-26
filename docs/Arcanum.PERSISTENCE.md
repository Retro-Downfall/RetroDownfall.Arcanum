# Persistence strategy

Arcanum's primary persistent store is the **Grimoire**, an encrypted SQLite (SQLCipher) database managed through EF Core with a compiled model, AOT-safe source-generated JSON contexts, and embedded hand-authored SQL migrations. This document tracks which operational state lives in the Grimoire, which still lives in memory, and the conventions each subsystem follows when it moves from one to the other.

One deliberately **non-Grimoire** persisted artifact exists alongside it: the **persisted inference audit log** (`Arcanum:Host:AuditLog`, disabled by default, DESIGN.md §8.26) — plain dated JSONL files (`~/.config/arcanum/audit-YYYYMMDD.jsonl` by default), not a SQLite table. It records operational metadata about completed inference turns (model, tokens, latency, tool activity), and is intentionally kept out of the encrypted Grimoire so operators can `tail`/`grep`/ship it with standard log tooling without needing the Grimoire's decryption key.

**Session attachment bytes** follow the same split as OpenAI `/v1/files`: metadata in the Grimoire (`SessionAttachments`), raw bytes on disk under `~/.config/arcanum/attachments/` — **not** SQLCipher-encrypted (see §13). The audit log remains out of scope for the rest of this document's Grimoire-backed state tables; attachment metadata is in-scope below.

**Weave / Saga / workspace embedding tables** (`entry_embeddings`[+`_vec`], `workspace_file_*`, `saga_*`) are raw-SQL schemas created by `WeaveSchemaInitializer` after migrations (dimensions from config; optional sqlite-vec `vec0`). Behavioral gates and degradation: DESIGN.md §21. Reset: `POST /api/embeddings/reset`.

This is a living document. It is updated each time an in-memory subsystem gains Grimoire persistence (see `docs/Arcanum.DESIGN.md` §2.2 for the tracked backlog of remaining amnesiac gaps).

## 1. Where state lives

| State | Table | Status |
|---|---|---|
| Sessions, Entries, MageSettings (Lore — legacy operator key-value), Campaigns, Apprentices, WorkspaceContexts | `Sessions`, `Entries`, `MageSettings`, `Campaigns`, `Apprentices`, `WorkspaceContexts` | Existing. `MageSettings` is no longer model-directed — the Lore MCP tools are removed; it remains an operator-only surface (`/api/lore`, `arcanum lore`). Agent memory is The Lexicon. |
| **The Lexicon** (agent-directed entity memory: Name + Type + Facts per entity) | `lexicon_entries` + FTS5 `lexicon_fts` | Existing — raw-SQL tables created by `LexiconSchemaInitializer` at Grimoire bootstrap (alongside `WeaveSchemaInitializer`), **not** part of the compiled EF model and **not** an EF migration. Accessed via `LexiconService` over the scoped `ArcanumDbContext` connection + `SqliteBusyRetry`. See DESIGN.md §10.6. |
| Migration history | `__EFMigrationsHistory` | Existing |
| **Unseen Servant watermarks** (last-run timestamp + dynamic interval override, per job) | `UnseenServantWatermarks` | Existing |
| Daemon execution history | — | Deferred (in-memory, `InMemoryDaemonExecutionRepository`) |
| **Sanctum breach history** (per-campaign audit trail: tool, breach type, description, JSON details) | `SanctumBreaches` | Existing — replaces the former in-memory ring buffer (`SanctumBreachStore`, retired) |
| **Idempotency-Key claims** (replay cache for side-effecting inference; DESIGN.md §11.17) | `IdempotencyClaims` | Existing — claim-key ≠ fingerprint; state machine + lease; TTL swept hourly by `UnseenServantService`. Legacy `IdempotencyKeys` may still exist for sweep compatibility. |
| **Inference accounting** (runs, billable ops, budget reservations, cost adjustments; ADR 0002) | `InferenceRuns`, `BillableOperations`, `BudgetReservations`, `CostAdjustments` | Existing — hand-authored SQL migration `20260721010000_AddInferenceAccountingAndIdempotencyClaims`; raw SQL via `ITurnRunWriter` / `IBudgetReservationService`. `BillableOperations.ReasoningTokens` is a non-null count and reasoning remains a subset of output. **Not** part of the compiled EF model. Session `TotalCostUsd` remains a projection only. |
| **Uploaded file metadata** (`POST /v1/files`; DESIGN.md §11.20) | `UploadedFiles` | Existing — row is metadata only; file bytes live on disk under `ArcanumPaths.FilesDirectory`, named by a fresh GUID (never the client filename) |
| **Session attachment metadata** (Command Center + host; DESIGN.md §10.2.5) | `SessionAttachments` | Existing — hand-authored SQL migration `20260719180000_AddSessionAttachments`; raw SQL via `ISessionAttachmentStore` / `SessionAttachmentStore`. Bytes under `ArcanumPaths.AttachmentsDirectory` (original filenames preserved under version folders). **Not** part of the compiled EF model. |
| **Batch job metadata** (`/v1/batches`; DESIGN.md §11.21) | `Batches` | Existing — no request-count columns; `GET` computes `request_counts` on the fly by reading the input/output/error files off disk (all three are themselves `UploadedFiles` rows) |
| **Session accumulated cost** (USD spend per session, updated atomically with `TotalTokensUsed`) | `Sessions.TotalCostUsd` | Existing — `NUMERIC NOT NULL DEFAULT 0` column on the existing `Sessions` table (precision 18, scale 8) |
| **Budget alerts** | `BudgetAlerts` | Existing — unique `(Threshold, date(AlertedAt))`; insert-before-dispatch so the index is the dedup authority. |
| **Embedding reset** (`POST /api/embeddings/reset`) | `entry_embeddings`, `entry_embeddings_vec`, `workspace_file_embeddings`, `workspace_file_embeddings_vec`, `workspace_file_chunks`, `saga_memory_embeddings`, `saga_memory_embeddings_vec`, `saga_memories`, `saga_extraction_watermarks` | Existing — raw-SQL tables created by `WeaveSchemaInitializer`; cleared by `EmbeddingsResetService` in a single transaction per scope (`all`, `entry`, `workspaceFile`, `saga`) |
| **Entry pinning** (`IsPinned` flag on conversation entries; pinned entries survive read-time context compression and are included in inference context even when older than the compression watermark) | `Entries.IsPinned` | Existing — `INTEGER NOT NULL DEFAULT 0` column on `Entries` with index `IX_Entries_SessionId_IsPinned`; compiled EF model regenerated with `dotnet ef dbcontext optimize` |
| Apprentice Chronicle (lifecycle/execution events) | — | Deferred (in-memory bounded channel, `ChronicleHub`) |
| Active Wards | — | **Not persisted by design** (see §7) |
| A2A task id ↔ Apprentice id mapping (§5.7.1) | — | **Not persisted by design** (see §7) |

## 2. Serialization strategy

The `UnseenServantWatermarks` table uses **scalar columns only** — no JSON blob, no `JsonSerializerContext` registration needed:

- `JobKey TEXT` — the composite key `{Name}\0{TargetSpell}` the scheduler already uses internally (`UnseenServantJobTracker.JobTrackingKey`)
- `LastRunAt TEXT` — ISO 8601 UTC (`DateTimeOffset.ToString("o")`), sortable as text, same convention as `Entries.CreatedAt`
- `EffectiveIntervalMinutes INTEGER` — `0` means "no override, use the configured interval"

Future tables that need structured payloads (e.g. a daemon execution's correlated log entries, or a Sanctum breach's request context) should serialize through one of the existing source-generated `JsonSerializerContext` types rather than introducing ad hoc `System.Text.Json` calls:

- **`GrimoireJsonContext`** — pattern-domain types (`PatternSnapshot`, `DomainType`)
- **`TheForgeJsonContext`** — campaign/session/sanctum domain types (`CampaignSettings`, `SanctumConfig`, `Session`, `Entry`, etc.)
- **`ArcanumJsonContext`** — the full API wire surface (DTOs, requests, responses)

If none of these fit a new payload shape, add a new `JsonSerializerContext` rather than extending an unrelated one — keeps AOT trim analysis scoped and avoids accidentally widening an existing context's reachability graph.

### 2.1 Reasoning is an ephemeral boundary

Client-safe reasoning may appear in native buffered `reasoning` segments, live `IntelligenceEvent` NDJSON `reasoning` frames, or additive OpenAI `reasoning_summary` / `reasoning_content` fields, but it is not conversation history. CLI `ask`/`chat`, Command Center, and The Forge Tome render it only in bounded in-memory views; reload/export paths remain answer-only. Grimoire `Entries` persist only user and assistant answer text.

Provider `TextReasoningContent.ProtectedData` is stricter still: it may remain on the raw in-memory assistant message long enough for a tool result to continue with the same provider, but is never projected, logged, audited, traced, exported, or stored. Reasoning does not enter Apprentice step results, Master context, prompts, checkpoints, or Chronicle events: `ApprenticeStreamFramePolicy` explicitly ignores `Reasoning` and defaults unknown future event kinds to ignore. Provider-reported `BillableOperations.ReasoningTokens` and `InferenceAuditRecord.ReasoningTokens` are counts only. The Forge's separate local inference-trace store may retain event type, output mode, and token count, but replaces the message with a fixed redaction and nulls data before local persistence or JSON export.

## 3. Migration strategy

New tables ship as embedded `.sql` files under `src/RetroDownfall.Arcanum.Infrastructure/Data/SqlMigrations/`, named `<yyyyMMddHHmmss>_<Name>.sql` (14-digit UTC timestamp prefix, matching every existing migration). The file is:

1. Added to the `MigrationOrder` array in `GrimoireSqlSchemaMigrator.cs`, in the order it should apply (always append — never reorder or remove existing entries)
2. Auto-embedded by the Infrastructure project's csproj glob — no manual `<EmbeddedResource>` entry needed
3. Applied by `GrimoireSqlSchemaMigrator.ApplyPendingAsync` on first host start, one script per `SqliteTransaction` alongside its `__EFMigrationsHistory` insert (the migrator owns both; scripts contain DDL only — no `BEGIN`/`COMMIT`, no history row)

**Squashing.** Because Arcanum has no production Grimoire databases in the wild, there is no installed base whose upgrade path would break if the incremental history were collapsed. When the migration list grows long enough that it reads more like archaeology than documentation, it is squashed back down to a single `InitialCreate` migration id — both the EF Core `.cs`/`.Designer.cs` pair (regenerated via `dotnet ef migrations add InitialCreate` against a clean `Data/Migrations/` folder) and its hand-authored SQL twin under `SqlMigrations/` sharing that same id, with `GrimoireSqlSchemaMigrator.MigrationOrder` reset to the single entry. A squash is only valid if the resulting schema is byte-for-byte identical to what the old chain produced — verified by replaying the deleted scripts in order against a scratch SQLite file and diffing `pragma_table_info`, index, trigger, and foreign-key output against the new single script. The `UnseenServantWatermarks` and `SanctumBreaches` tables (previously separate additive migrations) are folded into the current `InitialCreate.sql` baseline this way — both are single `CREATE TABLE` statements with no changes to other tables or columns, so folding them in changed nothing about their shape.

**Reasoning-accounting install-script exception.** `20260721010000_AddInferenceAccountingAndIdempotencyClaims.sql` was edited in place so the original `CREATE TABLE BillableOperations` declares `ReasoningTokens INTEGER NOT NULL DEFAULT 0` directly; there is no follow-up `ALTER TABLE` and no new migration. Because an existing database has already recorded that migration id, it cannot receive the changed column by replay. The supported upgrade path is a mandatory stop/delete/reinstall of the local Grimoire (§10), not a data migration.

## 4. Retention/eviction policy

Watermarks are **one row per configured job key** (cardinality ≈ configured `Arcanum:Daemon:Jobs`). No TTL. Orphaned rows for deleted jobs are inert. `DeleteAsync(jobKey)` exists for callers; nothing auto-deletes on config change.

**`SanctumBreaches`:** most-recent-N per campaign on write (`SanctumConfig.MaxBreachCount`, clamp 100–100,000).

## 5. Crash consistency

Watermark writes are **write-through** (no batching). WAL + `SqliteBusyRetry` cover durability/contention. Failed writes are logged and swallowed — in-memory state may diverge until the next successful write.

## 6. Read path

`UnseenServantService` hydrates once at `ExecuteAsync` start (after Grimoire migrations):

1. `IUnseenServantWatermarkStore.GetAllAsync()`
2. Hydrate `IUnseenServantJobTracker` (last-run timestamps; overdue → keep `LastRunAt`, mark result) and `IUnseenServantPacer` (interval overrides)
3. Hydration failure → warn and fall back to startup jitter (never fatal)

**Persisted vs process-local:** `LastRunAt` and dynamic interval overrides (`EffectiveIntervalMinutes`) persist and hydrate into the tracker + pacer. **`nextDueAt` is reconstructed** in memory as `LastRunAt + effectiveInterval` (plus startup jitter on first dispatch). **`LastResult` is process-local** — hydrate seeds `"Overdue (host was down)"` or `"Restored from Grimoire"`; real run summaries are never written to the Grimoire.

**Overdue on hydrate:** if `LastRunAt + EffectiveIntervalMinutes < now`, hydrate **keeps the real `LastRunAt`** and marks `LastResult = "Overdue (host was down)"` so the job stays due and can dispatch on the first tick (comment in `UnseenServantJobTracker.HydrateAsync`). It does **not** pretend the job just ran. Lost/corrupt watermarks → warn and fall back to startup jitter (same as failed hydrate).

## 7. What is NOT persisted

- **Wards** (`WardGate`) — ephemeral `TaskCompletionSource`s; nothing to resume after restart.
- **SSE subscriber state** / **live token streams** — connection-bound.
- **`_firstDispatchAfterUtc` startup jitter** — regenerated each process start.
- **A2A task mappings** — runtime index; Apprentices/Sessions/Entries already persist. In-flight external A2A tasks die with the process (same as other live streams).

## 7a. The Lexicon (raw-SQL, no EF migration)

The Lexicon is agent-directed entity memory, replacing the legacy key-value Lore MCP tools for model use. It follows the same raw-SQL-over-`ArcanumDbContext`-connection pattern as `SagaMemoryStore`, `SanctumBreachRepository`, `UnseenServantWatermarkStore`, and `BudgetAlertRepository` — deliberately **not** part of the compiled EF model, so it required no `dotnet ef dbcontext optimize` regeneration and no entry in `GrimoireSqlSchemaMigrator.MigrationOrder`.

- **Schema** is created idempotently by `LexiconSchemaInitializer.EnsureSchemaAsync`, invoked from `GrimoireDatabaseBootstrapper.EnsureInitializedAsync` right after `GrimoireSqlSchemaMigrator.ApplyPendingAsync` and alongside `WeaveSchemaInitializer`. Every statement uses `CREATE ... IF NOT EXISTS`. A failure is logged and swallowed — the Lexicon is optional agent memory and must never fail host startup; `LexiconService` degrades to empty matches / logged write failures when the tables are absent.

```sql
CREATE TABLE IF NOT EXISTS lexicon_entries (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    NameNormalized TEXT NOT NULL,
    Type TEXT NOT NULL,
    FactsJson TEXT NOT NULL,
    FactsText TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_lexicon_entries_NameNormalized ON lexicon_entries(NameNormalized);
CREATE VIRTUAL TABLE IF NOT EXISTS lexicon_fts USING fts5(
    Name, Type, FactsText, content='lexicon_entries', content_rowid='rowid'
);
```

- **Triggers** `lexicon_entries_ai` / `_ad` / `_au` sync the FTS5 external-content index on insert/delete/update using the FTS5 `'delete'` command for the old row. Triggers copy `Name`, `Type`, `FactsText` only — no JSON parsing happens inside SQLite. `FactsJson` is the durable array (serialized via the source-generated `LexiconJsonContext`); `FactsText` is a newline-joined plain-text projection for FTS tokenization.
- **Writes** (`scribe_lexicon`) upsert by `NameNormalized` (trim + invariant) under `BEGIN IMMEDIATE` inside `SqliteBusyRetry`, so concurrent appends cannot lose facts. Non-duplicate facts are appended; counts and lengths are capped by `LexiconLimits` (max name/type length, max facts per upsert, max fact length, max facts retained per entry). Type semantics: new + blank → `General`; existing + blank → keep; non-empty → refresh. `delete_lexicon` removes the row; its FTS row is removed by the `_ad` trigger.
- **Reads** (`MatchEntitiesAsync`) are tiered: exact `NameNormalized IN (...)` hits first (ordered `UpdatedAt DESC`), then for unresolved terms a column-weighted FTS5 query `WHERE lexicon_fts MATCH 'Term' ORDER BY bm25(lexicon_fts, 3.0, 2.0, 1.0) ASC` (3.0 Name, 2.0 Type, 1.0 FactsText — boosting lives in `bm25()`, not in the `MATCH` string; SQLite FTS5 does not support Lucene caret boosting). Results are deduplicated by `Id`, exact hits before FTS hits, and capped by `LexiconMaxMatchedEntries`. If the FTS query fails it degrades to a bounded `LIKE` fallback over `Name`/`FactsText`, or empty matches — never an exception.
- **No data to migrate:** the Lexicon is a net-new feature with no existing rows; `MageSettings` Lore is not migrated into it and remains intact as the operator key-value surface.

## 8. Existing patterns followed

- **`GrimoireSqlSchemaMigrator` transaction wrapping** — one script + its `__EFMigrationsHistory` row per `SqliteTransaction`, migrator-owned (scripts never `BEGIN`/`COMMIT` or insert their own history row)
- **`SqliteBusyRetry`** — wraps all watermark reads/writes; bounded exponential backoff on `SQLITE_BUSY`/`SQLITE_LOCKED`
- **Raw SQL via `DbParameter`** (not `SqliteParameter`) obtained through `cmd.CreateParameter()` — keeps the code provider-agnostic via `System.Data.Common`, matching `SessionRepository.ResolveFtsSessionIdsAsync` and `GrimoireRepository.SearchArchivesAsync`
- **Scoped `ArcanumDbContext` connection reuse** — `db.Database.GetDbConnection()`, opened if not already open, never disposed by the caller (EF Core owns the connection lifetime); this avoids opening a second connection to an already-encrypted SQLCipher database
- **AOT-safe serialization** — this table needs none (scalar columns only); future JSON-bearing tables must route through an existing or new `JsonSerializerContext`, never ad hoc reflection-based `System.Text.Json`

## 9. Migration safety and configuration impact

The `UnseenServantWatermarks` table is purely additive — a table with no foreign keys to existing tables, no column changes on existing tables, no data backfill. Arcanum has no production Grimoire databases in the wild (see `docs/Arcanum.README.md` "Database migrations" section), so it ships as part of the current `InitialCreate.sql` schema baseline via `GrimoireDatabaseBootstrapper` with zero risk to existing data. No data migration step is needed.

The table introduces no configuration elements and modifies no existing ones. `DaemonSettings`, `UnseenServantJob`, `WardSettings`, and all other `ArcanumSettings` sections are unchanged. The Compendium desktop application (`RetroDownfall.Compendium.Ux`, the `arcanum.json` editor) requires no updates. No base database data or seed data needs updating — the table starts empty and is populated at runtime as jobs complete.

## 10. Cost tracking and budget enforcement

Daily budget admission authority is **`BillableOperations` (completed UTC day) + outstanding `BudgetReservations`** (ADR 0002). `Sessions.TotalCostUsd` / `TotalTokensUsed` remain a **projection/cache** updated via `IncrementSessionTokensAndCostAsync` for UI convenience — not the spend authority. `BudgetMonitor.CheckAsync` prefers `IBudgetReservationService` and falls back to session-sum spend only when the reservation service is unavailable. Pricing, alert deduplication (`BudgetAlerts`), and `GET /api/budget` are documented in [Arcanum.DESIGN.md §22.2](Arcanum.DESIGN.md#222-cost-tracking-and-budget-enforcement-arcanumpricing-arcanumbudget).

`BillableOperations` is deliberately outside `ArcanumDbContext` and its compiled EF model. `TurnRunWriter` inserts/reads it through parameterized raw SQL; do not add a `DbSet`, create an EF migration, or regenerate the compiled model for reasoning accounting. Each completed provider call stores prompt, completion, cached, and reasoning counts plus the pricing snapshot before in-memory cost accumulation or response post-processing. Reasoning is a completion subset; cached input is a prompt subset. A present provider `TotalTokenCount` remains authoritative even when inconsistent (including zero); only a missing total is derived from prompt + completion. Missing usage is safe and contributes no ledger row/cost.

`ReasoningPer1M` is nullable. Null falls back to `OutputPer1M`; explicit zero remains a real zero price. Reservations treat a supplied reasoning budget as conservative output headroom (`max(output limit, reasoning budget)`), while reconciliation uses actual provider counts. If a durable operation insert fails, the run is marked failed and its reservation remains outstanding until expiry; Arcanum does not reconcile or release spend it could not prove.

**Mandatory developer action:** before running this version with a Grimoire created from the older install script, stop every Arcanum host/background daemon and back up anything needed. Delete the database and both SQLite sidecars, then restart Arcanum so the current embedded scripts install a fresh database. There is intentionally no incremental migration.

```bash
rm -f -- "$HOME/.config/arcanum/arcanum.db" "$HOME/.config/arcanum/arcanum.db-wal" "$HOME/.config/arcanum/arcanum.db-shm"
```

```powershell
Remove-Item -Force -ErrorAction SilentlyContinue `
  "$HOME\.config\arcanum\arcanum.db", `
  "$HOME\.config\arcanum\arcanum.db-wal", `
  "$HOME\.config\arcanum\arcanum.db-shm"
```

The `BudgetAlerts` table follows the same raw-SQL-via-`ArcanumDbContext`-connection pattern as `UnseenServantWatermarks` and `SanctumBreaches` — deliberately **not** part of the compiled EF model, so it required no `dotnet ef dbcontext optimize` regeneration. The `Sessions.TotalCostUsd` column, by contrast, is a change to an existing compiled-model entity, so the compiled model was regenerated with `dotnet ef dbcontext optimize` and the generated files under `src/RetroDownfall.Arcanum.Infrastructure/Generated/` were updated in place.

## 11. Prompt caching

Prompt caching is provider-managed for OpenAI-compatible endpoints and never becomes an Arcanum response cache. Nullable provider/model profiles may emit only the verified root `prompt_cache_key` / `prompt_cache_retention` contract; absent, `providerManaged`, `none`, and unsupported profiles leave the provider body unchanged. The former `Arcanum:Cache` options block remains rejected with its existing migration message.

Every completed provider call persists reported `CachedTokens` and `PricingSnapshotJson` (including `CachedPer1M`) before in-memory aggregation. Cached tokens are clamped to the prompt subset for cost, priced at `CachedPer1M`, and never added to total tokens. The session projection uses the accounting root's accumulated per-call cost when a durable run exists; budget reservations remain conservative and assume zero cache hits. These columns already exist in `20260721010000_AddInferenceAccountingAndIdempotencyClaims.sql`, so this feature adds no schema change or reinstall requirement. Per-call low-cardinality eligibility/hit/token/savings metrics are detailed in [Arcanum.DESIGN.md §22.3](Arcanum.DESIGN.md#223-prompt-caching-provider-managed-with-capability-driven-controls).

## 12. EF migration snapshot drift

The EF Core `InitialCreate` migration C# file and `ArcanumDbContextModelSnapshot.cs` are intentionally **stale** — `Sessions.TotalCostUsd` (precision 18, scale 8), `Entries.IsPinned`, `Session.ForkedFromSessionId` (with index), and several additive tables (`UnseenServantWatermarks`, `SanctumBreaches`, `IdempotencyKeys`, `UploadedFiles`, `Batches`, `BudgetAlerts`) were added via hand-authored SQL migrations (Section 3) and the compiled EF model (`src/RetroDownfall.Arcanum.Infrastructure/Generated/`), not via `dotnet ef migrations add`. **`SessionAttachments`** and the inference-accounting tables (`InferenceRuns`, `BillableOperations`, `BudgetReservations`, `CostAdjustments`) are hand-authored/raw-SQL surfaces and are **not** on the compiled EF model; `BillableOperations.ReasoningTokens` therefore changes neither the EF snapshot nor generated model. The `ArcanumDbContextModelSnapshot.cs` was hand-edited to include `Session.TotalCostUsd` and `Session.ForkedFromSessionId` (with `HasIndex`) so `dotnet ef migrations add` produces an empty migration rather than a spurious "add column" migration. The compiled model is canonical for EF-tracked entities; runtime raw-SQL schemas are applied by `GrimoireSqlSchemaMigrator`.

## 13. Session attachments (`attachments/` + `SessionAttachments`)

**Purpose:** durable text + Scrying image attachments for Command Center sessions (DESIGN.md §10.2.5). `arcanum chat` staging remains ephemeral in this pass.

### On-disk layout

Root: `ArcanumPaths.AttachmentsDirectory` → `~/.config/arcanum/attachments/` (next to `arcanum.db`).

```text
attachments/
  _pending/{turnId}/
    {logicalKey}/v1/{originalFileName}
  {sessionId:N}/
    {logicalKey}/
      v1/{originalFileName}
      v2/{originalFileName}
```

Owner-only permissions on `attachments/` and every session / `_pending` subtree. Logical keys and original filenames are sanitized (no path separators / `../` / control chars); built paths are revalidated to stay under `AttachmentsDirectory` before I/O.

### Table invariants (`SessionAttachments`)

Hand-authored migration `20260719180000_AddSessionAttachments` in `GrimoireSqlSchemaMigrator.MigrationOrder`. Access: raw SQL through scoped `ArcanumDbContext` + `SqliteBusyRetry` (`SessionAttachmentStore`) — **not** an EF `DbSet`.

| Column | Bound | Pending |
|--------|-------|---------|
| `Id` | set | set |
| `SessionId` | **NOT NULL** | **NULL** |
| `EntryId` | set when user entry known (nullable until bound) | **NULL** |
| `PendingTurnId` | **NULL** | **NOT NULL** |
| `State` | `'Bound'` | `'Pending'` |
| `LogicalKey`, `OriginalFileName`, `Version`, `RelativePath`, `ContentSha256`, `MimeType`, `ByteLength`, `Kind`, `CreatedAt` | as usual | as usual |

- `GET /api/sessions/{id}/attachments` returns **`State = Bound`** only.
- Pre-bind: `_pending/{turnId}/` + `State = Pending`. On `SessionBound` / first persisted user entry: **promote** copies bytes into the session tree, then updates rows to Bound (`SessionId` set, `PendingTurnId` null, new `RelativePath`) in a DB transaction — not an atomic filesystem move.
- Persist **before** model inference; failure fails the turn closed.
- **Fork:** pre-copies + hash-verifies attachment bytes into the fork session tree, then inserts `Session` / `Entries` / `SessionAttachments` in one EF ambient transaction (raw SQL enlisted). On DB failure, the partial fork tree is deleted.
- **Purge:** deletes `SessionAttachments` + `Session` / `Entries` in one transaction, then best-effort deletes `attachments/{sessionId}/` (not tied to request cancellation). FS failure is logged; reconcile recovers.
- **Entry hard-delete:** in the same DB transaction as the entry delete, sets matching `SessionAttachments.EntryId = NULL` (bytes and rows remain Bound to the session).

### Retention / GC / reconcile

`SessionAttachmentPendingGcHostedService` runs once at host startup via `ReconcileAsync`: stale **Pending** GC (rows + matching `_pending/{turnId}` dirs older than `Arcanum:Attachments:PendingRetentionHours`, default 24h, clamp 1–168), then:

| Sweep | Behavior |
|-------|----------|
| Missing session rows / dirs | Bound rows whose `SessionId` has no `Sessions` row → delete rows, then best-effort session dir; orphan session dirs with no live session → delete dir |
| Missing-file rows | Row whose file is gone (or `RelativePath` escapes root) → delete row + log |
| Unreferenced temp/final files | Orphan files under the attachments tree with no matching row → delete |
| Invalid `_pending` child names | Log warning and leave alone (no identity-checked delete) |

Soft caps `MaxBytesPerSession` / `MaxVersionsPerLogicalKey` reject new writes when exceeded (no background prune of bound files).

### Privacy / uninstall / copy

| Layer | Protection |
|-------|------------|
| Grimoire metadata | SQLCipher-encrypted |
| Attachment **bytes** | Owner-perm files only — **not** SQLCipher |
| OS encryption / backup | Operator responsibility |

Deleting or resetting only `arcanum.db` leaves orphan bytes under `attachments/`. For full conversation continuity (or a clean uninstall), copy or remove `~/.config/arcanum/attachments` together with the database. Distinct from `/v1/files` opaque `files/{guid}` storage.
