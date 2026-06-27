# 99 — Cross-cutting sweeps & systemic patterns

This report synthesizes patterns that recur **across** modules (and therefore are best fixed once, systemically) and records the results of whole-repo verification sweeps. Individual instances are cited in the per-module reports; this file groups them by root cause.

---

## Whole-repo verification sweeps (results)

| Sweep | Result |
|-------|--------|
| **`AIFunctionFactory.Create` in `src/`** | **None.** All `AIFunction` tools use hand-authored `JsonDocument` schemas — AOT-correct. |
| **`async void` in `src/`** | **None.** No fire-and-forget `async void` anywhere in production code. |
| **Sync-over-async (`.Result`/`.Wait()`/`.GetAwaiter().GetResult()`)** | Only two `.Result` reads, both in `LlamaServerManager.cs:249,265`, and **both are guarded** by `lazy.Value.IsCompletedSuccessfully` (`:244,:260`) — they never block. No real sync-over-async. |
| **Manual `new HttpClient(...)`** | Exactly one, `ChatClientFactory.cs:308` (`disposeHandler: true`, ref-counted in the endpoint-client cache). Everything else uses `IHttpClientFactory`. (The cache's over-cap eviction is the P2 in 02-api.md.) |
| **Reflection-based JSON** | `JsonSerializer.Serialize/Deserialize` calls were confirmed by the per-area passes to use source-generated `JsonTypeInfo`/contexts (`ArcanumJsonContext`, the Core contexts, `McpJsonSerializerContext`); the repo also enforces this with `scripts/verify-aot-il-warnings.sh`. |
| **Outbound egress** | All untrusted egress (llama pull, CommLink webhook) routes through `OutboundUrlGuard` with connect-time DNS re-resolution + IP pinning and redirects disabled. Verified solid. |

The AOT/trim posture and the async-correctness posture are genuinely strong; the systemic issues below are about **reliability, resource growth, and consistency**, not AOT.

---

## Systemic patterns (fix once, benefit everywhere)

### S1. Semantic configuration is never validated at startup *(highest leverage)*
`ConfigurationValidator.Validate` and `OutboundUrlGuard.ValidateArcanumSettingsAsync` run **only** on `PUT /api/config` and `POST /api/config/validate` — never during host boot (00-core.md P1). Wiring them into startup (an `IStartupFilter`/`IHostedService`) is the single highest-leverage fix because it catches, at boot instead of at runtime:
- invalid `DefaultModel`/`FastModel` (model-resolution failures),
- MCP `RequestTimeoutSeconds`/`MaxJsonRpcLineBytes` ordering,
- the **llama port-overflow** (01-infra P1 #13) — add `PortStart + PortRange - 1 ≤ 65535`,
- CommLink `AllowedHosts` / empty `AllowedSchemes` (01-infra P2 — today they silently no-send),
- non-existent allow-list roots and blocked/SSRF webhook URLs.

### S2. The "load everything then trim in memory" EF pattern
`GrimoireRepository.GetSessionAsync`/`GetSessionEntriesAsync`/`GetRecentSessionEntriesAsync` and the analytics queries materialize whole tables/sessions and filter in memory (01-infra P1/P2). This single pattern is the root of **both**:
- the **hot-path per-turn perf** cost (full session loaded every inference turn), and
- the **P1 silent context-compression data loss** (02-api.md) — the global most-recent-N window is taken before the summary watermark filter.

`SessionRepository.GetEntriesAscendingAsync` already shows the correct server-side `OrderByDescending(...).Take(...)`. Porting that shape (anchored at the watermark) fixes the correctness bug and the perf issue together.

### S3. Unbounded per-key state dictionaries that never evict
A repeated shape: `ConcurrentDictionary<TKey, SemaphoreSlim|state>` populated by `GetOrAdd` with no removal —
`SessionWriteLock` (per session), `SpellRepository._workspaceLocks` (per workspace), `TheReliquary._downloadLocks` (per model), `SanctumBreachStore._buffers` (per campaign), `HumanPromptRegistry._waiters` (per prompt), MCP server registry. Each leaks slowly in a long-lived host. A single shared **self-evicting keyed-lock/keyed-cache utility** (evict on zero in-flight, or bounded LRU) would resolve all of them consistently. `BoundedLruCache` is the existing building block.

### S4. Silent failure / fail-open swallowing
Several paths log-and-continue (or return success) on conditions an operator needs to see:
- CommLink returns `Result.Success()` when a webhook is blocked/misconfigured (01-infra P2),
- `SecureFilePermissions` `catch (Exception) { /* best effort */ }` with no log on chmod failure (01-infra P2),
- Apprentice crash-recovery `continue`s with no log when the queue is full (01-infra P1 #12),
- `SessionEventHub` drops SSE events without the `EventsDropped` signal `ChronicleHub` emits (01-infra P3),
- in-process MCP server drops oversized requests with an `id` and no response (01-infra P2),
- `HumanPromptRegistry`/`ArcanumApiClient` swallow exceptions in `finally`/cleanup without logging.

Recommendation: adopt a consistent "log-at-warning + surface a typed result" policy for suppressed failures; reserve silent drops for genuinely best-effort telemetry.

### S5. Capacity/config frozen at construction (no hot-reload)
`InMemoryLogRingBuffer` capacity, `ChronicleHub`/`SessionEventHub`/`InMemoryEventBus` channel capacity, and `ManaPreflight` LRU size are read once at construction; `IOptionsMonitor.OnChange` is not honored, so `PUT /api/config` changes to these need a restart. Either subscribe to `OnChange` or document these keys as startup-only (and have the validator say so).

### S6. Check-then-act on soft capacity caps
`WardGate` `MaxActiveWards`, MCP `MaxServers`, daemon single-running enforcement, and the `ChatClientFactory` endpoint-cache cap all do non-atomic check-then-act, so they can overshoot under concurrency (01-infra, 02-api). Impact is low at single-user scale, but `SseConnectionGate`/`ApprenticeConcurrencyGate` already demonstrate the correct atomic increment-then-compare-then-rollback pattern to copy.

### S7. Divergent session write paths
`GrimoireRepository` writes are lock-serialized (`SessionWriteLock`), `SqliteBusyRetry`-wrapped, and maintain `UnsummarizedEntryCount`; `SessionRepository` writes do none of these (01-infra P1 #3, P2). Consolidating session-entry writes behind one path (or sharing the lock/retry/counter helpers) removes the counter drift, the busy-failure asymmetry, and the dual-writer race.

### S8. Child-process environment inheritance
`execute_command` (01-infra P1 #10) and global MCP servers inherit the full host environment (which holds provider API keys), while workspace MCP servers scrub it. Apply one environment-scrubbing policy to all spawned children.

### S9. Non-atomic file writes
`SpellRepository` update and `TheReliquary` manifest finalize write in place / out of order, risking corruption on crash (01-infra P1 #6, P2). `ConfigurationWriter` is the gold standard (temp + write-through + flush + atomic move + owner-only perms) — reuse it everywhere a durable file is written.

### S10. Streaming broken-pipe handling
The non-OpenAI SSE/NDJSON writers (`EventEndpoints`, `InferenceExecuteWriter`, `ChronicleSseStreamWriter`) catch only `OperationCanceledException`; an `IOException` on a closed socket *before* `RequestAborted` fires is unhandled and can delay inference cancellation (02-api P2). Treat write `IOException`/`HttpIOException` as a disconnect and cancel the linked CTS.

### S11. House-style blank-line drift
The "one blank line after each C# line" rule is unevenly applied (Core entities, several Infra security/daemon/hosting files, the Api OpenAI mapper/registry/tools, several Cli commands). The repo already ships `scripts/align-csharp-blanklines.sh` / `align_csharp_blanklines.py` — a single formatting pass would normalize this repo-wide.

---

## Path-containment posture (verified)

Path containment is **strong and layered** where it matters most — `PhysicalFileSystemBrowser`, `WorkspacePathResolver`, `SpellPathPolicy`, `CodexReader`, and MCP `SandboxedFileIo`/`ToolHelpers` all do lexical containment + symlink resolution + (for reads) post-open `FileHandleIdentity` (dev/ino / volume+index) revalidation. The audited gaps are localized, not systemic:
- `SpellScanner` is the notable exception — no cycle guard and prefix-only containment without handle revalidation (01-infra P1 #4, #5).
- MCP **write** path lacks the post-open handle check the read path has (01-infra P2).
- The claimed llama cache-key `..` traversal was **disproved** (sanitizer strips separators and dots; see 01-infra "Verified strengths").

---

## Documentation accuracy

Code-vs-docs discrepancies surfaced during this audit (e.g. CommLink `AllowedHosts` "rejected at startup" wording not matching dispatch-time enforcement; the `/v1` `tool_calls` omission being intentional-and-documented; the Sanctum route-parameter naming) overlap with the separate **Arcanum Documentation Audit** plan. Recommend folding these into that effort rather than tracking them twice. No new doc-only findings of consequence were found beyond that plan's scope.

---

See [SUMMARY.md](SUMMARY.md) for the consolidated, severity-ranked master index and the recommended remediation ordering.
