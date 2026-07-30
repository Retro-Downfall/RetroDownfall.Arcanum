# Compendium

Compendium is Arcanum's desktop `arcanum.json` editor. It is a .NET 10 Avalonia
application (`RetroDownfall.Compendium.Ux`) and does not run inference, open the
Grimoire database, execute tools, manage the daemon, or perform blob migration/key rotation.
Persistence operators use `arcanum data encryption status|migrate|verify|rotate-key`; those
code-owned safety limits are intentionally not editable configuration.

## Documentation authority

This file is the only complete public configuration listing. The other canonical
documents are [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md) for architecture and API contracts,
[`Arcanum.README.md`](Arcanum.README.md) for agent/operator orientation,
[`Arcanum.Design.Human.md`](Arcanum.Design.Human.md) for conceptual navigation,
and [`Arcanum.DEBUGGING.md`](Arcanum.DEBUGGING.md) for verified breakpoint and recipe guides.
Configuration changes update this reference, `SettingDescriptors`, validation,
source-generated metadata, and the editor together; other docs link here instead
of reproducing the complete key table.

## Launch and configuration file

The Forge opens Compendium from **View → Open Compendium**, The Anvil, the setup
wizard, and disabled-feature guidance. Discovery checks installed binaries first
and then the development project. If launch fails, The Forge shows the exact
configuration path for manual editing.

Compendium edits:

- macOS/Linux: `~/.config/arcanum/arcanum.json`
- Windows: `%USERPROFILE%\.config\arcanum\arcanum.json`

Paths are resolved through `ArcanumPaths`; service code does not use
platform-specific path literals.

## Public configuration contract

Compendium exposes genuine deployment choices, provider/model facts,
credential or secret references, security policy, integration endpoints and
allowlists, feature opt-ins, operator schedules, host-capacity choices, pricing
facts, and user preferences.

The navigation is intentionally limited to:

1. **Edition** — runtime hardening mode.
2. **Host** — port, CORS, external HTTPS binding, certificate selection,
   inference-audit policy, and buffered-log level.
3. **Providers & Models** — `DefaultModel`, `FastModel`, provider endpoint and
   credential environment-variable reference, model inventory, vision, context
   capacity, and factual reasoning capabilities/wire dialect.
4. **Security** — Ward and guardrail policy, unsafe-process acknowledgement,
   metrics authentication, distinct Perception/Spell/Campaign roots, and upload
   and image MIME allowlists.
5. **Workspaces** — default root and explicit write permission.
6. **Features** — flat capability opt-ins, including Conclave/A2A, Apprentices,
   embeddings, Saga, Scrying, attachments, browsing, guardrails, workspace
   checks, and memory management.
7. **Integrations** — A2A identity/allowlist, CommLink webhook environment
   reference and allowlists, embedding provider/model facts, MCP plaintext-host
   policy, trusted workspace-check executable, and custom profiles.
8. **Execution** — operator-controlled host concurrency and backpressure for
   Apprentices, SSE streams, and batches.
9. **Cost** — default/per-model pricing and daily budget policy.
10. **Daemon** — Unseen Servant schedules and host concurrency.
11. **CLI** — built-in theme selection and mana-bar preference.

## Complete configuration reference

This is the sole complete documentation reference for `arcanum.json`. The 108
rows below correspond one-for-one to the current entries in
`SettingDescriptors.All`. Each key is the descriptor's exact camel-case dot
path; `providers.models.*` applies to every model in every provider, and
`daemon.jobs.*` applies to every job. JSON nests these paths beneath an exact
top-level `"Arcanum"` object.

Arcanum does not flatten arbitrary environment variables into the configuration
tree. `ARCANUM_EDITION` and `ARCANUM_HOST_ANY` are explicit runtime overrides;
secret values use only the dedicated environment references documented below.

### Edition, host, providers, and model selection

| Descriptor key | Type and default | Bounds | Semantics |
|---|---|---|---|
| `edition` | enum `"local"` | `local`, `development` | Runtime hardening mode; `ARCANUM_EDITION` overrides it. Development-only surfaces still require their companion startup flags. |
| `host.port` | `int`, `5001` | 1–65,535 | Loopback HTTP port. |
| `host.corsAllowedOrigins` | `string[]`, `["http://localhost:5001", "http://127.0.0.1:5001", "http://localhost:3000", "http://127.0.0.1:3000"]` | — | Browser origins allowed to read keyed responses. |
| `host.listenAny` | `bool`, `false` | — | All-interface binding is HTTPS-only and also forces rate limiting and metrics authentication; `ARCANUM_HOST_ANY` overrides it. |
| `host.auditLog.enabled` | `bool`, `false` | — | Enables the append-only inference audit trail. |
| `host.auditLog.retentionDays` | `int`, `7` | 1–365 | Compliance retention for dated inference-audit files. |
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
| `providers.type` | enum `"OpenAICompatible"` | `OpenAICompatible` | Provider wire contract; Ollama uses its OpenAI-compatible `/v1` endpoint. |
| `providers.endpoint` | `string`, `""` | absolute HTTP(S) endpoint | OpenAI-compatible base endpoint. |
| `providers.credentialEnvironmentVariable` | `string?`, `null` | portable, case-insensitively unique environment name | Exact API-key reference; omission derives `ARCANUM_PROVIDER_<NORMALIZED_NAME>_API_KEY`. |
| `providers.models.name` | `string`, `""` | nonblank; unique within provider | Provider-advertised model ID. A bare string model entry is also accepted. |
| `providers.models.supportsVision` | `bool`, `false` | — | Declares image-content support. |
| `providers.models.reasoning.controlSupport` | enum `"none"` | `none`, `effort`, `budget`, `effortAndBudget` | Explicit supported reasoning controls. |
| `providers.models.reasoning.supportsSummary` | `bool`, `false` | — | Declares client-safe summary output. |
| `providers.models.reasoning.supportsFull` | `bool`, `false` | — | Declares client-safe full output; never authorizes protected-reasoning disclosure. |
| `providers.models.reasoning.supportsStreaming` | `bool`, `false` | — | Declares incremental client-safe reasoning output. |
| `providers.models.reasoning.reportsReasoningTokens` | `bool`, `false` | — | Usage reports reasoning as a completion-token subset. |
| `providers.models.reasoning.allowsClientOutput` | `bool`, `false` | — | Permits projection of declared client-safe reasoning. |
| `providers.models.reasoning.wireDialect` | enum `"standard"` | `standard`, `openRouter`, `topLevelReasoningBudget`, `anthropicThinking` | Exact request shape; never inferred from names. |
| `providers.models.reasoning.maxBudgetTokens` | `int?`, `null` | 1–2,097,152 | Optional numeric-budget ceiling; requires budget support and a compatible nonstandard dialect. |
| `providers.contextWindowLimit` | `int`, `8192` | 256–2,097,152 | Factual provider context capacity. |

`providers` defaults to `[]`; a usable configuration supplies at least one
valid provider and model. A model's optional `reasoning` object defaults to
`null`.

### Security and workspaces

| Descriptor key | Type and default | Bounds | Semantics |
|---|---|---|---|
| `security.allowUnsandboxedToolChildren` | `bool`, `false` | — | Explicitly permits process tools without an OS filesystem jail where that escape hatch is supported; it does not bypass Sanctum denials. |
| `security.metricsRequireApiKey` | `bool`, `true` | — | Requires authentication for loopback metrics; external binding always forces it. |
| `security.ward.enabled` | `bool`, `true` | — | Enables Forbidden Arts approval policy. |
| `security.ward.forbiddenArts` | `string[]`, `[]` | — | Operator additions to the intrinsic code-owned Ward tool set. |
| `security.ward.autoDenyInUnattendedMode` | `bool`, `true` | — | Immediately denies Ward-gated calls on unattended paths. |
| `security.ward.unattendedMode` | `bool`, `false` | — | Default for operator-facing chat; daemons and Apprentices remain unattended. |
| `security.guardrails.detectPii` | `bool`, `true` | — | PII policy used only when `features.guardrails` is enabled. |
| `security.guardrails.blockToxicity` | `bool`, `false` | — | Applies the authored toxicity blocklist when guardrails are enabled. |
| `security.guardrails.toxicityBlocklist` | `string[]`, `[]` | — | Case-insensitive authored terms. |
| `security.guardrails.allowedTopics` | `string[]`, `[]` | — | Optional topic-pattern allowlist. |
| `security.guardrails.blockedTopics` | `string[]`, `[]` | — | Optional topic-pattern blocklist. |
| `security.guardrails.auditLog.enabled` | `bool`, `false` | — | Persists guardrail violations when guardrails are enabled. |
| `security.guardrails.auditLog.retentionDays` | `int`, `7` | 1–365 | Compliance retention for dated guardrail-audit files. |
| `security.perceptionWorkspaceRoots` | `string[]`, `[]` | absolute roots | Roots Perception may scan; empty denies scans. |
| `security.spellWorkspaceRoots` | `string[]`, `[]` | absolute roots | Roots spell CRUD may access; empty denies workspace spell CRUD. |
| `security.campaignRoots` | `string[]`, `[]` | absolute roots | Roots from which campaigns may be registered; empty denies registration. |
| `security.allowedUploadMimeTypes` | `string[]`, `[]` | MIME types | Optional additional upload restriction; empty adds no operator restriction. |
| `security.allowedImageMimeTypes` | `string[]`, `["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"]` | MIME types; nonempty while Scrying is enabled | MIME policy for Scrying images. |
| `workspaces.defaultRoot` | `string?`, `null` | path | Default for workspace-scoped routes. |
| `workspaces.enableFileWrite` | `bool`, `false` | — | Permits workspace create, modify, and delete routes. |

### Feature opt-ins

| Descriptor key | Type and default | Bounds | Semantics |
|---|---|---|---|
| `features.enterpriseTelemetry` | `bool`, `false` | — | Emits structured enterprise telemetry. |
| `features.scalarUi` | `bool`, `false` | — | Mounts the interactive Scalar API UI. |
| `features.conclave` | `bool`, `false` | — | Enables cross-Apprentice delegation. |
| `features.a2AServer` | `bool`, `false` | — | Exposes configured inbound A2A endpoints and derives Conclave availability. |
| `features.a2AClient` | `bool`, `false` | — | Permits dispatch to allowed remote A2A agents and derives Conclave availability. |
| `features.apprentices` | `bool`, `true` | — | Enables the Apprentice subsystem. |
| `features.lexicon` | `bool`, `true` | — | Enables model-writable Lexicon memory. |
| `features.archiveSearch` | `bool`, `true` | — | Enables search over past sessions. |
| `features.metrics` | `bool`, `true` | — | Exposes Prometheus metrics. |
| `features.embeddings` | `bool`, `false` | — | Enables The Weave embedding substrate. |
| `features.sessionSearch` | `bool`, `false` | — | Enables semantic session search and derives embedding-substrate activation. |
| `features.codebaseRetrieval` | `bool`, `false` | — | Enables semantic workspace retrieval and derives embedding-substrate activation. |
| `features.saga` | `bool`, `false` | — | Enables long-term associative memory retrieval and derives embedding-substrate activation. |
| `features.sagaExtraction` | `bool`, `false` | — | Enables automatic Saga extraction and derives Saga/substrate availability. |
| `features.semanticSpellRouting` | `bool`, `false` | — | Enables embedding-assisted spell routing and derives embedding-substrate activation. |
| `features.scrying` | `bool`, `true` | — | Accepts images for vision-capable models. |
| `features.attachments` | `bool`, `true` | — | Persists session attachments and exposes the attachment tool. |
| `features.clientTools` | `bool`, `false` | — | Forwards client-declared tools to compatible providers. |
| `features.webBrowsing` | `bool`, `false` | — | Advertises the native `web_search` and `read_url` tools. The deprecated `browse_web` name remains available only as a compatibility surface. |
| `features.guardrails` | `bool`, `false` | — | Runs configured input/output guardrails. |
| `features.workspaceChecks` | `bool`, `true` | — | Allows `workspace_check` advertisement when all platform and trust checks pass. |
| `features.memoryManagement` | `bool`, `false` | — | Enables session deletion, pinning, and compaction. |

Feature flags are capability policy. Edition, dependency, security, provider,
and platform eligibility still apply.

The native `delegate_task` subagent tool has no operator configuration key: its one-level
recursion cap and per-call token/cost/turn delegation are code-owned safety requirements.

### Integrations, execution, cost, daemon, and CLI

| Descriptor key | Type and default | Bounds | Semantics |
|---|---|---|---|
| `integrations.a2A.serverPath` | `string`, `"/api/conclave/a2a"` | valid API path | A2A endpoints and Agent Card discovery path. |
| `integrations.a2A.agentCardName` | `string?`, `null` | — | Advertised identity name. |
| `integrations.a2A.agentCardDescription` | `string?`, `null` | — | Advertised identity description. |
| `integrations.a2A.allowedRemoteAgents` | `string[]`, `[]` | URLs or origins | Optional remote Agent Card allowlist; empty adds no allowlist beyond the outbound URL guard. |
| `integrations.a2A.defaultWorkspace` | `string`, `""` | path | Fallback for inbound tasks; empty falls through to `workspaces.defaultRoot`, then the current directory. |
| `integrations.commLink.webhookUrlEnvironmentVariable` | `string?`, `null` | portable, case-insensitively unique environment name | Exact secret-bearing webhook reference; omission uses `ARCANUM_COMMLINK_WEBHOOK_URL`. |
| `integrations.commLink.allowedSchemes` | `string[]`, `["https"]` | URI schemes | Allowed webhook schemes. |
| `integrations.commLink.allowedHosts` | `string[]`, `[]` | hostnames | Optional webhook host allowlist; empty adds no host restriction beyond the outbound URL guard. |
| `integrations.embeddings.provider` | `string?`, `null` | configured provider name | Provider used for embeddings. |
| `integrations.embeddings.model` | `string?`, `null` | provider-advertised model | Embedding model ID. |
| `integrations.embeddings.dimensions` | `int`, `768` | 64–4,096 | Expected vector dimensions. Changing this value requires clearing and re-indexing embeddings or reinstalling the local database. |
| `integrations.embeddings.codebaseIndexing.watcherDebounceMilliseconds` | `int`, `300` | 50–5,000 | Debounce window for coalescing editor save/create/change/delete/rename events before incremental semantic indexing. |
| `integrations.embeddings.codebaseIndexing.maxWatchers` | `int`, `32` | 0–128 | Maximum recursively watched workspaces. `0` disables watchers but retains bounded periodic reconciliation. |
| `integrations.embeddings.codebaseIndexing.reconciliationIntervalMinutes` | `int`, `60` | 1–1,440 | Full workspace reconciliation cadence used even when watchers are healthy and as the complete fallback when they are unavailable. |
| `integrations.mcp.allowedHttpHosts` | `string[]`, `[]` | hostnames | Explicit plaintext-HTTP MCP exceptions; empty permits none and HTTPS remains the default. |
| `integrations.webResearch.searchProvider` | `string`, `"perplexity"` | nonblank registered provider name | Provider used by `web_search`. |
| `integrations.webResearch.perplexityModel` | `string`, `"sonar"` | `sonar` or `sonar-pro` | Perplexity Sonar model used for synthesized search. |
| `integrations.webResearch.credentialEnvironmentVariable` | `string?`, `null` | portable, case-insensitively unique environment name | Exact Perplexity credential reference. Omission checks `ARCANUM_PERPLEXITY_API_KEY`, then the secure local provider-key store. |
| `integrations.workspaceChecks.executableCatalog.dotNet.path` | `string`, `""` | canonical absolute path | Optional trusted native `dotnet`; empty delegates to trusted runtime resolution. |
| `integrations.workspaceChecks.customProfiles` | `Dictionary<string, WorkspaceCheckProfileSettings>`, `{}` | closed shape described below | Case-insensitive operator-authored build, test, and lint profiles; models never supply raw commands. |
| `execution.maxConcurrentApprentices` | `int`, `5` | 1–50 | Host-wide concurrent Apprentices. |
| `execution.maxPendingApprenticeStarts` | `int`, `100` | 1–1,000 | Apprentice-start backpressure queue. |
| `execution.maxConcurrentApprenticeBranches` | `int`, `3` | 1–64 | Concurrent Simulacrum branches within an Apprentice. |
| `execution.maxConcurrentA2ATasks` | `int`, `50` | 1–500 | Concurrent outbound A2A delegations. |
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
| `daemon.maxConcurrentJobs` | `int`, `8` | 1–1,024 | Concurrent Unseen Servant jobs. |
| `daemon.jobs.name` | `string`, `""` | nonblank | Human-readable schedule name. |
| `daemon.jobs.intervalMinutes` | `int`, `60` | 1–10,080 | Minutes between runs. |
| `daemon.jobs.targetSpell` | `string`, `""` | valid spell | Spell invoked on each tick. |
| `daemon.jobs.enabled` | `bool`, `true` | — | Makes the schedule eligible to run. |
| `cli.theme` | enum `"SystemDefault"` | `Light`, `Dark`, `SystemDefault` | CLI color theme. |
| `cli.showManaBar` | `bool`, `true` | — | Shows the chat token-budget indicator. |

### Dynamic dictionary shapes

`cost.pricing.modelPricing` is keyed case-insensitively by model name. Every
value has exactly the same rate fields and bounds as `defaultPricing`:

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

`integrations.workspaceChecks.customProfiles` is keyed case-insensitively by
profile ID and has this recursively validated shape:

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

The current closed-profile limits are 32 profiles, 32 fixed arguments per
profile, 16 options per profile, 16 allowed values per option, and 32 argument
tokens per allowed-value rendering. Profile and option IDs are 1–64 lowercase
ASCII letters, digits, or hyphens, start with a letter or digit, and are unique
case-insensitively; built-in profile IDs are reserved. Value IDs are nonblank,
case-insensitively unique, and at most 256 characters. Argument tokens are
nonblank, single-line, and at most 256 characters; response files, scripts,
shells, restore-enabling arguments, and runtime-owned path overrides are
rejected. `target` is empty or a workspace-relative `.sln`, `.slnx`, `.csproj`,
`.fsproj`, or `.vbproj` path of at most 256 characters. `kind`, `parser`, and
the first fixed argument must agree: `build`/`msBuild`/`build`,
`test`/`vsTest`/`test`, or `lint`/`dotNetFormat`/`format` with
`--verify-no-changes`.

### Credential environment references

Secret values are not configuration fields:

- `providers.credentialEnvironmentVariable` names the provider API-key
  variable. Omission derives
  `ARCANUM_PROVIDER_<NORMALIZED_NAME>_API_KEY`.
- `host.https.certificatePasswordEnvironmentVariable` names the PFX-password
  variable. Omission uses `ARCANUM_HTTPS_CERTIFICATE_PASSWORD`; PEM ignores
  this reference.
- `integrations.commLink.webhookUrlEnvironmentVariable` names the
  secret-bearing webhook URL variable. Omission uses
  `ARCANUM_COMMLINK_WEBHOOK_URL`.

An explicit reference replaces its default and does not fall through when
unset. Provider-name normalization retains ASCII letters and digits,
upper-cases letters, collapses non-alphanumeric runs to `_`, and uses `UNNAMED`
when empty. Provider names must be nonblank, and all final provider, PFX, and
CommLink reference names must be portable and unique case-insensitively.
Compendium never reads or displays a referenced secret value.

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

Set `OPENAI_API_KEY` in the host environment; never place its value in the
file.

## Editor architecture

The Host, Providers, Daemon, and CLI pages are polished views. Edition,
Security, Workspaces, Features, Integrations, Execution, and Cost use the
descriptor-driven generic view.

`SettingDescriptors` contains only editable public choices. Every descriptor is
rendered by one of those views. Descriptor coverage tests recurse through the
public graph, treat authored dictionaries as one editor, and verify public
mutable setters plus `ConfigurationJsonContext` metadata.

Provider/model rows and daemon schedules have structured editors. Pricing maps
and custom workspace-check profiles use multiline JSON editors backed by the
source-generated configuration JSON context. Allowlists use chip editors.

`ConfigurationViewModel` keeps the last loaded settings as a snapshot. Polished
pages rebuild only their owned records; generic edits clone and update only the
selected public property path. Unopened sections and provider facts therefore
survive load/edit/save unchanged.

## Secrets and HTTPS

The configuration fields, defaults, and resolution rules for secret references
are defined in
[Credential environment references](#credential-environment-references).
Compendium never resolves referenced secret values.

The Host page generates an owner-only self-signed loopback PEM certificate/key
pair under `~/.config/arcanum/certs/`, so generated local HTTPS needs no stored
password. It preserves a valid operator-selected HTTPS port and does not
install OS trust. External binding still requires HTTPS and a certificate valid
for the remote hostname/IP; TLS validation must not be disabled.

## Saving and validation

Save runs `ConfigurationValidator`, writes a temporary file, atomically replaces
`arcanum.json`, and applies owner-only permissions. Host/API loading performs a
source-generated fail-closed walk before binding, grouping all unknown paths
while preserving pricing and workspace-check dictionary keys. Validation
pointers use the same dot paths as descriptors.

Cancel restores the in-memory snapshot. Reload re-reads disk after confirmation
when local edits exist. A file watcher reports external changes and blocks
overwriting them until reload. Arcanum loads the source-generated configuration
snapshot at process start; restart the host after saving changes.

## Build and test

```bash
dotnet run --project src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj
```

Compendium is not Native AOT-published, but it edits the same source-generated,
Native-AOT-compatible configuration contract used by Arcanum.
