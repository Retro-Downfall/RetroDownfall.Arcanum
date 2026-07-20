# Session Attachments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist session attachments (text + Scrying images) on disk with Grimoire metadata so Command Center conversations can list, reveal, re-attach, and let the model re-attach via an internal MCP tool with explicit multimodal injection.

**Architecture:** Host-only `ISessionAttachmentStore` writes bytes under `~/.config/arcanum/attachments` and rows via raw SQL. Persist before model inference. CLI sends content/`AttachmentReferences`; lists via GET. Model tool is internal MCP (attunement) with post-tool `TextContent`/`DataContent` injection.

**Tech Stack:** .NET, SQLite/SQLCipher, ASP.NET Minimal APIs, Terminal.Gui Command Center, MEAI content types, internal MCP.

## Global Constraints

- Native AOT: source-gen JSON only; no new project-level AOT suppressions.
- OpenAI `/v1` untouched; `arcanum chat` untouched.
- No Spectre/`AnsiConsole`/`Console.WriteLine` while Command Center TUI is active.
- Schema: hand-authored SQL in `GrimoireSqlSchemaMigrator.MigrationOrder` — not EF compiled model.
- Access: raw SQL via scoped `ArcanumDbContext` + `SqliteBusyRetry`.
- Do **not** git commit unless the human explicitly asks (user rule overrides plan commit steps).
- Spec: `docs/superpowers/specs/2026-07-19-session-attachments-design.md`.

### File map

| Responsibility | Path |
|----------------|------|
| Paths | `src/RetroDownfall.Arcanum.Core/Storage/ArcanumPaths.cs` |
| Settings | `src/RetroDownfall.Arcanum.Core/Configuration/AttachmentsSettings.cs` (new) |
| Clamps | `src/RetroDownfall.Arcanum.Core/Configuration/ArcanumSettingClamps.cs` |
| Store interface + records | `src/RetroDownfall.Arcanum.Core/Storage/ISessionAttachmentStore.cs` (new) |
| Name sanitizer | `src/RetroDownfall.Arcanum.Core/Storage/SessionAttachmentPathSanitizer.cs` (new) |
| Migration SQL | `src/RetroDownfall.Arcanum.Infrastructure/Data/SqlMigrations/20260719180000_AddSessionAttachments.sql` |
| Migrator | `src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireSqlSchemaMigrator.cs` |
| Store impl | `src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs` (new) |
| GC hosted service | `src/RetroDownfall.Arcanum.Infrastructure/Hosting/SessionAttachmentPendingGc.cs` (new) |
| Wire DTO | `PingRequest` + `AttachmentReferenceDto` |
| JSON | `ArcanumJsonContext` (+ Forge if needed) |
| Persist hook | `WizardIntelligenceProvider.cs` |
| Prompt index | `SystemPromptBuilder.cs` |
| MCP tool | `ArcanumInternalToolServer` partials |
| Post-tool inject | `ToolExecutionPipeline` / `ProcessedToolCall` |
| API | `SessionEndpoints.cs` |
| CLI | Command Center parser/dispatcher/state/chat runner + Reveal helper |
| Descriptors | `SettingDescriptors.cs`, `ConfigSection.cs` |
| Docs | DESIGN / PERSISTENCE / README |

---

### Task 1: Config, paths, clamps, descriptors

**Files:**
- Create: `src/RetroDownfall.Arcanum.Core/Configuration/AttachmentsSettings.cs`
- Modify: `ArcanumSettings.cs`, `ArcanumSettingClamps.cs`, `ArcanumPaths.cs`, `ConfigurationValidator.cs` (if other sections validate similarly), `SettingDescriptors.cs`, `ConfigSection.cs`
- Test: `tests/.../Configuration/AttachmentsSettingsClampTests.cs`, `tests/.../Storage/ArcanumPathsTests.cs`

**Interfaces:**
- Produces: `AttachmentsSettings` with keys from spec; `ArcanumPaths.AttachmentsDirectory`; clamp methods listed below.

- [ ] **Step 1: Failing tests** for clamps + `AttachmentsDirectory == Path.Combine(GrimoireDirectory, "attachments")`.

- [ ] **Step 2: Implement**

```csharp
// AttachmentsSettings.cs
namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>Session attachment persistence. Bound from <c>Arcanum:Attachments</c>.</summary>
public sealed record AttachmentsSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxReferencesPerTurn { get; set; } = 8;
    public int MaxVersionsPerLogicalKey { get; set; } = 20;
    public long MaxBytesPerSession { get; set; } = 256L * 1024L * 1024L;
    public int PendingRetentionHours { get; set; } = 24;
    public int MaxIndexItemsInPrompt { get; set; } = 40;
    public int MaxIndexBytesInPrompt { get; set; } = 4_096;
    public bool EnableModelAttachTool { get; set; } = true;
}
```

Clamps (add to `ArcanumSettingClamps`):
- `AttachmentsMaxReferencesPerTurn`: 1–32
- `AttachmentsMaxVersionsPerLogicalKey`: 1–100
- `AttachmentsMaxBytesPerSession`: 1 MiB – 10 GiB
- `AttachmentsPendingRetentionHours`: 1–168
- `AttachmentsMaxIndexItemsInPrompt`: 1–200
- `AttachmentsMaxIndexBytesInPrompt`: 256–64_000

Add `Attachments` property on `ArcanumSettings`. Add `ConfigSection.Attachments` + descriptors for all keys. `ArcanumPaths.AttachmentsDirectory`.

- [ ] **Step 3: Tests pass.** Do not commit.

---

### Task 2: SQL migration

**Files:**
- Create: `.../SqlMigrations/20260719180000_AddSessionAttachments.sql`
- Modify: `GrimoireSqlSchemaMigrator.cs` — append to `MigrationOrder`
- Test: `GrimoireSqlSchemaMigratorTests` (extend or add assert table exists after apply)

```sql
CREATE TABLE IF NOT EXISTS "SessionAttachments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_SessionAttachments" PRIMARY KEY,
    "SessionId" TEXT NULL,
    "EntryId" TEXT NULL,
    "PendingTurnId" TEXT NULL,
    "State" TEXT NOT NULL,
    "LogicalKey" TEXT NOT NULL,
    "OriginalFileName" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    "RelativePath" TEXT NOT NULL,
    "ContentSha256" TEXT NOT NULL,
    "MimeType" TEXT NOT NULL,
    "ByteLength" INTEGER NOT NULL,
    "Kind" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Session_Logical_Version"
  ON "SessionAttachments" ("SessionId", "LogicalKey", "Version");
CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Session_CreatedAt"
  ON "SessionAttachments" ("SessionId", "CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_EntryId"
  ON "SessionAttachments" ("EntryId");
CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_PendingTurnId"
  ON "SessionAttachments" ("PendingTurnId");
CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_State"
  ON "SessionAttachments" ("State");
```

- [ ] **Step 1: Failing migrator test expecting SessionAttachments.**
- [ ] **Step 2: Add SQL + MigrationOrder entry.**
- [ ] **Step 3: Pass.** Do not commit.

---

### Task 3: Core store contract + path sanitizer

**Files:**
- Create: `ISessionAttachmentStore.cs`, `SessionAttachmentPathSanitizer.cs`, related records in Core
- Test: `SessionAttachmentPathSanitizerTests.cs`

**Produces:**

```csharp
public enum SessionAttachmentKind { Text, Image }
public enum SessionAttachmentState { Pending, Bound }

public sealed record SessionAttachmentRecord(
    Guid Id,
    Guid? SessionId,
    Guid? EntryId,
    string? PendingTurnId,
    SessionAttachmentState State,
    string LogicalKey,
    string OriginalFileName,
    int Version,
    string RelativePath,
    string ContentSha256,
    string MimeType,
    long ByteLength,
    SessionAttachmentKind Kind,
    DateTimeOffset CreatedAt);

public sealed record SessionAttachmentIndexItem(
    string LogicalKey,
    string OriginalFileName,
    IReadOnlyList<int> Versions,
    SessionAttachmentKind Kind,
    long LatestByteLength);

public interface ISessionAttachmentStore
{
    Task<SessionAttachmentRecord> PersistNewAsync(
        Guid? sessionId,
        string? pendingTurnId,
        Guid? entryId,
        string logicalNameHint,
        string originalFileName,
        ReadOnlyMemory<byte> bytes,
        string mimeType,
        SessionAttachmentKind kind,
        CancellationToken cancellationToken = default);

    Task PromotePendingAsync(string pendingTurnId, Guid sessionId, Guid? entryId, CancellationToken cancellationToken = default);

    Task<SessionAttachmentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SessionAttachmentRecord?> GetByLogicalAsync(Guid sessionId, string logicalKey, int? version, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(Guid sessionId, int maxItems, CancellationToken cancellationToken = default);

    Task<ReadOnlyMemory<byte>> ReadBytesAsync(SessionAttachmentRecord record, CancellationToken cancellationToken = default);

    Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);

    Task ValidateReferencesAsync(Guid sessionId, IReadOnlyList<Guid> attachmentIds, int maxReferences, CancellationToken cancellationToken = default);
}

public static class SessionAttachmentPathSanitizer
{
    public static bool TrySanitize(string? input, out string sanitized, out string error);
}
```

Sanitize: strip separators/`..`/control/leading dots; cap length (~120); reject empty/reserved.

- [ ] TDD sanitizer; define interface. Do not commit.

---

### Task 4: Store implementation + GC + DI

**Files:**
- Create: `SessionAttachmentStore.cs`, `SessionAttachmentPendingGcHostedService.cs` (or method called from bootstrap)
- Modify: `ServiceCollectionExtensions.cs`
- Test: `SessionAttachmentStoreTests.cs` (v1, dedupe, v2, traversal reject, caps, pending promote, GC)

Implement with keyed lock `ConcurrentDictionary<string, SemaphoreSlim>`, owner-only dirs via `SecureFilePermissions.EnsureOwnerOnlyDirectoryExists`, latest-hash dedupe, reject on version/byte caps.

Pending: `SessionId NULL`, `PendingTurnId NOT NULL`, `State=Pending`, path `_pending/{turnId}/...`.
Bound: `SessionId NOT NULL`, `PendingTurnId NULL`, `State=Bound`.

- [ ] TDD store behaviors. Do not commit.

---

### Task 5: Wire DTOs + JSON + host validation helpers

**Files:**
- Create: `AttachmentReferenceDto.cs` if needed (`Guid AttachmentId` or just `Guid[]`)
- Modify: `PingRequest.cs` add `List<Guid>? AttachmentReferences = null` (or DTO list)
- Modify: `ArcanumJsonContext.cs`, `TheForgeJsonContext.cs` if it lists PingRequest props
- Create: host validator used by Wizard (can live in Api or Infrastructure)

Rules: if `SessionId` null and refs non-empty → fail; enforce `MaxReferencesPerTurn`; each id must be Bound to that session.

- [ ] Unit tests for validation. Do not commit.

---

### Task 6: Persist-before-model + rehydrate + promote in WizardIntelligenceProvider

**Files:**
- Modify: `WizardIntelligenceProvider.cs`, possibly `InferenceContextBuilder.cs`, `GrimoireTurnWriter.cs`
- Test: focused Wizard/provider tests with fake store

Lifecycle after gates + after turn begin / pending id:
1. Persist new AttachedFiles/ScryingFoci bytes via store
2. Validate + load AttachmentReferences
3. On failure → `ResolveInterruptedAsync` + fail (no model call)
4. Rehydrate into message list (text + DataContent for images)
5. On SessionBound → `PromotePendingAsync`

Also inject index into `SystemPromptBuilder.Build`.

- [ ] Tests: persist failure aborts; success includes content; resume does not auto-inject. Do not commit.

---

### Task 7: System prompt Session Attachments Index

**Files:**
- Modify: `SystemPromptBuilder.cs`
- Test: `SystemPromptBuilder` / new `SessionAttachmentsIndexTests.cs`

Append `### Session Attachments Index` with hardened names; respect `MaxIndexItemsInPrompt` / `MaxIndexBytesInPrompt`.

- [ ] TDD. Do not commit.

---

### Task 8: MCP `attach_session_file` + post-tool injection

**Files:**
- Modify: `ArcanumInternalToolServer` Schemas/Registry + new partial
- Modify: `ProcessedToolCall`, `ToolExecutionPipeline`, `WizardIntelligenceProvider` tool loop
- Test: internal tool tests + injection tests

Advertise when Enabled && EnableModelAttachTool && session exists.
On success: tool result text acknowledges; **also** queue `AIContent` (`TextContent` / `DataContent`) appended to next inference messages after the function result.
Missing → tool-result error listing names.

- [ ] TDD. Do not commit.

---

### Task 9: GET `/api/sessions/{id}/attachments` + CLI client

**Files:**
- Modify: `SessionEndpoints.cs`, DTO mapping, `ArcanumApiClient.cs`, JSON context
- Test: endpoint tests

Return Bound rows only with RelativePath, ids, versions, kind, etc.

- [ ] TDD. Do not commit.

---

### Task 10: Command Center `/attachments` + Reveal + AttachmentReferences

**Files:**
- Modify: `ShellCommandParser.cs`, `ShellCommandDispatcher.cs`, `CommandCenterState.cs`, `CommandCenterChatRunner.cs`, help text
- Create: `SessionAttachmentReveal.cs` (OS reveal)
- Test: parser/dispatcher tests

Commands locked in spec. Stage refs in state; ChatRunner puts them on `PingRequest.AttachmentReferences`.

- [ ] TDD. Do not commit.

---

### Task 11: Docs

**Files:**
- Modify: `docs/Arcanum.DESIGN.md` (§10.2.4, §16.6, Attachments + privacy)
- Modify: PERSISTENCE + README as required by repo conventions

- [ ] Doc updates matching locked privacy + commands + config. Do not commit.

---

## Self-review

- Spec coverage: ownership, layout, schema, lifecycle, validation, wire, index, MCP+injection, commands, config, privacy, reveal, tests — each has a task.
- No commits (user rule).
- Types consistent: `SessionAttachmentRecord`, `ISessionAttachmentStore`, `Arcanum:Attachments`.
