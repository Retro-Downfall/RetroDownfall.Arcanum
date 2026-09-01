# Compendium

Compendium is Arcanum's desktop `arcanum.json` editor. It is a .NET 10 Avalonia application (`RetroDownfall.Compendium.Ux`) and does not run inference, open the Grimoire database, execute tools, manage the daemon, or perform blob migration/key rotation. Persistence operators use `arcanum data encryption status|migrate|verify|rotate-key`; those code-owned safety limits are intentionally not editable configuration.

## Documentation authority

This file is the source of truth for Arcanum's public configuration elements and structure. The other canonical documents are [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md) for architecture and design, [`Arcanum.API.md`](Arcanum.API.md) for native and OpenAI-compatible API contracts, [`Arcanum.Command.Reference.md`](Arcanum.Command.Reference.md) for complete CLI usage, [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md) for conceptual navigation, and [`Arcanum.DEBUGGING.Human.md`](Arcanum.DEBUGGING.Human.md) for verified breakpoint and recipe guides. [`README.md`](../README.md) is the curated public repository front page, not an implementation or configuration contract. Configuration changes update this reference, `SettingDescriptors`, validation, source-generated metadata, and the editor together; other docs link here instead of reproducing the complete key table.

## Launch and configuration file

The Forge opens Compendium from **View → Open Compendium**, The Anvil, the setup wizard, disabled-feature guidance, and the macOS application-menu **Settings...** item. The CLI opens the same editor with `arcanum open compendium`; `arcanum config open` remains the configuration-family entry. Discovery checks installed platform application locations first and recognizes a side-by-side extracted `compendium-win-x64` folder or the active `compendium-linux-x64|arm64` architecture before the repository development project. If launch fails, every attempted candidate is shown with a safe kind/display path, followed by a repository-relative `dotnet run --project ...` command and the `arcanum config edit` fallback. Copyable command arguments use PowerShell quoting on Windows and POSIX-shell quoting on macOS/Linux; actual process launch remains shell-free.

Each launcher passes a versioned settings deep link as one `ProcessStartInfo.ArgumentList` value, never through a shell. The envelope carries only the Compendium target, configuration resource kind, initial view, and an optional safe connection-profile identifier; it never carries a credential, endpoint, configuration value, file content, attachment, or path. Compendium safely selects Edition for a valid settings request and also for normal startup, a malformed or wrong-target request, an unknown view, or a future schema. Starting a new process is the portable behavior; Compendium is reported as reused/focused only if a platform integration actually does so.

Compendium edits:

- macOS/Linux: `~/.config/arcanum/arcanum.json`
- Windows: `%USERPROFILE%\.config\arcanum\arcanum.json`

For non-visual work, use `arcanum config path`, `show`, `get <key>`, `set <key> [value]`, `validate`, or `edit`. CLI reads preserve the API's provider-endpoint redaction. Dot paths use the source-generated configuration descriptor metadata and explicit numeric indices for collections (`providers.0.endpoint`). Sensitive endpoint updates omit `[value]` and read redirected stdin or a hidden prompt so the value never appears in argv or output. The commands use `/api/config` while the host is available and clearly label canonical local bootstrap mode otherwise.

Paths are resolved through `ArcanumPaths`; service code does not use platform-specific path literals.

## Public configuration contract

Compendium exposes genuine deployment choices, provider/model facts, credential or secret references, security policy, integration endpoints and allowlists, feature opt-ins, operator schedules, host-capacity choices, pricing facts, and user preferences.

The navigation is intentionally limited to:

1. **Presets** — shared workflow descriptions, canonical current effective state, separately labelled selected-preset projection, exact diff, prerequisites, progressive disclosure, recommendations, completion summaries, and Apply/Reset actions. Failed state inspection is shown as unavailable instead of retaining a stale active/drifted label.
2. **Edition** — runtime hardening mode.
3. **Host** — port, CORS, external HTTPS binding, certificate selection, inference-audit policy, and buffered-log level.
4. **Providers & Models** — `DefaultModel`, `FastModel`, provider endpoint and credential environment-variable reference, model inventory, vision, context capacity, and factual reasoning capabilities/wire dialect. A **Familiar** row (`ClaudeCodeCli` / `CodexCli`) hides the endpoint and credential fields — it has neither — and shows a command override, a readiness line with a **Re-probe** button, and the hidden-models list instead.

   Readiness comes from the running host over `GET /api/providers/{name}/familiar-probe`; Compendium does not spawn processes. When the host is not running, the page says so and names `arcanum serve` rather than spinning, and the hidden-models list stays fully editable — nothing about editing depends on the probe. Compendium never offers to sign in: remediation is a command you run yourself, and Arcanum never reads the CLI's credential store.
5. **Security** — Ward record and tool-advertisement policy, guardrails, unsafe-process acknowledgement, metrics authentication, distinct Perception/Spell/Campaign roots, and upload and image MIME allowlists.
6. **Workspaces** — default root and explicit write permission.
7. **Features** — flat capability opt-ins, including Conclave/A2A, Apprentices, embeddings, Saga, Scrying, attachments, browsing, guardrails, workspace checks, and memory management. The Covenant introduces no configuration key yet: its runtime is composed — prompt placement, token attribution, per-attempt admission, disclosure evidence, staging, atomic publication, and the two `propose_covenant` / `retire_covenant` MCP tools — eligible turns advertise both tools when the feature and canonical tier are healthy. Proposals stage only into the Campaign Proposed lane; retirement emits the ordinary record-only `ungated` pair, binds the exact canonical preflight and Campaign-scoped one-call capability, commits disclosure before the Process effect, accounts for egress, and still dispatches through Sanctum. Attendance and the removed Ward enabled/auto-approval settings do not decide retirement; those obsolete keys are rejected with migration guidance. `ForbiddenArts` defaults empty and remains only an explicit advertisement filter for ordinary tools. The runtime already honours the published capability flag on `CovenantAvailabilitySnapshot`, so a disabled installation adds no Covenant prompt bytes and performs no Covenant read. One environment variable changes meaning rather than a key being added: `ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1` no longer enables the unsandboxed escape hatch by itself. A host started with it but without a completed taint transition now refuses to start with `Covenant.HostToolsTransitionRequired`, and completing that transition permanently closes Covenant on the installation. The public contract adds no configuration key either. It freezes the public request and response shapes, the error codes and their exact HTTP statuses, the five service ports, the lease-bound response types, and the two durable recovery checkpoints, so that the four surfaces that consume them build against one contract instead of four. Nothing in it executes on any path, so a Compendium built against this revision renders exactly what it rendered before. The shared erasure kernels add no configuration key either. They build the shared protected-artifact and managed-file erasure kernels, the schema-repair journal and its pre-readiness resume, and the two durable maintenance operation kinds; every one of them runs only under an operator authority or an exclusive lease that no configuration can grant, and nothing in it executes on a turn path. Exactly one key is added, `Arcanum:Features:Covenant`, default off. The default is the guarantee rather than a convention: an installation that never names it adds no Covenant prompt bytes, performs no Covenant read, and renders byte-identical prompts. It is a `{ get; set; }` property because the configuration binding generator silently skips `init`-only properties (dotnet/runtime#107856), which would have left the feature permanently off while `arcanum.json` still said otherwise. Compendium renders the exact shared enablement disclosure and every resolved provider-retention help action *before* it constructs the toggle — order asserted by test, because a warning below the switch is read after the decision. A change is published to the in-memory availability gate without a restart and never touches SQLCipher or the secret store, so a disable still works on a locked or degraded database; callbacks are serialized and republish the monitor's current value, so two rapid edits delivered out of order cannot leave an installation enabled after the operator's last action was to disable it. The dedicated Covenant management routes, the Campaign-path and Session-binding administration surfaces, and the `arcanum memory covenant` commands are **not** included and will add none; the existing `/api/data` erasure lifecycle is separate and adds no configuration key. Backup and restore add no configuration key either, and change the meaning of `Arcanum:Features:Covenant` in one direction only: with it on, a physical backup now commits a durable disclosure receipt before it reads the protected database and again before it writes the first archive byte, and a selective Session import runs through the protected transfer store under one atomic compound lease per Session. With it off — the default — backup, restore, and selective import behave byte-for-byte as they did before. One further key is added, `Arcanum:Features:Annals`, default on, governing automatic durable-memory history. Omitted configuration makes ordinary Saga extraction and Lexicon writes append `AgentExtracted` and `AgentAsserted` claims in the subjects' own transactions, so a required claim failure rolls back the subject write. Explicit `false` stops only future automatic claims while existing history remains readable. Corrections, retirements, and reinstatements that change Saga state still append required evidence; their idempotent outcomes and every pin/unpin append nothing to the Annals, and erasure remains ungated. Re-enabling Annals does not backfill the opted-out period or synthesize prior claims, but a later ordinary write — notably a Lexicon upsert — follows the then-current enabled policy. The schema-v3 sweep was a one-time historical upgrade, and this policy change adds no migration or rerun. It is a `{ get; set; }` property because the configuration binding generator silently skips `init`-only properties (dotnet/runtime#107856), which would ignore an explicit opt-out while `arcanum.json` still said otherwise.
8. **Integrations** — A2A identity/allowlist, CommLink webhook environment reference and allowlists, embedding provider/model facts, MCP plaintext-host policy, trusted workspace-check executable, and custom profiles.
9. **Execution** — operator-controlled host concurrency and backpressure for Apprentices, SSE streams, and batches.
10. **Cost** — default/per-model pricing and daily budget policy.
11. **Retention** — unified sweep bounds, typed per-class policies, accounting floor, and explicit protected-session holds.
12. **Daemon** — Unseen Servant schedules and host concurrency.
13. **CLI** — built-in theme selection and mana-bar preference.

## Preset workflow

Compendium consumes the same `IConfigurationPresetService`, immutable catalog, planner, state, and persistence results as `arcanum preset`; it does not maintain UI-only descriptions or duplicate overlay/apply logic. A preset is a versioned partial overlay, not a new `arcanum.json` section or a hidden runtime mode. The five workflow presets are current v2 definitions; Advanced/Custom remains v1:

| Preset | Explicit ownership and prerequisite intent |
|--------|--------------------------------------------|
| **General Assistant** (`general-assistant`, v2) | Owns attachments, conservative Saga/extraction/memory-management values, and the unsandboxed-child safe default. Requires a provider/model. |
| **Coding Workspace** (`coding-workspace`, v2) | Owns workspace checks, file-write permission, and the unsandboxed-child safe default. Requires a provider/model and configured default workspace root; Weave indexing stays deferred. |
| **Research** (`research`, v2) | Owns web browsing and the unsandboxed-child safe default. Requires a provider/model and existing research credential; it does not add citation, retry, hop, timeout, or cost knobs. |
| **Private/Offline** (`private-offline`, v2) | Owns loopback binding, web browsing off, enterprise telemetry off, and the unsandboxed-child safe default. Requires a loopback provider; authored MCP and other integrations are disclosed rather than erased. |
| **Automation** (`automation`, v2) | Owns the operator-facing unattended default and the unsandboxed-child safe default. Requires a provider/model and a positive operator-authored daily budget; it does not create a schedule, enlarge a budget, or deny ordinary tools. |
| **Advanced/Custom** (`advanced-custom`, v1) | Owns no paths and changes nothing; direct editing remains authoritative. |

The five retired-key-era v1 workflow definitions remain frozen for historical sidecar validation, including their original wording and the two retired Ward approval paths. A matching raw v1 state/rollback pair is fully validated before Compendium receives an in-memory v2 survivor projection. Reads do not rewrite those sidecars, and reset/recovery never restores or writes a retired path.

Selecting a card is read-only. The page shows shared purpose and enable/disable/security/provider/ resource disclosures; **Custom**, **Active**, or **Drifted** state; exact owned setting rows; prerequisite detail and resolution commands; essential first choice; intentionally deferred features; recommendations; the Ward/Sanctum/Weave/Saga/Lexicon plain-language glossary; and the provider/model, workspace/campaign, memory-source, tool-policy, privacy, and next-command completion summary. Recommendations are directly executable; Coding Workspace shows `arcanum run --workspace . "Inspect this workspace and summarize it."`, including the required prompt.

Each diff row distinguishes the persisted `arcanum.json` value, the effective value after recognized environment overrides, and the proposed persisted value. It also names the current source and environment variable (never its value), environment effectiveness, ownership, prerequisite IDs, restart requirement, and separate persisted/effective change flags. An environment override therefore remains the effective truth even if Apply changes the file. Only an override that contradicts an owned safety/privacy boundary blocks Apply; a benign feature mask is shown as drift and does not make the plan inapplicable. Only a Research preview/diff or Apply probes the secure research-credential store; other preset cards, inspection, reset, and mutations do not.

**Apply preset** and **Reset preset** are explicit actions and add no confirmation dialog. They are disabled while the root editor has unsaved changes, is saving, or has an unreviewed external file change; the page says to save or cancel/reload instead of silently discarding edits. Apply requires a coherent prerequisite-complete plan and canonical full-candidate validation, preserves secrets and every unowned value, checks concurrent changes, enters the current-user cross-process transaction shared by every canonical configuration writer, writes atomically, and records owner-only provenance plus rollback state outside `arcanum.json`. Reapplying the same version and owned values is idempotent.

No provenance is Custom; matching applied persisted/effective owned values are Active; a later owned-value change is Drifted. Reset restores only paths that still equal the preset-applied value, preserves user drift and unrelated edits, reports both counts, and clears preset provenance. A prepared transaction journal stores only owned before/after values and hashes plus previous/next provenance. Failure recovery conditionally reverses values that still match the interrupted write, preserving unrelated and later manual edits. Bounded no-follow sidecar reads and exact catalog ownership/value/hash/state validation reject untrusted provenance before reset or recovery.

No preset silently enables `ListenAny`, unsandboxed tool children, untrusted workspace MCP, destructive memory operations, changes to the explicit `ForbiddenArts` advertisement filter, or changes to explicit token/cost/security policy. Presets do not add retry, timeout, loop-count, or other arbitrary tuning knobs. The guided `arcanum setup` wizard consumes the same preset service for its preset step; this page exposes that service directly and does not implement the wizard's other steps.

## Complete configuration reference

This is the sole complete documentation reference for `arcanum.json`. All 160 editable paths in `SettingDescriptors.All` appear below, and that total is pinned by `SettingDescriptorCoverageTests.Editable_descriptor_count_matches_the_documented_total`, so a descriptor change updates the code, the test, and this page together. Each key uses the exact camel-case dot path; `providers.models.*` applies to every model in every provider, and `daemon.jobs.*` applies to every job. JSON nests these paths beneath an exact top-level `"Arcanum"` object.

Compendium exposes policy and facts, not incidental implementation mechanics. Retained fields are provider/model capabilities, credentials/endpoints, security/permission choices, explicit feature opt-ins, host concurrency/capacity, pricing/budgets, schedules, retention policy, or user preferences. Retry counts, total-operation timeouts, workflow/queue/page/checkpoint counts, and indexing slice sizes are code-owned adaptive behavior and are not editable. Removing a field never removes authentication, containment, SSRF protection, Ward audit records, Sanctum, cryptographic/protocol integrity, single-allocation protection, or explicit operator policy.

Arcanum does not flatten arbitrary environment variables into the configuration tree, but it does reserve one override namespace. A variable named `ARCANUM_Arcanum__<Path>` — the prefix keeps the `Arcanum` wrapper, and `__` separates path segments, for example `ARCANUM_Arcanum__Host__Port` — is applied to the matching descriptor path by `ConfigurationEnvironmentResolver`. **That namespace reaches every editable path on this page, including security policy** such as `security.ward.forbiddenArts` and `security.allowUnsandboxedToolChildren`, so a deployment audit must cover the whole `ARCANUM_Arcanum__*` namespace and not just the two named variables below. `ARCANUM_EDITION` and `ARCANUM_HOST_ANY` are additional explicit runtime overrides. An environment override never touches `arcanum.json`: it applies to a cloned effective snapshot, and `arcanum config show`/`get` report the variable name and whether it took effect without ever printing its value. Secret values use only the dedicated environment references documented below, and a secret reference naming anything inside `ARCANUM_Arcanum__*` (or the `Arcanum__*` binding namespace) fails validation.

### Edition, host, providers, and model selection

| Descriptor key | Type and default | Bounds | Semantics |
|---|---|---|---|
| `edition` | enum `"local"` | `local`, `development` | Runtime hardening mode; `ARCANUM_EDITION` overrides it. Development-only surfaces still require their companion startup flags. |
| `host.port` | `int`, `5001` | 1–65,535 | Loopback HTTP port. |
| `host.corsAllowedOrigins` | `string[]`, `["http://localhost:5001", "http://127.0.0.1:5001", "http://localhost:3000", "http://127.0.0.1:3000"]` | — | Browser origins allowed to read keyed responses. |
| `host.listenAny` | `bool`, `false` | — | All-interface binding is HTTPS-only and also forces rate limiting and metrics authentication; `ARCANUM_HOST_ANY` overrides it. |
| `host.auditLog.enabled` | `bool`, `false` | — | Enables the append-only inference audit trail. |
| `host.auditLog.redactToolArguments` | `bool`, `true` | — | Records tool names without argument JSON. |
| `host.https.enabled` | `bool`, `false` | — | Adds TLS on loopback; required for all-interface binding. |
| `host.https.port` | `int`, `5443` | 1–65,535; must differ from `host.port` | TLS listen port. |
| `host.https.certificatePath` | `string?`, `null` | path | PFX/P12 bundle, or PEM certificate when `privateKeyPath` is supplied. |
| `host.https.privateKeyPath` | `string?`, `null` | path | Optional PEM private key. |
| `host.https.certificatePasswordEnvironmentVariable` | `string?`, `null` | portable, case-insensitively unique environment name | Exact PFX-password reference; omission uses `ARCANUM_HTTPS_CERTIFICATE_PASSWORD`; PEM ignores it. |
| `host.minLogLevelInBuffer` | enum `"information"` | `trace`, `debug`, `information`, `warning`, `error`, `critical` | Minimum severity retained in the in-memory diagnostics buffer. |
| `defaultModel` | `string?`, `null` | configured exact model name | Default when a request omits a model. |
| `fastModel` | `string?`, `null` | configured exact model name | Optional model for eligible internal work. |
| `providers.name` | `string`, `""` | nonblank; unique case-insensitively | Human-readable provider identity. |
| `providers.type` | enum `"OpenAICompatible"` | `OpenAICompatible`, `ClaudeCodeCli`, `CodexCli` | Provider wire contract. Ollama uses its OpenAI-compatible `/v1` endpoint. The two CLI kinds are **Familiars**: a vendor CLI you already installed and signed in to, invoked on your own subscription. |
| `providers.endpoint` | `string`, `""` | absolute HTTP(S) endpoint; **rejected** on a Familiar | OpenAI-compatible base endpoint. N/A for a Familiar, which is spawned rather than dialled. |
| `providers.credentialEnvironmentVariable` | `string?`, `null` | portable, case-insensitively unique environment name; **rejected** on a Familiar | Exact API-key reference; omission derives `ARCANUM_PROVIDER_<NORMALIZED_NAME>_API_KEY`. N/A for a Familiar, which signs in through its own CLI — Arcanum never reads that credential store. |
| `providers.command` | `string?`, `null` | nonblank when present; Familiar kinds only | Path to (or alternate name for) the Familiar binary. Omit to resolve `claude` or `codex` on `PATH`. |
| `providers.hiddenModels` | `string[]`, `[]` | nonblank entries, de-duplicated case-insensitively; Familiar kinds only | Model IDs left out of listings and pickers for this provider. Empty — the default — means every model the CLI offers is available, including models released after you set this up. **Hidden is not blocked:** an explicitly named model still resolves, so this is a decluttering preference and not a policy control. An entry naming a model that is not currently offered is retained, not pruned. |
| `providers.models.name` | `string`, `""` | nonblank; unique within provider | Provider-advertised model ID. A bare string model entry is also accepted. Required for an OpenAI-compatible provider; optional for a Familiar, whose catalogue belongs to the vendor. |
| `providers.models.supportsVision` | `bool`, `false` | — | Declares image-content support. |
| `providers.models.reasoning.wireDialect` | enum, `null` (omitted) | `standard`, `openRouter`, `topLevelReasoningBudget`, `anthropicThinking` | Exact request shape; never inferred from names. Omitting it is not the same as `"standard"`: a model with no `wireDialect` declares no reasoning capability at all, and any request carrying reasoning options is refused. Write `"standard"` explicitly for a model that takes effort and summary options over the plain OpenAI-compatible shape. |
| `providers.models.reasoning.maxBudgetTokens` | `int?`, `null` | 1–2,097,152 | Optional numeric-budget ceiling; the adapter requires a nonstandard dialect for it and rejects a numeric budget under `standard`. A budget alongside `"standard"` — or alongside an omitted `wireDialect`, which is equally unusable — is refused at startup with a pointer at `wireDialect`. |
| `providers.contextWindowLimit` | `int`, `8192` | 256–2,097,152 | Factual provider context capacity. Set it on a Familiar row too: the default suits neither CLI's models, and read-time compression and the Mana bar both measure against it. |

`providers` defaults to `[]`; a usable configuration supplies at least one valid provider and model. A model's optional `reasoning` object defaults to `null`.

A model's `reasoning` block carries exactly the two keys above. `providers.models.reasoning.controlSupport`, `.supportsSummary`, `.supportsFull`, `.supportsStreaming`, `.reportsReasoningTokens`, and `.allowsClientOutput` are not part of the configuration contract: `ModelReasoningSettings` has no such members, the `ModelEntry` JSON converter skips them on read and never writes them, and the host aborts startup on a file that declares any of them (`ConfigurationStartupValidator` via `ConfigurationValidator.RejectObsoleteKeys`). Compendium loads such a file without complaint and drops the keys on the next save, so a file that still carries them must be edited before the host will start. Arcanum derives the corresponding behaviour from the declared wire dialect and budget instead.

### Security and workspaces

| Descriptor key | Type and default | Bounds | Semantics |
|---|---|---|---|
| `security.allowUnsandboxedToolChildren` | `bool`, `false` | — | Explicitly permits process tools without an OS filesystem jail where that escape hatch is supported; it does not bypass Sanctum denials. |
| `security.metricsRequireApiKey` | `bool`, `true` | — | Requires authentication for loopback metrics; external binding always forces it. |
| `security.ward.forbiddenArts` | `string[]`, `[]` | — | Tool names removed from advertisement when a request selects `noForbiddenArts`. The empty default removes nothing; this list never gates execution. |
| `security.ward.unattendedMode` | `bool`, `false` | — | Default unattended-mode request value for operator-facing chat; daemons and Apprentices remain unattended. It does not make ordinary tool calls Ward-denied. |
| `security.guardrails.detectPii` | `bool`, `true` | — | PII policy used only when `features.guardrails` is enabled. |
| `security.guardrails.blockToxicity` | `bool`, `false` | — | Applies the authored toxicity blocklist when guardrails are enabled. |
| `security.guardrails.toxicityBlocklist` | `string[]`, `[]` | — | Case-insensitive authored terms. |
| `security.guardrails.allowedTopics` | `string[]`, `[]` | — | Optional topic-pattern allowlist. |
| `security.guardrails.blockedTopics` | `string[]`, `[]` | — | Optional topic-pattern blocklist. |
| `security.guardrails.auditLog.enabled` | `bool`, `false` | — | Persists guardrail violations when guardrails are enabled. |
| `security.perceptionWorkspaceRoots` | `string[]`, `[]` | absolute roots | Roots Perception may scan; empty denies scans. |
| `security.spellWorkspaceRoots` | `string[]`, `[]` | absolute roots | Roots spell CRUD may access; empty denies workspace spell CRUD. |
| `security.campaignRoots` | `string[]`, `[]` | absolute roots | Roots from which campaigns may be registered; empty denies registration. |
| `security.allowedUploadMimeTypes` | `string[]`, `[]` | MIME types | Optional additional upload restriction; empty adds no operator restriction. |
| `security.allowedImageMimeTypes` | `string[]`, `["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"]` | MIME types; nonempty while Scrying is enabled | MIME policy for Scrying images. |
| `workspaces.defaultRoot` | `string?`, `null` | path | Default for workspace-scoped routes. |
| `workspaces.enableFileWrite` | `bool`, `false` | — | Permits workspace create, modify, and delete routes. |

The removed approval keys are `security.ward.enabled`, `security.ward.autoDenyInUnattendedMode`, `security.ward.autoApprove.enabled`, and `security.ward.autoApprove.tools`. File/API validation and `arcanum config set` reject those names with guidance to remove them; no preset, environment override, or compatibility DTO can restore a blocking Ward prompt.

`arcanum workspace register|tree|info|read|search|index|index-status|chunks|unregister` adds no configuration keys. Those commands call the existing authenticated Workspace API and continue to honor the server's path allowlists, indexing feature gates, and `workspaces.enableFileWrite` policy. CLI path arguments describe the server host; the current-directory default is valid only for the shipping loopback client/server pairing. Campaign remains a separate persistent project container.

### Feature opt-ins

| Descriptor key | Type and default | Bounds | Semantics |
|---|---|---|---|
| `features.enterpriseTelemetry` | `bool`, `false` | — | Emits structured enterprise telemetry. |
| `features.scalarUi` | `bool`, `false` | — | Mounts the interactive Scalar API UI. |
| `features.conclave` | `bool`, `false` | — | Enables cross-Apprentice delegation. |
| `features.a2AServer` | `bool`, `false` | — | Exposes configured inbound A2A endpoints and derives Conclave availability. |
| `features.a2AClient` | `bool`, `false` | — | Permits dispatch to allowed remote A2A agents and derives Conclave availability. |
| `features.apprentices` | `bool`, `true` | — | Enables the Apprentice subsystem. |
| `features.lexicon` | `bool`, `true` | — | Enables prompt-time/model-writable Lexicon memory; attachment-derived facts require current-turn materialization and retain typed provenance. Disabling the gate does not delete entities or block authenticated `arcanum memory` inspection. |
| `features.archiveSearch` | `bool`, `true` | — | Enables search over past sessions. |
| `features.metrics` | `bool`, `true` | — | Exposes Prometheus metrics. |
| `features.embeddings` | `bool`, `false` | — | Enables The Weave embedding substrate. |
| `features.sessionSearch` | `bool`, `false` | — | Enables semantic session search and derives embedding-substrate activation. |
| `features.codebaseRetrieval` | `bool`, `false` | — | Enables semantic workspace retrieval and derives embedding-substrate activation. |
| `features.attachmentRetrieval` | `bool`, `false` | — | Enables bounded per-session semantic retrieval over eligible versioned text attachments and derives embedding-substrate activation. Command Center reports aggregate pending/completed/failed indexing and separates retrieved attachment-RAG tokens from attachment metadata. |
| `features.saga` | `bool`, `false` | — | Enables long-term associative memory retrieval and derives embedding-substrate activation. |
| `features.sagaExtraction` | `bool`, `false` | — | Enables automatic concise Saga extraction and derives Saga/substrate availability; attachment claims must match the source turn's materialized allowlist. |
| `features.tapestry` | `bool`, `false` | — | Enables **The Tapestry**: hierarchical (RAPTOR-style) summary trees woven over the workspace, session-attachment, and session-history corpora, and derives embedding-substrate activation. Trees are derived data rebuilt by a background sweep; a scope with no published generation contributes no context. Cluster summaries call the summary model, so enabling this costs tokens. |
| `features.semanticSpellRouting` | `bool`, `false` | — | Enables embedding-assisted spell routing and derives embedding-substrate activation. |
| `features.scrying` | `bool`, `true` | — | Accepts images for vision-capable models. |
| `features.attachments` | `bool`, `true` | — | Enables encrypted, versioned Session snapshots; standalone snapshot/reference/content APIs and `attachment list|add|reference|show|versions|refresh|pin|unpin|export|reveal`; direct `ask`/`chat --attachment <guid>`; `attach_session_file`; host-authorized `refresh_session_file`; and Command Center Snapshot/Live/Stale state. Snapshot add may upload any client-readable file/stdin, while reference/refresh paths are server-only and Workspace/Sanctum-authorized. Unsupported binary/PDF/Office files remain valid `NotEligible` attachments. Every path shares MIME, Scrying, measured-byte, pin, identity/provenance, and provider-context policy without version/reference count ceilings; model vision is required only when an image enters model context, not for standalone refresh. Metadata never emits bytes; export is atomic plaintext; reveal requires a locally present encrypted `ARCABLOB` snapshot. |
| `features.clientTools` | `bool`, `false` | — | Forwards client-declared tools to compatible providers. |
| `features.webBrowsing` | `bool`, `false` | — | Advertises native `web_search` / `read_url` and enables authenticated `search`, `browse`, and server-orchestrated `research` CLI workflows. The deprecated `browse_web` name remains only as a direct-invoke compatibility alias. |
| `features.reasoning` | `bool`, `true` | — | Hard gate for reasoning controls and reasoning production; enabling it does not itself request reasoning. |
| `features.reasoningSummaries` | `bool`, `false` | — | Allows declared client-safe reasoning output to reach responses; disabling it does not prevent provider-internal reasoning. |
| `features.guardrails` | `bool`, `false` | — | Runs configured input/output guardrails. |
| `features.workspaceChecks` | `bool`, `true` | — | Allows `workspace_check` advertisement when all platform and trust checks pass. |
| `features.memoryManagement` | `bool`, `false` | — | Enables session deletion, pinning, and compaction. Read-only `arcanum memory status\|sources\|search\|explain` remains available and reports the disabled mutation gate alongside retained counts. |
| `features.campaignScopedMemory` | `bool`, `false` | — | Scopes cross-session memory to the Campaign the turn resolved. Off by default, and the default is the guarantee: with it unset, Saga retrieval and Lexicon matching see exactly the candidate sets and the ordering they see today, no Session binding is read, and no scope column is consulted. Turning it on **narrows** what the model can recall, which is the point — a conclusion drawn inside one Campaign stops competing for a bounded top-K against the Campaign in front of the model, and stops contradicting it. A turn that resolves no Campaign draws on installation-scoped memory only; memory whose ownership is unresolved is recallable nowhere until an operator resolves that Session's binding. Widening it again is a configuration change, not a data change: nothing is deleted, re-scoped, or re-embedded in either direction. Saga's scoped search always ranks through the managed cosine path, so the predicate cannot be bypassed by the presence of a native vector accelerator. `arcanum memory status\|search\|explain` state which scope a turn would draw from before one runs, and `arcanum data reset-memory --campaign` clears one Campaign's Saga or Lexicon memories without touching another's. |
| `features.annals` | `bool`, `true` | — | Records ordinary Saga `AgentExtracted` and Lexicon `AgentAsserted` claims. Explicit `false` opts out only future ordinary claims; existing history remains readable, and re-enabling does not backfill the opted-out period. State-changing Saga corrections, retirements, and reinstatements remain mandatory evidence; pin/unpin remain claimless. |
| `features.covenant` | `bool`, `false` | — | Enables **The Covenant**, the durable operator-and-agent profile injected on every turn. Off by default, and the default is the guarantee: an installation that never names this key adds no Covenant prompt bytes and performs no Covenant read. Enabling it sends eligible content on **every** primary, fallback, retry, compression, and tool-loop provider attempt, and one turn may reach several configured providers. Provider logs and automatic prompt caches are outside every local erasure path and cannot be revoked by Arcanum. Compendium renders the exact shared disclosure and the resolved provider-retention help actions *before* the toggle is interactive; see [README: Covenant provider retention and deletion](../README.md#covenant-provider-retention-and-deletion). Changes take effect without a restart — a disable is visible at the next in-memory gate read, and turns already dispatched to a provider cannot be recalled. |

Feature flags are capability policy. Edition, dependency, security, provider, and platform eligibility still apply.

The standalone attachment family adds no public configuration keys or consent setting. `attachment show --privacy` is an immediate disclosure, not an acknowledgement gate. Text attachment pins may materialize implicitly within the code-owned pin/turn budgets; image pins remain durable but report `Unsupported` until a vision-capable turn explicitly names the bound GUID. Attachment export asks before overwrite (or honors global `--yes`), stages beside the destination, and never permits stdout; all other attachment commands remain metadata-only. Server-side reference authorization, MIME/content validation, and `MaxBytesPerSession` remain code-owned. The former public `MaxReferencesPerTurn` and `MaxVersionsPerLogicalKey` controls were removed: identity/ownership, inject-once/provider-context admission, and measured session bytes own those risks without count ceilings.

The native `delegate_task` subagent tool has no operator configuration key: each call requires an explicit positive token or cost budget, and completion/no-progress/cancellation applies without a turn, depth, or explicit-file-count counter. Child tools are disabled by construction. Child requests inherit no attachment context; an explicit attachment file must name an id from the parent's current-turn materialized allowlist, and each explicit path/content remains individually bounded.

### What the Covenant can hold

These are fixed bounds rather than settings — there is no key that raises them, which is why they are stated here rather than in the table above. An operator who knows them can tell the difference between Arcanum refusing something and Arcanum being full, and the refusal always says which.

The Covenant has two scopes and two lanes. **Global** applies to every turn on the installation; a **Campaign** scope applies only to turns resolved to that Campaign. Within each, the **Confirmed** lane holds what you asserted and the **Proposed** lane holds what the agent has suggested and you have not yet acted on. A turn renders your Global entries, then the active Campaign's, and only ever loads one Campaign — so what any single turn carries is Global plus one Campaign, never the whole installation.

| Section | Entries | Rendered bytes |
|---|---|---|
| Global Confirmed | 64 | 4,096 |
| Campaign Confirmed | 64 | 4,096 |
| Campaign Proposed | 32 | 4,096 |

There is one further bound, and it is the one worth understanding: **160 active entries in the pair a single turn would load.** That is exactly the three sections above added together, so an installation that fills each section to its stated maximum sits precisely on it. The bound exists because the per-section limits alone would let a Global scope and a Campaign scope each stay legal while combining into a turn load no snapshot may carry, and the failure would arrive as an integrity refusal on an ordinary turn rather than on the write that caused it. Refusing the write is the honest place to refuse.

**Editing something you already wrote is always allowed, including at the ceiling.** A write to a key that already exists replaces that entry rather than adding one, so the number of entries a turn would load is the same afterwards, and it is not charged against the bound. Only a genuinely new key is. The same is true of an agent refining a proposal it made earlier. If you are at the ceiling and want to add something new, retire an entry you no longer need — a retired entry renders nowhere and stops counting immediately, and it can be reinstated later by an explicit reactivating write rather than by proposing the key again.

When a bound is reached the refusal names it and says by how much, and nothing is written. An agent that reaches one is told the same thing in terms it can act on, and it is told **before** anything is staged, so a full Covenant costs the suggestion and never the answer you asked for.

### Integrations, execution, cost, retention, daemon, and CLI

| Descriptor key | Type and default | Bounds | Semantics |
|---|---|---|---|
| `integrations.a2A.serverPath` | `string`, `"/api/conclave/a2a"` | any path | Inbound A2A endpoint and Agent Card path. A value outside `/api` is mounted under it (`/conclave/a2a` → `/api/conclave/a2a`) so the API-key boundary always applies; the effective path is reported by `arcanum conclave status`. |
| `integrations.a2A.agentCardName` | `string?`, `null` | — | Advertised identity name. |
| `integrations.a2A.agentCardDescription` | `string?`, `null` | — | Advertised identity description. |
| `integrations.a2A.allowedRemoteAgents` | `string[]`, `[]` | URLs or origins | Optional **outbound** allowlist for `dispatch_sending` targets; empty adds no allowlist beyond the outbound URL guard. Every interface the remote Agent Card advertises is checked, not just the first. |
| `integrations.a2A.defaultWorkspace` | `string`, `""` | path | Fallback for inbound tasks; empty falls through to `workspaces.defaultRoot`, then the current directory. |
| `integrations.a2A.outboundCredentialEnvironmentVariable` | `string`, `""` | env var name | Environment variable holding the credential presented to remote agents. Empty sends none, so only unauthenticated peers are reachable — including *not* another Arcanum. The value never lives in configuration, and it is sent only to the origin you named or an allowlisted target, never to a host the remote Agent Card picks. |
| `integrations.a2A.outboundCredentialHeader` | `string`, `"X-Arcanum-Key"` | header name | Header carrying that credential. Use `Authorization` (with a `Bearer …` value) for agents expecting bearer auth. |
| `integrations.a2A.inputModes` | `string[]`, `[]` | media types | Content types inbound Sendings may carry. Empty advertises `text/plain`. This is also what an **outbound** Sending asks a peer to answer in when the caller states no preference — an instance that can only ingest one modality cannot read a peer's answer in another either. |
| `integrations.a2A.outputModes` | `string[]`, `[]` | media types | Content types this instance can answer with. Empty advertises `text/plain`. A peer whose `acceptedOutputModes` intersect none of these is **rejected by name** rather than silently answered as text. |
| `integrations.a2A.skills[].id` | `string?`, `null` | non-empty, unique | Advertised Agent Card skill identifier peers match on. An empty skill list advertises the single historical `apprentice-goal-execution` skill, so a default card is unchanged. A declared skill without an id fails validation rather than vanishing from the card. |
| `integrations.a2A.skills[].name` | `string?`, `null` | — | Display name for the skill; defaults to its id. |
| `integrations.a2A.skills[].description` | `string?`, `null` | — | What the skill does, shown to other agents. |
| `integrations.a2A.skills[].inputModes` | `string[]`, `[]` | media types | Per-skill inbound content types; empty inherits `integrations.a2A.inputModes`. |
| `integrations.a2A.skills[].outputModes` | `string[]`, `[]` | media types | Per-skill outbound content types; empty inherits `integrations.a2A.outputModes`. |
| `integrations.a2A.pushNotifications` | `bool`, `false` | — | Enables the A2A push-notification surface in both directions: inbound, peers may register a callback and receive task-state transitions; outbound, a Sending may be dispatched in callback mode so it stops holding a concurrency slot while the remote works. The Agent Card advertises `pushNotifications` only when this is on. A peer-supplied callback URL is checked against the outbound URL guard **and** `allowedRemoteAgents`, at registration and again at every delivery. |
| `integrations.a2A.pushCallbackBaseUrl` | `string`, `""` | absolute http(s) origin | Externally reachable base URL peers post outbound-Sending callbacks to, e.g. `https://arcanum.example.com`. Empty means callback mode is unavailable — this instance has no way to tell a peer where to reach it — and a `--callback` dispatch simply waits inline instead. The callback path itself is derived from `serverPath`. |
| `integrations.commLink.webhookUrlEnvironmentVariable` | `string?`, `null` | portable, case-insensitively unique environment name | Exact secret-bearing webhook reference; omission uses `ARCANUM_COMMLINK_WEBHOOK_URL`. |
| `integrations.commLink.allowedSchemes` | `string[]`, `["https"]` | URI schemes | Allowed webhook schemes. |
| `integrations.commLink.allowedHosts` | `string[]`, `[]` | hostnames | Optional webhook host allowlist; empty adds no host restriction beyond the outbound URL guard. |
| `integrations.embeddings.provider` | `string?`, `null` | configured provider name | Provider used for embeddings. |
| `integrations.embeddings.model` | `string?`, `null` | provider-advertised model | Embedding model ID. |
| `integrations.embeddings.dimensions` | `int`, `768` | 64–4,096 | Expected vector dimensions. Changing this value requires clearing and re-indexing embeddings or reinstalling the local database. |
| `integrations.embeddings.tapestry.retrievalMode` | `enum`, `CollapsedTree` | `CollapsedTree`, `TreeTraversal` | How The Tapestry reads its trees. `CollapsedTree` searches leaf and summary nodes as one pool; `TreeTraversal` starts at the tree's roots and expands only the selected nodes' children. |
| `integrations.embeddings.tapestry.summaryModel` | `string?`, `null` | configured model name | Model used to write Tapestry cluster summaries. Blank falls back to `fastModel`, then `defaultModel`. Summary calls are priced, reserved, and audited like any other model call. |
| `integrations.mcp.allowedHttpHosts` | `string[]`, `[]` | hostnames | Explicit plaintext-HTTP MCP exceptions; empty permits none and HTTPS remains the default. |
| `integrations.webResearch.searchProvider` | `string`, `"perplexity"` | nonblank registered provider name | Provider used by `web_search`. |
| `integrations.webResearch.perplexityModel` | `string`, `"sonar"` | `sonar` or `sonar-pro` | Perplexity Sonar model used for synthesized search. |
| `integrations.webResearch.credentialEnvironmentVariable` | `string?`, `null` | portable, case-insensitively unique environment name | Exact Perplexity credential reference. Omission checks `ARCANUM_PERPLEXITY_API_KEY`, then the secure local provider-key store. |
| `integrations.workspaceChecks.executableCatalog.dotNet.path` | `string`, `""` | canonical absolute path | Optional trusted native `dotnet`; empty delegates to trusted runtime resolution. |
| `integrations.workspaceChecks.customProfiles` | `Dictionary<string, WorkspaceCheckProfileSettings>`, `{}` | closed shape described below | Case-insensitive operator-authored build, test, and lint profiles; models never supply raw commands. |
| `execution.maxConcurrentApprentices` | `int`, `5` | 1–50 | Host-wide concurrent Apprentices. |
| `execution.maxConcurrentApprenticeBranches` | `int`, `3` | 1–64 | Concurrent Simulacrum branches within an Apprentice. |
| `execution.maxConcurrentA2ATasks` | `int`, `50` | 1–500 | Simultaneous outbound A2A delegations; excess work waits cancellably for capacity rather than being rejected. |
| `execution.maxSseConnections` | `int`, `50` | 1–100 | Global live-event connection capacity. |
| `execution.maxSseConnectionsPerType` | `int`, `20` | 1–50 | Per-stream-family fairness capacity. |
| `execution.maxConcurrentBatches` | `int`, `3` | 1–20 | Concurrent OpenAI-compatible batches. |
| `execution.maxConcurrentRequestsPerBatch` | `int`, `1` | 1–10 | Per-batch request concurrency. |
| `cost.pricing.defaultPricing.inputPer1M` | `decimal`, `0` | 0–1,000,000 | Fallback USD per million input tokens. |
| `cost.pricing.defaultPricing.outputPer1M` | `decimal`, `0` | 0–1,000,000 | Fallback USD per million output tokens. |
| `cost.pricing.defaultPricing.reasoningPer1M` | `decimal?`, `null` | 0–1,000,000 | Optional reasoning rate; `null` uses output pricing and explicit `0` is free. |
| `cost.pricing.defaultPricing.cachedPer1M` | `decimal`, `0` | 0–1,000,000 | Fallback USD per million cached-input tokens. |
| `cost.pricing.modelPricing` | `Dictionary<string, ModelPricingEntry>`, `{}` | entry rates 0–1,000,000 | Case-insensitive model-name overrides with the shape described below. |
| `cost.budget.enabled` | `bool`, `false` | — | Rejects new inference after the UTC-day limit is reached. |
| `cost.budget.dailyLimitUsd` | `decimal`, `0` | 0–1,000,000 | Maximum UTC-day spend when enforcement is enabled. |
| `retention.automaticSweepsEnabled` | `bool`, `false` | — | Opts in to scheduled policy sweeps; status, dry-run planning, and explicit item-scoped deletion remain available when disabled. |
| `retention.sweepIntervalHours` | `int`, `24` | 1–168 | Interval between automatic sweep attempts when scheduling is enabled. |
| `retention.accountingMinimumDays` | `int`, `365` | 30–3,650 | Minimum effective retention for inference runs, billable operations, budget reservations, and cost adjustments, regardless of the accounting rule's shorter requested period. |
| `retention.protectedSessionIds` | `Guid[]`, `[]` | comma-separated GUIDs in Compendium | Explicit operator holds. Every value is validated as a GUID before save; a held session remains visible as a blocked plan candidate and is not deleted. |
| `retention.activeSessions.enabled` | `bool`, `false` | — | Makes old active sessions eligible for policy sweeps; explicit deletion remains a separate confirmed operation. |
| `retention.activeSessions.days` | `int`, `365` | 1–3,650 | Age threshold for active sessions. |
| `retention.archivedSessions.enabled` | `bool`, `false` | — | Makes old archived sessions eligible for policy sweeps. |
| `retention.archivedSessions.days` | `int`, `180` | 1–3,650 | Age threshold for archived sessions. |
| `retention.entries.enabled` | `bool`, `false` | — | Enables policy retention for unpinned session Entries; pins and owning-session conflicts remain blockers. |
| `retention.entries.days` | `int`, `180` | 1–3,650 | Age threshold for eligible Entries. |
| `retention.attachments.enabled` | `bool`, `false` | — | Enables policy retention for attachment versions together with their owned bytes, chunks, embeddings, and index state. |
| `retention.attachments.days` | `int`, `180` | 1–3,650 | Age threshold for eligible attachment versions. |
| `retention.uploadedFiles.enabled` | `bool`, `true` | — | Enables policy retention for uploaded files and batch input/output/error file roles; retained or in-progress batch references block deletion. |
| `retention.uploadedFiles.days` | `int`, `30` | 1–3,650 | Age threshold for unreferenced uploaded files. |
| `retention.completedBatches.enabled` | `bool`, `true` | — | Enables policy retention for terminal completed, failed, cancelled, or expired batch rows. |
| `retention.completedBatches.days` | `int`, `30` | 1–3,650 | Age threshold for terminal batches. |
| `retention.sagaMemories.enabled` | `bool`, `false` | — | Enables policy retention for Saga memories; deleting only a source attachment does not silently delete an independently retained memory. |
| `retention.sagaMemories.days` | `int`, `365` | 1–3,650 | Age threshold for Saga memories. |
| `retention.lexiconEntries.enabled` | `bool`, `false` | — | Enables policy retention for Lexicon entries; deleting only a source attachment preserves the fact and marks its typed provenance unavailable. |
| `retention.lexiconEntries.days` | `int`, `365` | 1–3,650 | Age threshold for Lexicon entries. |
| `retention.workspaceIndexes.enabled` | `bool`, `true` | — | Enables policy retention for workspace chunks and their corresponding embeddings. |
| `retention.workspaceIndexes.days` | `int`, `30` | 1–3,650 | Age threshold for workspace-derived index records. |
| `retention.sessionEntryEmbeddings.enabled` | `bool`, `true` | — | Enables policy retention for derived session Entry embeddings. |
| `retention.sessionEntryEmbeddings.days` | `int`, `30` | 1–3,650 | Age threshold for session Entry embeddings. |
| `retention.auditLogs.enabled` | `bool`, `true` | — | Enables unified retention planning for dated inference-audit JSONL files. Only the bounded durable retention service/scheduler deletes them; the logger never does. |
| `retention.auditLogs.days` | `int`, `30` | 1–3,650 | Age threshold for dated inference-audit files. |
| `retention.guardrailLogs.enabled` | `bool`, `true` | — | Enables unified retention planning for dated guardrail-audit JSONL files. Only the bounded durable retention service/scheduler deletes them; the logger never does. |
| `retention.guardrailLogs.days` | `int`, `30` | 1–3,650 | Age threshold for dated guardrail-audit files. |
| `retention.idempotencyClaims.enabled` | `bool`, `true` | — | Enables policy retention for terminal idempotency claims; active leases are never eligible. |
| `retention.idempotencyClaims.days` | `int`, `7` | 1–3,650 | Age threshold for terminal idempotency claims. |
| `retention.accounting.enabled` | `bool`, `true` | — | Enables policy retention for authoritative accounting chains, subject to the accounting floor and outstanding-reservation blockers. `Sessions.TotalCostUsd` is not accounting authority. |
| `retention.accounting.days` | `int`, `365` | 1–3,650; effective value is at least `accountingMinimumDays` | Requested age threshold for accounting rows. |
| `retention.longRunningOperations.enabled` | `bool`, `true` | — | Enables policy retention for terminal durable-operation history; active and repair-required operations remain conflicts. |
| `retention.longRunningOperations.days` | `int`, `90` | 1–3,650 | Age threshold for terminal operation history. |
| `retention.sanctumBreaches.enabled` | `bool`, `true` | — | Enables unified policy retention for durable Sanctum breach history. |
| `retention.sanctumBreaches.days` | `int`, `90` | 1–3,650 | Age threshold for Sanctum breach rows. |
| `retention.daemonHistory.enabled` | `bool`, `true` | — | Enables age pruning for terminal daemon execution summaries; active executions remain protected. The current history store is process-local and count-bounded, so restart also clears it. |
| `retention.daemonHistory.days` | `int`, `30` | 1–3,650 | Age threshold for terminal entries in the current process-local daemon history. |
| `daemon.maxConcurrentJobs` | `int`, `8` | 1–1,024 | Concurrent Unseen Servant jobs. |
| `daemon.jobs.name` | `string`, `""` | nonblank; unique case-insensitively | Human-readable schedule name. It is also the job's daemon id (`unseen-servant:<name>`), so a blank or duplicate name fails startup: the registry resolves an id to its first match, which would leave a twin permanently unrunnable and misattribute its history. |
| `daemon.jobs.intervalMinutes` | `int`, `60` | 1–10,080 | Minutes between runs. |
| `daemon.jobs.targetSpell` | `string`, `""` | valid spell | Spell invoked on each tick. |
| `daemon.jobs.enabled` | `bool`, `true` | — | Makes the schedule eligible to run. |
| `cli.theme` | enum `"SystemDefault"` | `Light`, `Dark`, `SystemDefault` | CLI color theme. |
| `cli.showManaBar` | `bool`, `true` | — | Shows the chat token-budget indicator. |

A constraint reduction removed the former audit-query lookback fields, embedding watcher/attachment indexing mechanics, pending-Apprentice queue size, and retention candidate/checkpoint counts from the public schema. Audit deletion is governed only by unified retention. Watcher, extraction, chunking, queue, retry, retrieval-slice, and checkpoint values now protect internal work slices and continue through reconciliation. `execution.maxConcurrentApprentices` still protects simultaneous host load, while additional starts queue and remain cancellable instead of failing at a public pending-count limit. Retention still honors the operator's age/hold policy and uses an internal durable checkpoint size until the complete selected plan finishes. The managed embedding fallback likewise has no public or code-owned total row budget: when sqlite-vec is absent, it streams the complete matching corpus with cancellation and bounded top-K memory.

Web workflow policy is per invocation, not retained configuration. `arcanum search --count` remains one provider-request result shape. Research accepts an optional positive `--sources` target, a positive explicit synthesis-token budget, and an optional nonnegative cost policy; it has no hop counter, and its fetch phase is bounded by a code-owned ceiling of 50 sources whenever the invocation names no `--sources` target, an explicit target being honoured as written. Passes continue while they add unique URLs and stop at the target, deterministic source exhaustion/no-progress, cancellation, explicit policy, or a provider/safety failure. Freshness is `day`, `week`, `month`, or `year`; include/exclude domain lists and each provider frame/body remain allocation-safe. Static URL reads retain SSRF, redirect-origin/DNS, connection/idle-I/O, body/frame, and content protections without a whole-operation wall-clock ceiling. No configuration switch implies that JavaScript rendering exists: `--render javascript` returns an explicit unavailable-renderer error until a server renderer is installed.

Compendium edits MCP transport policy but does not operate server lifecycle or approve a workspace-local `mcp.json`. Use `arcanum mcp trust [workspace]`, then `arcanum mcp list|show|start|stop|restart|reload|tools`; use `arcanum mcp invoke` only for external diagnostic tools and `arcanum tool list|show|invoke` for the bounded built-in diagnostic registry. These CLI families call the authenticated host APIs and never expose MCP command lines, environment, URLs, or secrets.

The direct-command flags `--json`, `--plain`, and `--yes` are intentionally not configuration keys and are not editable in Compendium. They are per-invocation automation authority: `--json`/`--plain` override theme and mana-bar rendering for that process, while `--yes` approves only that command's confirmation prompts. Persisting any of them would make interactive and destructive behavior surprising.

The unified `arcanum watch session|apprentice|logs|mcp|daemons|health` surface likewise adds no configuration keys. Recursive `--json`, opt-in `--reconnect`, repeatable free-form `--event-type`, repeatable free-form `--tool` / `--tool-name`, log `--level` / `--category` / `--search`, Session `--since`, and health `--interval` are invocation-only choices. Compendium does not impose a fixed event/tool allowlist, reconnect-attempt count, or additional polling restriction: reconnect runs until completion/cancellation with a code-owned capped backoff, and health accepts any positive whole-second interval (default five). Existing `execution.maxSseConnections` and `execution.maxSseConnectionsPerType` remain the server admission controls; every watcher still uses normal API authentication. A valid Unhealthy 503 health envelope is observable data, while SSE reconnect always warns of a possible gap and never promises replay.

For logs, category and search are free-form. Level keeps the API's existing nullable `LogLevel` contract: `trace`, `debug`, `information`, `warning`, `error`, or `critical`; Compendium adds no second severity policy.

The same per-invocation rule applies to `arcanum context inspect|tools|sources|cost`: `--show-content` is an explicit one-run operator reveal and `--no-retrieval` is a one-run request to skip embedding/RAG work. Compendium does not persist either switch. Use these commands after editing model, tool, Spell, retrieval, or context-window settings to verify the effective provider, tool surface, source-token allocation, reserve, and compression decision before spending main-inference tokens.

`arcanum file upload|list|show|download|delete` and `arcanum batch create|list|show|watch|cancel|reset|output|errors` add no configuration keys. They call the existing authenticated OpenAI-compatible routes and inherit `security.allowedUploadMimeTypes`, the code-owned upload ceiling, `execution.maxConcurrentBatches`, and `execution.maxConcurrentRequestsPerBatch`. Local JSONL preflight checks only the obvious batch wrapper before upload; server validation remains authoritative. Total request count, internal 64-line pages, and durable per-line dispatch/result checkpoints are code-owned rather than user restrictions. Restart skips completed lines and reports an uncertain dispatched line as `batch_interrupted_after_dispatch` without replaying it. Download overwrite approval is a per-invocation `--yes` decision and is never persisted by Compendium.

### Dynamic dictionary shapes

`cost.pricing.modelPricing` is keyed case-insensitively by model name. Every value has exactly the same rate fields and bounds as `defaultPricing`:

```json
{
  "<model-name>": {
    "inputPer1M": 0,
    "outputPer1M": 0,
    "reasoningPer1M": null,
    "cachedPer1M": 0
  }
}
```

`integrations.workspaceChecks.customProfiles` is keyed case-insensitively by profile ID and has this recursively validated shape:

```json
{
  "<profile-id>": {
    "executableId": "dotnet",
    "kind": "build",
    "parser": "msBuild",
    "target": "",
    "fixedArguments": ["build", "--no-restore"],
    "options": {
      "<option-id>": {
        "allowedValues": {
          "<value-id>": ["--configuration", "Release"]
        }
      }
    }
  }
}
```

The current closed-profile limits are 32 profiles, 32 fixed arguments per profile, 16 options per profile, 16 allowed values per option, and 32 argument tokens per allowed-value rendering. Profile and option IDs are 1–64 lowercase ASCII letters, digits, or hyphens, start with a letter or digit, and are unique case-insensitively; built-in profile IDs are reserved. Value IDs are nonblank, case-insensitively unique, and at most 256 characters. Argument tokens are nonblank, single-line, and at most 256 characters; response files, scripts, shells, restore-enabling arguments, and runtime-owned path overrides are rejected. `target` is empty or a workspace-relative `.sln`, `.slnx`, `.csproj`, `.fsproj`, or `.vbproj` path of at most 256 characters. `kind`, `parser`, and the first fixed argument must agree: `build`/`msBuild`/`build`, `test`/`vsTest`/`test`, or `lint`/`dotNetFormat`/`format` with `--verify-no-changes`.

### Credential environment references

Secret values are not configuration fields:

- `providers.credentialEnvironmentVariable` names the provider API-key variable. Omission derives `ARCANUM_PROVIDER_<NORMALIZED_NAME>_API_KEY`. When the referenced variable is unset, Arcanum falls back to the OS-backed secure store written by `arcanum setup` or `arcanum key provider set <provider>`; Compendium never reads or writes the credential itself.
- `host.https.certificatePasswordEnvironmentVariable` names the PFX-password variable. Omission uses `ARCANUM_HTTPS_CERTIFICATE_PASSWORD`; PEM ignores this reference.
- `integrations.commLink.webhookUrlEnvironmentVariable` names the secret-bearing webhook URL variable. Omission uses `ARCANUM_COMMLINK_WEBHOOK_URL`.

An explicit reference replaces its default and does not fall through when unset. Provider-name normalization retains ASCII letters and digits, upper-cases letters, collapses non-alphanumeric runs to `_`, and uses `UNNAMED` when empty. Provider names must be nonblank, and all final provider, PFX, and CommLink reference names must be portable and unique case-insensitively. Compendium never reads or displays a referenced secret value.

### Minimal complete `arcanum.json`

```json
{
  "Arcanum": {
    "edition": "local",
    "defaultModel": "gpt-4o-mini",
    "providers": [
      {
        "name": "OpenAI",
        "type": "OpenAICompatible",
        "endpoint": "https://api.openai.com/v1",
        "credentialEnvironmentVariable": "OPENAI_API_KEY",
        "models": [
          {
            "name": "gpt-4o-mini",
            "supportsVision": false
          }
        ],
        "contextWindowLimit": 128000
      }
    ]
  }
}
```

Set `OPENAI_API_KEY` in the host environment; never place its value in the file.

## Editor architecture

The Presets, Host, Providers, Daemon, and CLI pages are polished views. Edition, Security, Workspaces, Features, Integrations, Execution, Cost, and Retention use the descriptor-driven generic view.

`SettingDescriptors` contains only editable public choices. Every descriptor is rendered by one of those views. Descriptor coverage tests recurse through the public graph, treat authored dictionaries as one editor, and verify public mutable setters plus `ConfigurationJsonContext` metadata.

Provider/model rows and daemon schedules have structured editors. Pricing maps and custom workspace-check profiles use multiline JSON editors backed by the source-generated configuration JSON context. Allowlists use chip editors. `retention.protectedSessionIds` uses the same comma-separated editor while converting each value to a `Guid`; the stored contract remains a typed `Guid[]`.

A chip editor's entry box commits on `Enter`, on **Add**, and on losing focus, so a value typed but never explicitly added still reaches the list when the operator presses Save, opens another section, or closes the window. Text sitting in that box marks the editor dirty before it becomes a chip, which is what keeps Save reachable and makes the close confirmation ask about it. Every chip editor behaves this way, including the CORS origins editor on the Host page.

A descriptor whose key crosses a collection describes every element rather than one path — for example `integrations.a2A.skills.id` describes each entry of `integrations.a2A.skills`. Sections whose collections have a structured editor (providers, models, daemon schedules) bind those descriptors per row. Anywhere else the generic view renders the descriptor read-only with its label, description, and the `arcanum config set integrations.a2A.skills.0.id <value>` form that does write it, because an editable box there would accept text no save could apply. `GenericSettingsSectionViewTests.Every_input_control_the_generic_editor_renders_addresses_a_settable_path` pins that: every control the generic view offers addresses a path Save can write.

`ConfigurationViewModel` keeps the last loaded settings as a snapshot. Polished pages rebuild only their owned records; generic edits clone and update only the selected public property path. Unopened sections and provider facts therefore survive load/edit/save unchanged. `PresetsSectionViewModel` stays outside dirty tracking, delegates all catalog/diff/apply/reset behavior to the shared service, and reloads the root snapshot only after a successful preset mutation. It clears the now-stale plan immediately after that mutation, so failed reload cannot leave an old diff enabled against a newer file.

## Secrets and HTTPS

The configuration fields, defaults, and resolution rules for secret references are defined in [Credential environment references](#credential-environment-references). Compendium never resolves referenced secret values.

The Host page generates an owner-only self-signed loopback PEM certificate/key pair under `~/.config/arcanum/certs/`, so generated local HTTPS needs no stored password. Collision-resistant names preserve every pair even when several are generated in one second. Certificate and key bytes are durable-flushed to owner-only staging files, moved without overwrite, and removed as a pair if publication cannot finish. The editor preserves a valid operator-selected HTTPS port and does not install OS trust. External binding still requires HTTPS and a certificate valid for the remote hostname/IP; TLS validation must not be disabled.

## Portable backup ownership

Portable backup is owned by the `arcanum backup` CLI and shared backup services; Compendium does not implement a second archive format or a restore UI. The `full` and `configuration-and-authored-assets` scopes include the shared `arcanum.json` configuration and the `~/.config/arcanum/certs/` tree when present. Explicitly selecting `compendium-settings` while excluding `configuration` still captures `arcanum.json` under the Compendium component. When both components are selected, the archive stores one `Configuration` entry and records `CompendiumSettings` as a complete zero-entry alias rather than duplicating the settings bytes. Compendium certificates remain their own typed component.

The `.arcbackup` manifest and certificate/private-key bytes remain inside the authenticated encrypted payload. Environment-referenced secret values are not resolved or exported, raw Data Protection/OS credential stores are excluded, and the master API key is absent unless explicitly selected as a sensitive component. Global MCP configuration is authored state and may contain literal environment values, so the backup planner surfaces a warning when it is selected.

Restoring is likewise owned by the CLI: `arcanum backup restore` moves the configuration, certificates, database, and blobs as one generation, and Compendium implements no restore UI of its own. Restart Arcanum afterwards so the restored configuration snapshot is loaded. Referenced environment secrets and external workspace paths must still be supplied separately on the target, and restored certificates may not match or be trusted for a different hostname. Verify an archive before depending on it, and prefer `arcanum backup restore --dry-run` first: it validates the whole archive and reports the plan without touching the installation.

## Saving and validation

Descriptor validation runs in the editor as values change and blocks Save: an out-of-bounds, malformed, or otherwise rejected value is rendered inline beneath its own control, Save is unavailable while any field is invalid, and an attempt to save anyway names every offending descriptor key in an `Invalid settings` dialog. No invalid field is silently dropped from the written file. The JSON dictionary editors — pricing model overrides and custom profiles — parse their text against the source-generated configuration context before Save and report a per-field parse error in the same place.

Save runs `ConfigurationValidator`, rejects configuration files larger than the code-owned 10 MiB ceiling before JSON parsing, then takes the same current-user cross-process configuration transaction as preset and CLI writers. Inside that transaction, its existing local save lock serializes the owner-only temporary write, durable flush, atomic `arcanum.json` replacement, fingerprint acknowledgement, and staging cleanup. Host/API loading performs a source-generated fail-closed walk before binding, grouping all unknown paths while preserving pricing and workspace-check dictionary keys. Validation pointers use the same dot paths as descriptors, and the pointer-keyed error surface is the union of the editor's field errors and the errors returned by the last write attempt. Every unsuccessful save raises a dialog and leaves its message in the SaveBar, including a write refused by the fingerprint check, so nothing fails silently behind the unsaved-changes text. The next edit retires that verdict — the errors returned by the last write attempt and its SaveBar message are both cleared — because it described a settings snapshot that no longer exists; the editor's own field errors are recomputed as values change and are unaffected.

Re-applying owner-only permissions to `arcanum.json` happens after the replacement has already committed, so a failure there is reported as a successful save carrying a warning rather than as a failed one: the file on disk holds the new content, the fingerprint is acknowledged, the editor leaves the dirty state, and a `Saved with a warning` dialog names the path whose permissions could not be tightened. Restricting the configuration directory is likewise advisory during a save and at startup — Compendium reports what it could not restrict instead of refusing to save or refusing to open a window, and a store whose external-change watcher cannot be started still reads and saves without one.

A read that fails leaves the editor bound to fabricated defaults rather than the operator's values, so it fails closed: Save is disabled, the section tabs are disabled, and the SaveBar shows a persistent `Could not read arcanum.json - repair the file or Reload.` banner with Reload always reachable. The store independently records an unreadable fingerprint, so a write over a never-successfully-loaded configuration is refused at the store layer with a message explaining that saving would replace the file with default settings.

Cancel restores the in-memory snapshot and discards local edits; it does not clear an on-disk-change block, which only a Reload resolves. Reload re-reads disk after confirmation when local edits exist, and closing the window with unsaved edits — close button, `Cmd+W`, or `File > Exit` — prompts `Discard` or `Keep editing` in the same way. A file watcher reports external changes and blocks overwriting them until reload. Each successful read or ordinary Save acknowledges a SHA-256 fingerprint of the exact configuration bytes it loaded or wrote. A delayed watcher event matching that fingerprint is therefore recognized as the same edit—including a preset transaction followed by its successful Compendium reload—while any different bytes still surface as an external change. Arcanum loads the source-generated configuration snapshot at process start; restart the host after saving changes.

Preset apply/reset uses the shared preset transaction rather than this ordinary Save path. It serializes mutations, rejects a stale settings hash, validates the complete candidate with canonical semantic/outbound policy, maintains owner-only state/rollback/journal sidecars, atomically replaces the file, and verifies or conditionally reverses its owned changes. The journal contains only owned before/after values and hashes plus previous/next provenance, and recovery preserves unrelated or later manual edits. Sidecar reads are bounded/no-follow and provenance must exactly match the immutable catalog and paired state. Compendium never creates a second persistence format.

`arcanum config edit` provides the same safety boundary for an operator-configured text editor: it writes an owner-only redacted temporary wrapper, waits for `$VISUAL`, `$EDITOR`, or the platform editor, restores unchanged masks, validates the complete result, and only then invokes the API or atomic local writer. A parse, validation, editor, or write failure leaves the valid configuration file unchanged.

## Build and test

```bash
dotnet run --project src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
```

Compendium is not Native AOT-published, but it edits the same source-generated, Native-AOT-compatible configuration contract used by Arcanum.
