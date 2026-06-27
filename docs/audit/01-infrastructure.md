# 01 — Infrastructure (`RetroDownfall.Arcanum.Infrastructure`)

**Scope:** the OS-adjacent services — the encrypted Grimoire (EF Core 10 + SQLCipher), the MCP client layer, the `llama-server` manager + GGUF cache, background hosted services, Comm Link, security services, caching, logging, workspace/spell scanning. 166 files, ~34.3k lines — the largest and most operationally risky project.

This report is in two parts matching the two audit passes:
- **Part A — persistence, security, caching, workspaces** (L1a, below)
- **Part B — MCP, llama-server, background services, Comm Link** (L1b)

**Method:** parallel read-only deep-read passes; highest-severity findings re-verified against the exact source lines. Severities are calibrated to Arcanum's **single-user, loopback-by-default** posture (so several locally-exploitable-only races are P2/P3 rather than P0/P1).

---

# Part A — Persistence, security, caching, workspaces (L1a)

Pass agents: [Grimoire/EF](a0c8c661-c044-4798-b64e-e71425d2421e) · [Security](bc157430-b0d0-47af-b88d-a966f19ec930) · [Caching/Logging/Spells](be1ab246-743b-4f19-9ec7-48f10eda23f0).

## P1 findings

### [P1][performance] Hot-path session hydration loads the entire entry set, then trims in memory
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:398-404` (also `:433-437`, `:460-464`)
- **Observation:** `GetSessionAsync` does `_db.Entries.AsNoTracking().Where(m => m.SessionId == id).ToListAsync()` and only afterwards `SelectRecentEntries(entries, maxMessages)`. The correct pattern exists elsewhere — `SessionRepository.GetEntriesAscendingAsync:322-327` uses `OrderByDescending(CreatedAt).Take(clampedTake)` server-side.
- **Impact:** `GetSessionAsync` is on the per-turn inference path; it pulls **every** entry of a session (up to `MaxEntriesPerSession`) into memory on every turn, scaling RAM/I/O with total thread length instead of the requested window. A secondary correctness risk: read-time compression later filters `session.Entries` by the summary watermark, so if more than `MaxMessagesPerConversationLoad` entries exist after the last summary, middle (un-summarized) messages can be silently dropped from context (interaction with `WizardIntelligenceProvider` — confirm in 02-api.md).
- **Recommendation:** Push `Where(CreatedAt > watermark)` + `OrderByDescending` + `Take` into SQL; clamp the take.

### [P1][correctness] `SessionRepository.AddEntryAsync` never maintains `UnsummarizedEntryCount`
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs:273-316` vs `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:863-879`
- **Observation:** The Forge API entry-append path adds the entry and saves but never calls `IncrementUnsummarizedEntryCountIfKnownAsync`; the inference path (`GrimoireRepository`) does maintain the counter.
- **Impact:** Sessions mutated through `POST /api/sessions/{id}/entries` drift their `UnsummarizedEntryCount`, so `GetSessionsNeedingSummarizationAsync` and the Loremaster sweep mis-prioritize or skip them — read-time compression can then trust a summary/watermark that lags reality. This is a real correctness divergence between the two write paths.
- **Recommendation:** Maintain the counter in `AddEntryAsync` (and delete/archive paths), or consolidate all entry writes behind `GrimoireRepository`.

### [P1][reliability] `SpellScanner` directory BFS has no visited-set / depth / step cap (symlink-cycle hang)
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/SpellScanner.cs:393-456`
- **Observation:** `ScanTreeAsync` walks with a `Queue<string>` and enqueues every subdirectory with **no** visited set, depth bound, or step budget. Containment is the lexical `IsPathUnderWorkspaceRoot` prefix check (`:556-568`); `Path.GetFullPath` does not resolve symlinks, so a directory-symlink cycle under the root keeps producing valid in-root paths. The sibling scanners `PhysicalFileSystemBrowser` and `EyeOfTheWorldService` *do* bound this.
- **Impact:** A symlink cycle inside a scanned workspace/campaign root makes the scan grow paths until OS path-length limits throw (caught and `continue`d), effectively hanging or hugely slowing every spell-related API call (which re-scan on each request — see next finding). Severe reliability hit; it would be P0 if spell roots were untrusted, but they are operator-allowlisted, so P1.
- **Recommendation:** Track canonical visited directories (resolve symlinks), add a depth/step cap, and reject targets that escape the root (reuse `ToolHelpers`).

### [P1][security] `SpellScanner` opens spell files without handle/symlink revalidation
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/SpellScanner.cs:419-424,694`
- **Observation:** Containment is prefix-only; the enumerated `filePath` is read directly via `File.ReadAllTextAsync`. Contrast `PhysicalFileSystemBrowser.ReadAsync:184-187`, which calls `ToolHelpers.RevalidatePathBeforeIo` before I/O.
- **Impact:** A `SPELL.md` (or `SKILL.json`/script) that is a symlink whose lexical path is under the root, but whose target is outside, is read anyway — a path-containment escape for spell content that gets injected into prompts.
- **Recommendation:** Revalidate with handle-based identity (`ToolHelpers.RevalidatePathBeforeIo`) immediately before each open.

### [P1][performance] Spell read/search re-scan and fully re-parse the workspace on every request
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellRepository.cs:54-56,85,113,243,324`; `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellSearchService.cs:58-93`
- **Observation:** `ListAsync`/`GetAsync`/create-update-delete pre-checks each call `SpellScanner.ScanAsync` (full tree walk + `File.ReadAllTextAsync` per `SPELL.md`). Search runs a full scan for builtin + workspace + **each** campaign (N+1 full parses), with no shared cache.
- **Impact:** Latency and disk I/O scale with repo size × request rate; concurrent spell/search traffic re-walks and re-reads the whole tree repeatedly.
- **Recommendation:** Use `ScanMetadataAsync` + lazy `LoadFullAsync` for single-spell paths; share a TTL/mtime-keyed scan cache across repository and search.

### [P1][reliability] Spell update writes `SPELL.md` non-atomically
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellRepository.cs:287`
- **Observation:** Update is an in-place `File.WriteAllTextAsync(workspaceSpell.FilePath, content, ct)`. Create/import use staging + `Directory.Move`, and `ConfigurationWriter` uses temp+rename.
- **Impact:** A crash/power loss mid-write truncates/corrupts the spell.
- **Recommendation:** Temp-file + flush + atomic replace, matching `ConfigurationWriter`.

### [P1][correctness] Scan-time SKILL.json bounds ignore configured `Spells:*` settings
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/SpellScanner.cs:759-763`
- **Observation:** `SkillJsonBoundsValidator.Validate(skillMetadata, ArcanumSettingClamps.MaxDependencies(new SpellSettings().MaxDependencies), ArcanumSettingClamps.MaxDeclaredTools(new SpellSettings().MaxDeclaredTools))` — hardcoded defaults (20/50), not the operator's configured values (which `SpellRepository` create/update *do* honor at `:139-141,266-268`).
- **Impact:** Lowering `Spells:MaxDependencies`/`MaxDeclaredTools` does not constrain spells loaded from disk during scans/Arcane Resonance — inconsistent enforcement.
- **Recommendation:** Thread the live `ArcanumSettings`/clamps into `SpellScanner`.

### [P1][performance] `PhysicalWorkspaceScanner` recurses the whole tree with no bound
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Workspaces/PhysicalWorkspaceScanner.cs:18-33`
- **Observation:** `EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }` over `*.sln`/project globs with no step/depth cap (only token checks inside the loop).
- **Impact:** Large trees cause long CPU/I/O stalls building the project summary.
- **Recommendation:** Add max steps/depth consistent with `EyeOfTheWorldService`.

## P2 findings

### [P2][concurrency] Two session write paths, only one takes the write lock + busy-retry
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs:273,313` (no `SessionWriteLock`, no `SqliteBusyRetry`) vs `GrimoireRepository.cs:56,246` (both)
- **Observation:** `GrimoireRepository` acquires `SessionWriteLock` and wraps writes in `SqliteBusyRetry.ExecuteAsync`; `SessionRepository` mutators call `SaveChangesAsync`/`ExecuteUpdateAsync` directly.
- **Impact:** Concurrent inference writes and Forge-API entry writes to the same session are not serialized process-wide, and the Forge path fails fast on `SQLITE_BUSY` instead of retrying. SQLite's own locking prevents corruption, but multi-step assistant flows and counter updates can interleave.
- **Recommendation:** Route all session-scoped writes through one path, acquire `SessionWriteLock`, and use `SqliteBusyRetry` everywhere.

### [P2][concurrency] Several Grimoire mutators skip `SessionWriteLock`
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:152-174` (`FinalizeAssistantEntryAsync`), `:176-234` (`DiscardAssistantEntryAsync`), `:358-378` (`PurgeSessionAsync`)
- **Impact:** Races with locked write paths on the same session (partial deletes, counter drift, `SQLITE_BUSY`).
- **Recommendation:** Acquire the per-session lock for all session-scoped mutations.

### [P2][reliability] `SearchArchivesAsync` issues a raw command without ensuring the connection is open
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:606-636` vs `SessionRepository.ResolveFtsSessionIdsAsync:520-525`
- **Impact:** Intermittent "connection must be valid and open" failures on cold/pooled contexts.
- **Recommendation:** Open the connection first (as the sibling FTS path does).

### [P2][reliability] No production WAL checkpoint
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseBootstrapper.cs:93-101`
- **Impact:** `-wal`/`-shm` sidecars persist across restarts; copying a live DB without checkpoint risks an inconsistent backup.
- **Recommendation:** `PRAGMA wal_checkpoint(TRUNCATE)` on graceful shutdown.

### [P2][performance] Analytics + paged-list counts run many full scans / per-entry rows
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs:167-176` (entry-count query returns one row per entry, counted in memory), `:199-222` (`GetAnalyticsAsync` ~8 scans), `:128-139` (correlated `Any`/`Like` on un-indexed `ModelUsed`)
- **Impact:** Cost scales with table growth for list/analytics endpoints.
- **Recommendation:** `GROUP BY … COUNT(*)` in SQL; consolidate analytics into one or two aggregate queries; add a `(SessionId, ModelUsed)` index if model search is hot.

### [P2][performance] `GetRecentSessionEntriesAsync` has no upper clamp and loads all entries
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs:458-468`
- **Impact:** A large `takeLast` loads the whole session; no `Math.Clamp` upper bound unlike `SessionRepository.GetEntriesAsync`.
- **Recommendation:** Clamp and push `Take` to SQL.

### [P2][resource-safety] Per-session / per-workspace `SemaphoreSlim` maps never evict
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionWriteLock.cs:8,15`; `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellRepository.cs:982-995`
- **Impact:** `ConcurrentDictionary<…, SemaphoreSlim>` grows unbounded for long-lived processes touching many session/workspace keys.
- **Recommendation:** Evict on zero in-flight (carefully) or use a bounded cache.

### [P2][security] Secret temp file gets default permissions before the post-move chmod
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Security/DataProtectionSecretStore.cs:175-191`
- **Observation:** Ciphertext is written to `tempPath` (`FileMode.Create`), then `SecureFilePermissions.ApplyOwnerOnlyFile` runs only after `File.Move`.
- **Impact:** On Unix the `.tmp.*` file can briefly be group/world-readable (umask) before the move+chmod — a window where DP-encrypted secrets are exposed on multi-user hosts.
- **Recommendation:** `SetUnixFileMode` on the temp file before writing (or write+rename with the mode applied atomically).

### [P2][security] File/dir create-then-chmod race in `SecureFilePermissions`
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Security/SecureFilePermissions.cs:27-29,33-48`
- **Impact:** Brief window where newly-created sensitive paths inherit permissive default mode/ACL before restriction.
- **Recommendation:** Create with restrictive mode where the OS API allows; prefer private-temp + atomic rename.

### [P2][security] Startup permission self-check skips the secret files it is meant to protect
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Security/SecureFilePermissions.cs:167-191`
- **Observation:** `RunStartupPermissionSelfCheck` checks Grimoire paths but not `security.dat`/`grimoire-key.dat`; `ApplyOwnerOnlyToSensitivePaths` includes `security.dat` but not `grimoire-key.dat`.
- **Impact:** Loosened permissions on the master-key/secret files go undetected and unfixed at startup, giving `doctor`/startup checks false confidence.
- **Recommendation:** Add both secret paths to the self-check and the hardening pass.

### [P2][security] `SecureFilePermissions` fails open silently on chmod/ACL errors
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Security/SecureFilePermissions.cs:371-376,413-417,457-461`
- **Observation:** `catch (Exception) { // Best effort }` with no logging.
- **Impact:** Permission hardening can silently fail, leaving secrets group/world-readable with no operator signal.
- **Recommendation:** Log at warning; consider fail-closed for secret writes.

### [P2][resource-safety] Ward `JsonDocument` arguments are never disposed (native memory)
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Security/WardGate.cs:51-58,93-111,197-204`
- **Observation:** `WardEntry.Arguments` holds a `JsonDocument?`; resolve/timeout/cancel remove the entry but never `Dispose()` it.
- **Impact:** Native (pooled) memory leak under sustained ward submission with JSON arguments.
- **Recommendation:** Dispose `Arguments` on every terminal path.

### [P2][concurrency] `WardGate` `MaxActiveWards` check-then-add is racy
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Security/WardGate.cs:44-63`
- **Observation:** `if (_pending.Count >= maxActiveWards) return …;` then later `_pending.TryAdd(wardId, entry)` — non-atomic.
- **Impact:** Concurrent ward submissions can overshoot the cap (a soft safety limit). Low impact at single-user scale, hence P2.
- **Recommendation:** Reserve the slot under a lock / interlocked counter.

### [P2][resource-safety] `SanctumBreachStore` per-campaign buffers never evict their keys
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Security/SanctumBreachStore.cs:11-17`
- **Impact:** Unbounded growth of the campaign-keyed dictionary over long uptime (each buffer is ring-capped, but the key set is not).
- **Recommendation:** TTL/LRU-evict campaign keys.

### [P2][concurrency] `ApiKeyDigestCache` publishes digest + expiry via separate unsynchronized writes
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Security/ApiKeyDigestCache.cs:21-53`
- **Observation:** `StoreDigest` does two independent `Volatile.Write`s (expiry, then digest) with no lock.
- **Impact:** A concurrent reader can observe a new expiry with a stale digest (torn snapshot), weakening TTL guarantees during auth + rotation overlap. Low exploitability.
- **Recommendation:** Publish an immutable `(digest, expiry)` snapshot atomically (single reference swap) or guard with a lock.

### [P2][performance] Several scans/queries are unbounded or un-cached
- **Locations:** `SpellScanner.cs:254-259` (arsenal bypasses the metadata TTL cache — passes default `ttl=0`); `SpellScanner.cs:386-468` (no max-spells/step bound, scans whole workspace not just `spells/`); `SpellScanner.cs:237-249,170-200` (cache stampede: check-then-load with no single-flight); `Workspaces/CodexReader.cs:12,69-78` (unbounded `ConcurrentDictionary` codex cache); `Logging/LogQueryService.cs:15-30` (sorts the whole ring snapshot — up to 100k — per query); `Pattern/EyeOfTheWorldService.cs:75-121` (retains a `FileRec` per enumerated file up to `MaxEnumerationSteps`).
- **Impact:** Repeated parse/disk work, large transient allocations, and O(n log n) per log query.
- **Recommendation:** Pass the configured TTL from callers; add single-flight (`GetOrAdd` lazy task or per-key semaphore); bound the codex cache (`BoundedLruCache`); make the log buffer query incremental; cap `AllFiles` growth.

### [P2][reliability] Spell export reads scripts and SKILL.json without size bounds
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellRepository.cs:477-479,492-496`
- **Impact:** Oversized files are loaded fully into memory on export despite scan-time caps elsewhere.
- **Recommendation:** Enforce per-file and aggregate byte caps before reading.

## P3 findings

- **[P3][security] PRAGMA rekey via string interpolation** — `Hosting/GrimoireDatabaseBootstrapper.cs:213`. Passphrase is base64-derived (low injection risk today) but interpolated into SQL text; centralize `EscapeSqlString` / parameterize if the provider allows.
- **[P3][security] API-key rotation invalidates the digest cache after the disk write** — `Security/DataProtectionSecretStore.cs:57-59`. The old key can authenticate only in the sub-millisecond window between write and `Invalidate()` (the cache is re-read on the next request); negligible under single-user loopback, but invalidating within the lock around the write is cleaner.
- **[P3][security] `ApiKeyDigestCache` returns the cached `byte[]` by reference** — `Security/ApiKeyDigestCache.cs:33-35`; expose `ReadOnlyMemory<byte>` to prevent future mutation.
- **[P3][performance] `OutboundUrlGuard` resolves DNS twice per request** (pre-flight + connect callback) — `Security/OutboundUrlGuard.cs:251-254,286-289`; intentional defense-in-depth, but the validated addresses could be threaded through `HttpRequestOptions` to avoid the second lookup.
- **[P3][reliability] Log ring-buffer capacity is fixed at construction** — `Logging/InMemoryLogRingBuffer.cs:29-37`; `Logs:RingBufferCapacity` reloads have no effect until restart (subscribe to `OnChange` or document as startup-only).
- **[P3][maintainability] Dead `StripAddColumnStatements`** — `Data/GrimoireSqlSchemaMigrator.cs:376-402` (unused; `GuardExistingAddColumns` is the live path).
- **[P3][docs/style] House-style blank-line drift** — e.g. `Security/GrimoireDbPassphraseSource.cs`, `Repositories/CampaignRepository.cs:38-44`, `Pattern/EyeOfTheWorldService.cs:226-244`.

## Verified strengths (L1a)

- **DNS-rebind SSRF defense is real:** `OutboundUrlGuard.CreateUntrustedEgressHandler` disables auto-redirect and uses a `ConnectCallback` that re-resolves, re-checks the blocklist, and dials the validated `IPEndPoint` directly via `Socket.ConnectAsync` (`OutboundUrlGuard.cs:265-313`). It is fail-closed on mixed public/private DNS answers, normalizes IPv4-mapped IPv6, and blocks loopback/private/link-local/CGNAT/ULA. `TheReliquary` re-validates every redirect hop. (Gap: IPv4 broadcast / multicast are not in the blocklist — low practical HTTP-SSRF risk.)
- **Grimoire fundamentals are sound:** scoped/pooled `DbContext` (pool size 32) — no context shared across concurrent requests; FTS `MATCH` is parameterized and sanitized (`FtsMatchQuerySanitizer`); `LIKE` is escaped; SQLCipher uses a dedicated secret + PBKDF2 sidecar with `FailFast` on open failure and no passphrase logging; migrations are per-migration-transactional and idempotent; reads use `AsNoTracking`; cancellation tokens flow through.
- **Summary rollup never deletes entries** (`UpdateSessionCampaignRollupAsync` only `ExecuteUpdate`s session columns); the no-delete contract is documented and test-covered.
- **`ConfigurationWriter`** is a model for atomic persistence (temp + `CreateNew` + write-through + flush + atomic move + owner-only perms + single-writer lock).
- **`BoundedLruCache`** eviction/promotion is correct and stress-tested; **`SessionWriteLock`** always releases via `using`; **`WardGate`** uses `RunContinuationsAsynchronously`, timeout auto-deny, and tombstone pruning; **`DataProtectionSecretStore`** zeroes plaintext buffers.

---

# Part B — MCP, llama-server, background services, Comm Link (L1b)

Pass agents: [MCP](f5b6b090-b9d3-40e2-8ec7-e351969bc5b1) · [llama-server](94c3df3d-5144-4fee-a3d5-a0defea53cd5) · [Apprentice/background](9a43bc08-101f-4125-b279-a0e6813fe6d9) · [event-bus/daemons/CommLink](cf8a3b1d-c624-4429-884b-c11d50c8399b). The four highest-impact claims were re-verified against source; one claimed P1 (llama cache-key `..` traversal) was **disproved** and excluded — see the note under "Verified strengths".

## P1 findings

### [P1][security] `execute_command` spawns child processes with the full host environment
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs:1947-1965`
- **Observation:** The `ProcessStartInfo` sets `UseShellExecute = false` and uses `ArgumentList` (good — no shell), but never clears `psi.Environment`. Workspace-scoped MCP children *do* scrub the environment (`McpConnectionManager.cs:1156`); `execute_command` does not.
- **Impact:** A model-invoked command (e.g. `printenv`/`env`) inherits and can echo back the host process environment — which, per the project's own guidance, is where provider **API keys** live (`ARCANUM_Arcanum__Providers__…__ApiKey`). Command stdout is returned to the model, so this is a secret-exfiltration surface. Mitigated (not eliminated) by `execute_command` being a ward-gated Forbidden Art and Sanctum-bounded, but the inconsistency with MCP child scrubbing is a real gap.
- **Recommendation:** Clear `psi.Environment` and pass only an explicit allowlist (reuse the workspace MCP `ScrubProcessEnvironment` policy).

### [P1][reliability] Apprentice intervene+resume persists `Running` before acquiring an execution slot
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Hosting/ApprenticeService.cs:400-409`
- **Observation:** On resume, `apprentice.Status = Running` is saved (`:400-402`) and only then `TryAcquireExecutionSlot(..., queueOnCapacity: false, …)` runs; on `Apprentice.MaxReached` it returns the failure with the DB already showing `Running`.
- **Impact:** When at `MaxConcurrentApprentices`, a Divine-Intervention resume marks the apprentice `Running` but starts no execution task — it is stuck "running" with nothing executing until manual action.
- **Recommendation:** Acquire the slot first (or revert to `Escalated` on capacity failure).

### [P1][reliability] Crash-recovery silently drops resumable apprentices when both gate and pending queue are full
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/Hosting/ApprenticeService.cs:484-488,528-550`
- **Observation:** Recovery does `if (!TryAcquireExecutionSlot(id, queueOnCapacity: true, out _)) continue;` — when the concurrency gate **and** `_pendingStarts` are both full, it `continue`s with no log and no persisted "still needs resume" marker; the DB still says `Running`.
- **Impact:** Apprentices that were running before a restart are never resumed (until a future restart that happens to have capacity), with no operator signal.
- **Recommendation:** Log at warning with the apprentice id; persist a resume-pending flag or escalate after N failed recovery attempts.

### [P1][reliability] llama-server port arithmetic can exceed 65535
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/LlamaCpp/LlamaServerManager.cs:557-567` (clamps at `Core/Configuration/ArcanumSettingClamps.cs:160-162`)
- **Observation:** `PortStart` and `PortRange` are each clamped to `1..65535`, but the chosen port `portStart + ((startOffset + i) % portRange)` is not bounded (e.g. `PortStart=40000`, `PortRange=30000` → up to 69999).
- **Impact:** Bind/`Process.Start` fails or health probes target an invalid URI; spurious startup failures.
- **Recommendation:** Clamp the computed port to `1..65535`, or validate `PortStart + PortRange - 1 ≤ 65535` in `ConfigurationValidator` (note: that validator must also be wired to run at startup — see 00-core.md P1).

### [P1][resource-safety] Unexpected llama-server exit leaks the `Process` and its event handlers
- **Location:** `src/RetroDownfall.Arcanum.Infrastructure/LlamaCpp/LlamaServerManager.cs:1045-1079`
- **Observation:** The `Exited` handler sets `State = Error`, publishes an event, and calls `_onUnexpectedExit(CacheKey)`, but never calls `DetachAndDisposeProcess` (cancel output reads, unsubscribe handlers, `process.Dispose()`) — that path exists only in `StopAsync`.
- **Impact:** Under a crash/restart loop, undisposed `Process` handles and live `Exited`/output handlers accumulate until finalization; the `ManagedLlamaServer` is orphaned but still subscribed.
- **Recommendation:** Dispose the process under the gate in `OnExited` (guard against double-dispose with `StopAsync`).

## P2 findings

### MCP layer
- **[P2][security] Global MCP servers inherit the full host environment** — `Mcp/McpConnectionManager.cs:1156` (`stripUserEnvironment = entry.ScopeWorkingDirectory is not null`). Only workspace-scoped servers are scrubbed; global `~/.config/arcanum/mcp.json` server binaries see host secrets. Lower than `execute_command` because global servers are operator-curated, but still worth defaulting to scrub-with-opt-in.
- **[P2][reliability] In-process tool server silently drops oversized/malformed requests that carry an `id`** — `Mcp/ArcanumInternalToolServer.cs:260-290`. A `tools/call` whose serialized line exceeds `MaxJsonRpcLineBytes` (e.g. a large `write_file`) returns with no JSON-RPC response, so `McpClient.SendRequestAsync` blocks until `RequestTimeoutSeconds` instead of getting a clear "payload too large." Emit an error response keyed to the request id.
- **[P2][reliability] No outbound JSON-RPC line-size cap** — `Mcp/McpClient.cs:196`, `Mcp/McpProcessTransport.cs:257-261`, `Mcp/InProcessMcpTransport.cs:191-193`. Inbound is capped; outbound serializes the full request first, so a large request allocates unbounded before the peer rejects it. Enforce the cap before writing.
- **[P2][cancellation] `notifications/cancelled` is registered only on the caller token, not the timeout-linked token** — `Mcp/McpClient.cs:181-198`. On `RequestTimeoutSeconds` expiry the external server keeps processing (wasted CPU, possible duplicate side effects). Register wire-cancel on the wait token.
- **[P2][concurrency] `MaxServers` check-then-act race** — `Mcp/McpConnectionManager.cs:797-813`. Parallel first-touch of multiple workspaces can exceed the cap (registration isn't under `_registryLock`).
- **[P2][reliability] `McpBridgeTool` fallback can double-invoke a tool** — `Mcp/McpBridgeTool.cs:65-79`. Any non-cancel exception from the local client (including a post-execution transport error) triggers the global fallback, re-running a possibly-mutating tool. Restrict fallback to idempotent/transport failures.
- **[P2][security] Write path lacks open-handle identity revalidation** — `Mcp/SandboxedFileIo.cs:112-196`. Reads use `FileHandleIdentity` post-open checks; the write path validates lexically then `File.Move`s, leaving a TOCTOU window if the target is swapped (symlink/rename). Mirror the read-path handle check.

### llama-server / GGUF cache
- **[P2][reliability] Port free-check TOCTOU** — `LlamaCpp/LlamaServerManager.cs:567-571`. `IsPortFree` binds+releases a probe listener, then the real spawn binds later; concurrent `EnsureServerAsync` calls can pick the same port. Retry on bind failure / hold a port-allocation lock across check+spawn.
- **[P2][reliability] `portOverride` is used unvalidated** — `LlamaCpp/LlamaServerManager.cs:378,415-417`. An out-of-range/taken override fails with a single attempt. Clamp and optionally `IsPortFree`-check it.
- **[P2][reliability] LRU eviction can exceed `MaxCachedModels`** — `LlamaCpp/TheReliquary.cs:737-764`. If every LRU candidate is in use, eviction never reaches the target and the cache stays over cap. Evict on stop or fail the pull with a clear over-cap error.
- **[P2][reliability] Non-atomic cache finalize (model moved before manifest written)** — `LlamaCpp/TheReliquary.cs:659-688`. A crash between `File.Move(model)` and the manifest write leaves a model with no/partial manifest; integrity then falls back to config hash only. Write the manifest to a temp file and atomic-rename, or require re-verification when the manifest is missing.
- **[P2][security] Legacy/manifest-less cache entries skip integrity when no config hash is supplied** — `LlamaCpp/TheReliquary.cs:812-840`. With `RequireModelHash=false` and a missing/corrupt manifest, a cache hit is accepted with no hash comparison. Require a matching manifest (or `ModelSha256Map` entry) when `RequireModelHash` is true.

### Apprentice engine
- **[P2][reliability] Resumed-step re-execution on shutdown/cancel** — `Hosting/ApprenticeService.cs:1154-1156`. `StopAsync`/external cancel swallows `OperationCanceledException` without a terminal status, leaving `Running`/`in_progress`; the at-least-once resume design then re-runs a partially completed step, which can duplicate non-idempotent tool side effects. Checkpoint step state, or persist `Paused`/`Interrupted` on shutdown.
- **[P2][concurrency] Simulacrum spawns one `Task` + DI scope per parallel step** — `Hosting/ApprenticeService.cs:1630-1652`. `MaxSimulacra` bounds concurrency via a semaphore but not the number of tasks/scopes allocated upfront for a large contiguous parallel group. Chunk groups to `MaxSimulacra` (or cap group size at plan parse).
- **[P2][reliability] A single Simulacrum branch fault fails the whole apprentice after siblings ran** — `Hosting/ApprenticeService.cs:1652`. `Task.WhenAll(branchTasks)` has no per-branch catch; one non-cancel exception fails the apprentice while sibling branches may already have invoked tools. Catch per branch and reconcile.
- **[P2][concurrency] Pending-start queue issues**: duplicate enqueues for the same apprentice (`:507-538`); the queue is in-memory and lost on shutdown though clients were told "queued" (`:34,528-538`); queued-at-capacity apprentices are `Idle` and therefore **not cancellable** (`:2455-2468`) so they start later despite an abort. Track pending ids in a set, persist queue intent, and treat pending as cancellable.
- **[P2][reliability] Crash recovery re-emits `ApprenticeStarted` for `Planning` apprentices** — `Hosting/ApprenticeService.cs:688-705` (duplicate chronicle/SSE "started" after restart). Emit a distinct `ApprenticeResumed`.

### Event bus / daemons / Comm Link / PID
- **[P2][concurrency] `ScryingPool.Publish` holds the hub lock for the entire fan-out** — `Hosting/ScryingPool.cs:93-108`. Under many subscribers this serializes all publishers for that type/session. Snapshot channels under lock, then write outside it.
- **[P2][concurrency] Daemon single-running enforcement is racy / in-flight map is mis-keyed** — `Daemons/DaemonRunner.cs:41-49` (check-then-start TOCTOU) and `Daemons/InMemoryDaemonExecutionRepository.cs:130,186,225` (`_inFlightByDaemon[daemonId] = executionId` overwrites; completion does a blind `TryRemove` without comparing the execution id). Overlapping runs can both start and `HasRunningExecution` can report stale state. Use atomic add + id-matched removal.
- **[P2][reliability] Linux daemon uninstall ignores `systemctl` exit codes** — `Hosting/LinuxDaemonManager.cs:111-130` returns `Result.Success()` even if `disable --now` failed; the service can remain enabled while the CLI says "uninstalled." Propagate the exit code.
- **[P2][reliability] macOS daemon install writes the plist without a `ProcessPath` guard** — `Hosting/MacOsDaemonManager.cs:21-47,206-227` (`Environment.ProcessPath ?? string.Empty`); Linux guards this. Can produce a launchd job with empty `ProgramArguments`.
- **[P2][reliability] Comm Link silently suppresses blocked/misconfigured webhooks** — `CommLink/WebhookCommLinkDispatcher.cs:27-78` returns `Result.Success()` for missing URL, invalid URI, scheme/host policy rejection, and SSRF block. Callers can't distinguish "delivered" from "dropped." This compounds with the **Core P1** that `ConfigurationValidator` (which would catch `AllowedHosts`/empty-`AllowedSchemes` misconfig) never runs at startup. Return a distinct failure/"suppressed" result, and validate CommLink config at startup.
- **[P2][performance] `HttpResponseBodyDrainer` skips draining when the declared length exceeds the cap** — `CommLink/HttpResponseBodyDrainer.cs:27-31`; an undrained body can prevent HTTP connection reuse. Drain up to the cap regardless.
- **[P2][resource-safety] PID file has no exclusive-create guard** — `Hosting/PidFileService.cs:40-54` does read-stale-check then `File.WriteAllText`; two simultaneous starts can both pass and run. Use exclusive create / advisory lock.

## P3 findings (Part B)

- **MCP:** `StopAllAsync` doesn't stop in-process internal servers (relies on `DisposeAsync`) — `Mcp/McpConnectionManager.cs:111-124`; crashed `AlwaysOn` external servers aren't auto-restarted after backoff — `:1362-1457`; `list_directory` returns absolute paths instead of workspace-relative — `Mcp/ArcanumInternalToolServer.cs:1783,1839`.
- **llama:** per-server `SemaphoreSlim` not disposed (documented intentional, `DESIGN.md:1548`); `_downloadLocks` entries never removed (`TheReliquary.cs:42`); manager is not `IDisposable` (relies on the lifecycle hosted service).
- **Apprentice/hosting:** execution CTS is linked to `CancellationToken.None`, not the host stopping token (`ApprenticeService.cs:643-645`) — works today only because `StopAsync` cancels the tokens explicitly; Chronicle/Session/EventBus hub channel capacity is frozen at first creation (no hot-reload) — `ChronicleHub.cs:89-94`, `InMemoryEventBus.cs:54-57`; `SessionEventHub` doesn't surface a drop signal like `ChronicleHub` does.
- **Comm Link:** no retry/backoff on transient webhook failure (`WebhookCommLinkDispatcher.cs`, `CommLinkMultiplexer.cs`).
- **Style:** blank-line house-style drift in several files (`MacOsDaemonManager`, `SessionEventHub`, `GrimoireDbPassphraseSource`, etc.).

## Verified strengths (L1b)

- **Subprocess discipline:** `McpProcessTransport.DisposeAsync` kills the entire process tree, completes the channel, and awaits stdout/stderr with a grace period; startup failure disposes the client. `execute_command` is genuinely shell-free (`ArgumentList`) and kills the tree on timeout/cancel. The llama `StopAsync` tree-kills on all OSes, and failed spawns are stopped.
- **Anti-OOM JSON-RPC:** `McpStdioLineReader` enforces the UTF-8 byte budget and drains the rest of an oversized line; stderr uses the same cap; `tools/list` is bounded by per-server/per-page/total-byte caps with cursor dedup.
- **MCP trust + handle safety:** workspace servers are blocked until hash-matched trust; read tools do pre-open path validation **and** post-open dev/ino (or Windows volume+index) identity checks, fail-closed.
- **llama SSRF + integrity:** pre-flight `OutboundUrlGuard`, pinned egress handler, manual redirect re-validation per hop, SHA-256 verification with delete-on-mismatch, streamed download with `ModelDownloadMaxBytes`, `.download.tmp` + resume + 24h stale sweep, and per-key `Lazy<Task>` download dedup. **The claimed cache-key `..` traversal was investigated and disproved:** `LlamaCacheKey.SanitizeSegment` replaces path separators with `_` (regex) and then `Trim('_','.',' ')`, so `NormalizeModelKey("..")` resolves to empty and **throws** rather than escaping the cache root (`Core/LlamaCpp/LlamaCacheKey.cs:80-87`).
- **Concurrency primitives:** `ApprenticeConcurrencyGate`/`SseConnectionGate` use atomic increment-then-compare with idempotent leases; `ScryingPool` uses per-subscriber bounded `DropOldest` channels with `finally`-unsubscribe (no dead-subscriber leak); Second Wind backoff is exponential-with-full-jitter and bounded by `MaxStepRetries` (no poison-loop); apprentice `StopAsync` drains up to 30s; daemon managers all use `ArgumentList` (no shell injection).

