# Session attachments (disk + Grimoire pointers) — design

Supplement to Command Center / chat continuity. Locked product decisions (2026-07-19), including API-first and final pre-implementation addenda.

## Goals

- **Full conversations for review and continue:** attachments (text + Scrying images) survive beyond a single inference turn.
- **Bytes on disk, pointers in Grimoire** — not SQLCipher blobs; reverses “Scrying foci are ephemeral / never persisted.”
- **User knows where data lives:** under `~/.config/arcanum/` (next to `arcanum.db`).
- **Finder (or OS equivalent) review** via Reveal — original filenames preserved under version folders.
- **Re-attach into context** by user or model (model **auto-add**, no confirm — option A), with an **explicit** multimodal injection path.
- **API-first, single-writer Grimoire:** serve host owns all attachment persistence.

## Non-goals (deferred)

- Cloud sync / backup / migrate-install across machines.
- Human browsing of the attachments tree beyond Reveal.
- Auto-injecting *all* historical attachments into every resumed turn.
- Interactive Spectre file browser inside Command Center.
- Changing `/v1/files` opaque `files/{guid}` store.
- **`arcanum chat` changes** — Command Center + host only in this pass.
- Further retention productization beyond soft clamps below.

## Hard constraints

- Native AOT: source-gen JSON for any new DTO; no new project-level AOT suppressions.
- OpenAI `/v1` untouched.
- No Spectre / `AnsiConsole` / `Console.WriteLine` while Command Center TUI is active.
- `arcanum chat` untouched.
- Single-writer Grimoire: CLI does not open the DB for attachment rows/bytes.

---

## 1. Process ownership (API-first)

| Concern | Owner |
|---------|--------|
| Bytes + `SessionAttachments` rows | **Serve host** via host-side `ISessionAttachmentStore` |
| New file staging | CLI may validate locally for UX; sends content in `PingRequest.AttachedFiles` / `ScryingFoci`. **Host re-validates and persists before model call** (see §Lifecycle). |
| Re-attach existing | CLI sends `PingRequest.AttachmentReferences` (attachment ids). Host rehydrates from store; CLI does **not** re-read attachment disk. DTOs on source-gen JSON context. |
| Listing | `GET /api/sessions/{id}/attachments` → `ApiResponse<T>` of **bound** rows only (incl. `RelativePath`). |
| Reveal | CLI: `Path.Combine(AttachmentsDirectory, relativePath)` only after API row; never arbitrary strings. |
| Model re-attach | Internal **MCP** tool (not hub-native built-in); in-process; explicit post-tool injection (§Model tool). |

`ISessionAttachmentStore` is **not** shared into the CLI process.

---

## 2. On-disk layout

Root: `ArcanumPaths.AttachmentsDirectory` → `{GrimoireDirectory}/attachments/`.

```text
attachments/
  _pending/{turnId}/
    {logicalKey}/v1/{originalFileName}
  {sessionId:N}/
    {logicalKey}/
      v1/{originalFileName}
      v2/{originalFileName}
```

- Dedupe against **latest version hash only** (same hash → reuse id; no new `vN`).
- Owner-only perms on `attachments/` and every session / `_pending` subtree.

### Path traversal

Sanitize **both** `logicalKey` and `originalFileName`: strip separators/`../`/control chars/leading dots; cap length; reject empty/reserved/unsafe names. Before any I/O, revalidate the built path stays under `AttachmentsDirectory` (handle-identity discipline).

### Storage privacy (document in DESIGN / PERSISTENCE / README)

| Layer | Protection |
|-------|------------|
| Grimoire metadata (`SessionAttachments`) | SQLCipher-encrypted (same as other Grimoire tables) |
| Attachment **bytes** on disk | Owner-permission-protected files under `~/.config/arcanum/attachments` — **not** SQLCipher-encrypted |
| OS disk encryption / backup | Operator responsibility |
| Full conversation continuity | Requires copying/restoring `~/.config/arcanum/attachments` together with the DB |

---

## 3. Schema (`SessionAttachments`)

**Hand-authored SQL migration** registered in `GrimoireSqlSchemaMigrator.MigrationOrder` — **not** EF compiled model. Access: raw SQL through scoped `ArcanumDbContext` connection + `SqliteBusyRetry`.

### Columns (locked invariants)

| Column | Bound | Pending |
|--------|-------|---------|
| `Id` | set | set |
| `SessionId` | **NOT NULL** | **NULL** |
| `EntryId` | set when user entry known (nullable until bound to entry) | **NULL** |
| `PendingTurnId` | **NULL** | **NOT NULL** |
| `State` | `'Bound'` | `'Pending'` |
| `LogicalKey`, `OriginalFileName`, `Version`, `RelativePath`, `ContentSha256`, `MimeType`, `ByteLength`, `Kind`, `CreatedAt` | as usual | as usual |

- `GET /api/sessions/{id}/attachments` returns **`State = Bound`** rows only.
- Startup GC: delete stale **pending rows** and matching `_pending/{turnId}` directories **together** when older than `PendingRetentionHours`.

Indexes: `(SessionId, LogicalKey, Version)`, `(SessionId, CreatedAt)`, `(EntryId)`, `(PendingTurnId)`, `(State)`.

### Version concurrency

Keyed write lock per `(sessionId, logicalKey)` (for bound) / pending turn key as needed + `SqliteBusyRetry`. Latest-hash dedupe under that lock.

---

## 4. Lifecycle — persist **before** model call

1. Pre-flight gates pass (budget, Scrying shape, etc.).
2. Grimoire turn/session or **pending turn id** exists.
3. Host copies bytes + insert/update attachment rows (new content and/or validate references).
4. If persistence fails → resolve turn **interrupted** and **fail before model inference** (model must never see an attachment that did not persist).
5. Then call the model (with rehydrated content already in the message list / ready for injection).

New uploads and successful reference resolution both participate in this pre-model persist/rehydrate step.

---

## 5. Host-side validation (mandatory)

Do **not** rely on CLI validation. Host enforces:

- File/image size, count, MIME bounds (Cli + Scrying clamps as applicable).
- Safe `logicalKey` / `originalFileName`.
- Per-session byte and version caps (`MaxBytesPerSession`, `MaxVersionsPerLogicalKey`).
- Max `AttachmentReferences` per turn (`MaxReferencesPerTurn`).
- Each referenced attachment **belongs to the request `SessionId`**.
- If `SessionId` is null → **`AttachmentReferences` are invalid** (reject).

---

## 6. Wire / API

### PingRequest (additive)

- `AttachedFiles`, `ScryingFoci` — new content (CLI → host).
- `AttachmentReferences` — ids for re-attach; host loads from store.
- Source-gen JSON registration required.

### HTTP

- `GET /api/sessions/{id}/attachments` → bound rows only (`ApiResponse<…>`).

### Promotion

Pre-bind: `_pending/{turnId}/` + `State = Pending`.  
On `SessionBound` / first persisted user entry: atomic move + row update (`State = Bound`, `SessionId` set, `PendingTurnId` null) in one transaction.

---

## 7. Session Attachments Index (system prompt)

Inject **metadata only** (current session, bound attachments) into the system prompt:

```text
### Session Attachments Index
- notes.txt  versions=1,2  kind=Text  bytes=…
- shot.png   versions=1    kind=Image bytes=…
```

- Bounded by `MaxIndexItemsInPrompt` and `MaxIndexBytesInPrompt` (clamped).
- Harden filenames so they cannot create headings or instruction-like lines (escape/neutralize `#`, newlines, etc.).
- **No bytes** in the index — model uses the MCP tool (or user `/attachments add`) to pull content.

---

## 8. Model re-attach tool (`attach_session_file`)

### Registration

- **Internal MCP tool**, not a hub-native built-in (hub-natives skip Artifact Attunement; this tool **must** respect attunement).
- Hand-authored `JsonDocument` schema via `McpJsonSerializerContext` — **no** `AIFunctionFactory.Create`.
- Advertise only when: `Arcanum:Attachments:Enabled`, `EnableModelAttachTool`, and a **current session** exists.
- Sanctum can disable by tool name; no arbitrary path/network args.
- Current-session attachments only (never cross-session).

### Explicit injection path (locked — not implicit)

`attach_session_file` must **not** merely return image/text as a normal tool string and claim the model “saw” it.

After a **successful** tool call, a dedicated **post-tool path** (extend processed tool-call result with additional context content, **or** a hook in `WizardIntelligenceProvider` / `ToolExecutionPipeline`) appends resolved content into the **next inference round**:

| Kind | Injection |
|------|-----------|
| Text | Bounded `TextContent` / message text |
| Image | `DataContent` so vision-capable models receive the image |

Document and test this path explicitly. “Auto-add into current turn” means this injection, not a textual tool result alone.

### Errors

Missing name/version → **tool-result error** listing available logical names — not an exception.

---

## 9. Command Center commands (no `/attach` ambiguity)

| Command | Meaning |
|---------|---------|
| `@path` | Stage a **local** file (content on next/current turn). |
| `/attach <path>` | Stage a **local** file only. |
| `/attachments` | List session attachments (API). |
| `/attachments add <logicalName> [vN]` | Stage a **previous session attachment** as `AttachmentReferences`. |
| `/attachments reveal <logicalName> [vN]` | Reveal via API row → absolute store path. |

Do **not** overload `/attach foo.txt` to mean either local path or session logical name.

---

## 10. Config (`Arcanum:Attachments`)

| Key | Role |
|-----|------|
| `Enabled` | Master switch |
| `MaxReferencesPerTurn` | Cap on `AttachmentReferences` |
| `MaxVersionsPerLogicalKey` | Soft version cap (reject when exceeded) |
| `MaxBytesPerSession` | Soft byte budget (reject when exceeded) |
| `PendingRetentionHours` | GC age for `_pending` + pending rows |
| `MaxIndexItemsInPrompt` | Index item cap |
| `MaxIndexBytesInPrompt` | Index byte cap |
| `EnableModelAttachTool` | Advertise/run MCP tool |

Add clamps for all numeric settings. Update DESIGN / README / **Compendium descriptors** if this repo requires descriptor coverage for new settings.

---

## 11. Reveal (CLI, interactive only)

| Platform | Mechanism |
|----------|-----------|
| macOS | `open -R` |
| Windows | `explorer /select,` |
| Linux | `xdg-open` on parent dir |

Interactive local CLI only; never headless/unattended; `ProcessStartInfo` without string-interpolated shell; no Spectre/Console while TUI active.

---

## 12. Documentation updates (required)

- **DESIGN.md** §10.2.4 (persist Scrying foci via attachment store), §16.6, new Attachments section + privacy note.
- **PERSISTENCE.md** — `attachments/` artifact, `SessionAttachments` + Pending/Bound invariants, retention/GC, uninstall/copy note, privacy (bytes not SQLCipher).
- **README** — API map, Command Center command table, MCP tool, config keys.

---

## 13. Testing (acceptance)

- Staging → v1 + row; turn includes content/foci; **persist-before-model** (failure aborts before inference).
- Identical bytes → no v2; same id re-staged.
- Changed bytes → v2; both Reveal correctly.
- Resume lists bound attachments; does **not** auto-inject.
- Model tool: successful call → **DataContent/TextContent** on next round (not text-only fake); current session only; attunement/Sanctum honored.
- Index appears in system prompt within caps; hostile filenames neutralized.
- `SessionBound` promotes pending (atomic); GC removes stale pending row+dir together.
- Path-traversal names rejected; host rejects bad refs / null SessionId + refs.
- Concurrent same-name attach → single coherent next version.
- Command parser: `/attach` local-only; `/attachments add|reveal` for session refs.

---

## Locked decisions summary

| Topic | Decision |
|-------|----------|
| Ownership | Host-only store |
| Persist timing | Before model call; fail closed |
| Host validation | Mandatory (CLI UX-only) |
| Pending invariants | SessionId/PendingTurnId/State as above |
| Schema | Hand-authored SQL in MigrationOrder; raw SQL access |
| Model tool | Internal MCP; attunement; explicit post-tool DataContent/TextContent injection |
| Index | Metadata-only, bounded, hardened names |
| Commands | Disambiguated `/attach` vs `/attachments add\|reveal` |
| Config | `Arcanum:Attachments:*` keys above |
| Privacy | Metadata encrypted; bytes owner-perm only |
| chat / `/v1` | Untouched |
