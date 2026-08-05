# Arcanum Command Reference

This is the canonical user-facing reference for the Arcanum CLI command tree, arguments, options, aliases, interactive commands, output modes, and exit behavior. It is verified against the live `System.CommandLine` tree in `RetroDownfall.Arcanum.Cli`; architectural rationale remains in [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md), and HTTP wire contracts remain in [`Arcanum.API.md`](Arcanum.API.md).

## Invocation and notation

Use `arcanum [global-options] <command> [command-options]`. In the syntax tables, `<value>` is required, `[<value>]` is optional, and `<value>...` accepts remaining tokens. Run `arcanum --help` or `arcanum <command> --help` for the executable's current short help.

Use the standard `--` end-of-options marker before positional text that begins with a hyphen; for
example, `arcanum ask -- --explain-this` treats `--explain-this` as the prompt.

Options marked repeatable may be supplied more than once. System.CommandLine response-file
expansion is disabled: an `@filename` value is application syntax only where this reference says
the command reads from a file. Supported values include spell bodies and execution input, prompt
templates and execution input, Apprentice goals/plans, Trial inquisitors, and MCP/tool invocation
JSON. Redirected stdin is used where explicitly documented, notably `run`, secret, and
tool-argument input.

## Global options

| Option | Meaning |
|---|---|
| `--json` | Force structured machine-readable output. Non-streaming commands emit one JSON payload; watch streams emit one source event per line as NDJSON. Diagnostics and progress remain on stderr. |
| `--plain` | Disable ANSI color, styling, and terminal animations without changing persisted configuration. |
| `--yes` | Automatically approve commands that otherwise require confirmation, including overwrites and explicit deletion flows. It is the only automatic confirmation switch; it does not change unrelated mutations. |
| `--no-context` | Ignore saved `cli-context.json` defaults for this invocation. Independent current-directory Campaign/Workspace detection still applies. |
| `-?`, `-h`, `--help` | Show help for the current command path and exit without running it. |
| `--version` | Show the CLI version. This option is available at the root command. |

The four Arcanum process options (`--json`, `--plain`, `--yes`, and `--no-context`) are recursive
and may appear before or after subcommands. Help aliases are available at every command path;
`--version` is root-only.

## Shared selection and context behavior

Resource-taking commands resolve an explicit exact ID first, then an exact case-insensitive name, then a unique case-insensitive name prefix. Omitted selectors may open a searchable picker only when stdin and stdout are interactive and output is not JSON. Cursor catalogs are followed until exhaustion, cancellation, a fetch error, or a repeated-token no-progress failure; there is no 100-page product ceiling. Redirected, ambiguous, or cancelled selection never guesses.

Effective inference context precedence is: explicit command option, saved active CLI context, current-directory detection, then server default. `--no-context` skips only the saved-context layer. Workspace paths are always paths on the server host, even when the bundled client and host run on the same machine.

## Output and exit behavior

| Exit code | Meaning |
|---:|---|
| `0` | Success, normal stream completion, or a cancelled interactive picker that performed no action. |
| `1` | Generic validation, API, execution, failed Trial, or unexpected stream-disconnect failure. |
| `2` | Command-line/configuration failure, a confirmation that cannot be obtained non-interactively, a non-positive watch-health interval, or an operation reconciliation that still requires operator attention. |
| `3` | Network failure where the command exposes the public network exit classification. |
| `130` | Caller cancellation or Ctrl+C for non-interactive streaming/watch commands. In `chat`, Ctrl+C cancels the active turn and returns to the prompt. |

Structured stdout is never mixed with diagnostics. `--plain` strips presentation only; it does not change payload content. Watch reconnect is opt-in and always warns that a gap may exist.

Long-running turns, commands, research, indexing, and durable operations are not assigned an
Arcanum-owned expected duration. They continue while they complete work and emit progress; Ctrl+C
or the corresponding `cancel` command is the normal operator stop. A local page, frame, buffer, or
checkpoint bound protects one allocation only and must expose or automatically follow its
continuation. Retained-boundary diagnostics name the owner, safe measurement/limit, saved or
checkpointed state, and exact continuation or recovery action.

Command-specific refinements:

- `ask` returns `0` on success, `1` for empty prompt, inference-option, stream, or API failure, and
  `130` when the in-flight turn is cancelled.
- `run` returns `0` when its selected route or dry-run preview succeeds, `1` for a live
  execution/stream/API failure, `2` for invalid input, staging, context, or route selection, and
  `130` when cancelled.
  An over-limit redirected input exits `2` before dispatch and is never truncated.
- `chat` returns `0` after a clean REPL exit and `1` if any turn failed. Ctrl+C during a turn
  cancels that turn and returns to the prompt rather than exiting `130`.
- Bare Command Center returns `0` after `/exit` or `/quit`, for non-interactive usage, and when
  `ARCANUM_NO_COMMAND_CENTER=1`; terminal-size or UI-bootstrap failure returns `1`.
- `center` and `open center` return the same in-process Command Center result. Resource/application
  launch commands return `0` after a successful start or cancelled picker, `1` when selection or
  launch fails, and `130` for caller cancellation.
- Watch commands and their compatibility aliases return `0` on normal completion, `2` on parse
  failure or a non-positive health interval, `1` on validation/API/unexpected-disconnect failure,
  and `130` on cancellation.
- `trial run` returns `1` when the completed Trial result is not passing, independently of HTTP
  or validation failure.
- `operation reconcile` returns `2` when all recoverable pages were processed but one or more
  operations still require operator repair; otherwise it returns `0`.
- `backup create` returns `1` for an incomplete result and never labels it complete or publishes an
  archive; `backup verify` returns `1` when authentication, structure, checksums, or database
  verification fail. Typed backup-plan validation returns `2`. Commands that consume a passphrase
  also return `2` for invalid or conflicting passphrase-source options; `backup create --dry-run`
  does not consume or semantically validate those source options.
- `preset list` still returns `0` when definitions can be listed but effective-state inspection is
  unavailable; that diagnostic stays on stderr and state is shown as unavailable. Unknown presets,
  missing prerequisites, invalid complete candidates, stale configuration, failed apply/reset, or
  failed rollback return `2`; a `Connection.*` service failure returns `3`.

## Handler-validated required values

Some options are nullable in the generated parser so handlers can resolve saved context, read a secure value, or produce a better error. The following requirements are therefore enforced after parsing even when short help displays square brackets:

| Command | Runtime requirement |
|---|---|
| `ask` | A non-empty prompt is required. |
| `run` | A positional/interactive instruction, non-empty redirected stdin, or at least one valid `--with @path` source is required. |
| `campaign create` | `--name` and `--path` are required. |
| `campaign import` | `--file` is required. |
| `campaign codex put` | `--file` is required. |
| `spell create` | `--name` and an effective Workspace are required. |
| `spell update`, `spell delete` | An effective Workspace is required. |
| `spell execute` | `--input` is required. |
| `spell import` | `--file` is required. |
| `spell clone` | `--new-name` is required. |
| `spell version create`, `spell version update` | `--version` and `--body` are required. |
| `spell version activate` | `--version` is required. |
| `prompt create` | `--name`, `--version`, and `--template` are required. |
| `prompt execute` | `--input` is required. |
| `prompt clone` | `--new-name` and `--new-version` are required. |
| `prompt import` | `--file` is required. |
| `apprentice create` | `--goal` is required. |
| `apprentice reweave` | `--plan` is required. |
| `apprentice intervene` | `--guidance` is required. |
| `apprentice cast` | `--goal` is required. |
| `session rename` | `--title` is required. |
| `trial run` | A valid `--target` and `--target-value` are required. |
| `ward resolve` | Supply exactly one of `--allow` or `--deny`. |
| `config set` | A value is required unless the selected sensitive descriptor uses redirected stdin or a hidden prompt. |

## Bare `arcanum`: Command Center

A bare interactive invocation opens Command Center. A non-interactive invocation, or `ARCANUM_NO_COMMAND_CENTER=1`, prints usage instead. `ARCANUM_NO_AUTO_SERVE=1` disables interactive host auto-start; `NO_COLOR` or `ARCANUM_NO_COLOR` selects a monochrome theme but does not disable the UI. Command Center requires at least an 80-by-12 terminal after UI initialization; a smaller terminal or UI-bootstrap failure exits with code 1.

`arcanum center` is the explicit alias, and `arcanum open center` reaches the same in-process host.
Unlike the automatic bare launch, an explicit request is not suppressed by
`ARCANUM_NO_COMMAND_CENTER`; the normal terminal and UI requirements still apply.

Interactive auto-start uses short connection/readiness observation only: two seconds per health
probe, three seconds for an already-listening unhealthy host, and 20 seconds after spawn. A launcher
timeout never kills the spawned host; retry, run `arcanum doctor`, verify `arcanum key show`, or
inspect `~/.config/arcanum/logs/auto-serve-bootstrap.log`.

| Command Center input | Action |
|---|---|
| `/help`, `/?` | Show Command Center help. |
| `/keys` | Show keyboard shortcuts. |
| `/status` | Show current session and serve status. |
| `/doctor` | Run a compact health check. |
| `/clear` | Clear the visible session log. |
| `/mana` | Show current token counters. |
| `/tools` | Show native tools. |
| `/model list` | List configured models. |
| `/provider list` | List configured providers. |
| `/mcp` | Show MCP server status. |
| `/arsenal` | Show the effective workspace arsenal. |
| `/campaign list [offset]` | List a 50-line terminal page of campaigns. When more state exists, the result prints the exact next offset command. |
| `/session list` | Refresh and list sessions. |
| `/session new` | Start a new session. |
| `/session resume <id>` | Load a transcript and continue the selected session. |
| `/fork` | Fork the complete active session and open the branch. |
| `/fork confirm` | Confirm a large attachment-bearing fork. |
| `/fork alternative` | Fork before the selected answer and regenerate. |
| `/fork at [<entry-id>]` | Fork through the supplied or selected transcript entry. |
| `/branch parent`, `/branch child` | Open the visible parent or newest child branch. |
| `/attach <path>` | Stage a local text file or image for the next turn. |
| `@path` | Inline-stage a text or image attachment in a message. |
| `/attachments` | List bound session attachments. |
| `/attachments add <name> [vN]` | Stage a prior attachment version as a reference. |
| `/attachments reveal <name> [vN]` | Reveal an attachment file in the OS file manager. |
| `/attachments refresh <name>` | Securely load the current tracked-file version. |
| `/context [list]` | List persistent session context pins. |
| `/context pin <kind> <target>` | Pin a file, directory snapshot, symbol range, session entry, attachment, URL, or diagnostic. |
| `/context unpin <id>` | Remove one context pin. |
| `/spell list [offset]` | List a 50-line terminal page of spells with exact next-offset continuation. |
| `/ward list [offset]` | List a 50-line terminal page of open Wards with exact next-offset continuation. |
| `/ward allow [<id>]` | Allow the supplied or currently prompted Ward. |
| `/ward deny [<id>]` | Deny the supplied or currently prompted Ward. |
| `/exit`, `/quit` | Leave Command Center. |

List offsets must be nonnegative integers. Campaign pages are fetched from the API at the requested
offset; Spell and Ward pages slice the complete fetched state for terminal rendering. When another
page exists, Command Center states that server/durable state was not changed and prints the exact
next command instead of silently truncating the list.

The Sessions pane keeps one 40-session page and the Transcript pane keeps one 200-entry page;
these are view allocations, not history totals. In Sessions, `Ctrl+PgDn` loads older sessions and
`Ctrl+PgUp` returns toward recent sessions. In Transcript, `Ctrl+PgUp` loads older entries and
`Ctrl+PgDn` returns toward the latest entries. Paging uses exact server cursors/offsets, refuses a
repeated or missing checkpoint as no progress, honors cancellation, and rebuilds Incantations from
the current transcript page.

## `arcanum chat` slash commands

| REPL command | Action |
|---|---|
| `/exit`, `/quit` | Leave the REPL. |
| `/clear` | Clear the terminal. |
| `/help` | Show the REPL command table. |
| `/new` | Clear the last-session pointer so the next turn starts a new thread. |
| `/history` | List recent sessions. |
| `/resume <id>` | Resume a session by ID. |
| `/delete <id>` | Archive the selected session; if it is active, also clear the REPL's active-session pointer. |
| `/rest` | Queue memory consolidation for the current session. |
| `/log` | Show the Campaign Log for the current session. |
| `/memory`, `/summary` | Show compressed Campaign Summary memory. |
| `/mana` | Show REPL and durable-session token usage. |
| `/model [<name>]` | With no name, open the interactive configured-model picker; with a name, set the REPL model override. |
| `/look` | Show an Eye of the World snapshot. |
| `/tools` | Toggle MCP tools for subsequent turns; built-in tools are unaffected. |
| `/mcp reload` | Reload MCP configuration. |
| `/arsenal` | Show spells, native tools, and MCP status. |
| `/attach` | Open the interactive file browser for next-turn staging. |
| `@path` | Inline-stage a local text file or allowed Scrying image for the next turn; images require a vision-capable model. |

## CLI command tree

### `arcanum open`

Launch Command Center, The Forge, or Compendium, optionally at one server-owned resource. Resource
selectors use the shared ID/exact-name/unique-prefix behavior and finish before any application
process starts. A cancelled picker performs no launch and returns success; an ambiguous or failed
selection reports the selector error and performs no launch.

**Syntax:** `arcanum open <target>`

| Command | Destination | Additional command options |
|---|---|---|
| `arcanum open center` | Command Center in the current `arcanum` process. | None beyond global or inherited family options. |
| `arcanum open theforge` | The Forge shell. | None beyond global or inherited family options. |
| `arcanum open compendium` | Compendium at configuration settings. | None beyond global or inherited family options. |
| `arcanum open session [<session>]` | The Forge Workbench at the selected Session. | None beyond global or inherited family options. |
| `arcanum open campaign [<campaign>]` | The Forge Atelier at the selected Campaign. | None beyond global or inherited family options. |
| `arcanum open spell [<spell>]` | The Forge Workbench at the selected Spell. | `--workspace <workspace>` — Selects the server Workspace by ID, name, or server-host path. |
| `arcanum open prompt [<prompt>]` | The Forge Workbench at the selected Prompt. | None beyond global or inherited family options. |
| `arcanum open apprentice [<apprentice>]` | The Forge War Table at the selected Apprentice. | None beyond global or inherited family options. |

The launch envelope is versioned and contains only the target application, resource kind, canonical
server resource identifier, optional opaque Workspace scope ID, initial view, and optional connection
profile ID. API keys, endpoints, prompt or file content, attachments, and server paths never enter
the child process arguments. The launcher passes the encoded envelope as one
`ProcessStartInfo.ArgumentList` value without shell interpolation, so spaces, quotes, Unicode, and
leading hyphens cannot create additional arguments.

The launcher-only `--arcanum-deep-link` argument is consumed before the normal CLI parser. For a
Command Center target, a target-only envelope enters the current host and a Session envelope resumes
the canonical Session GUID. Malformed, wrong-target, or unsupported-resource envelopes fail with a
fixed diagnostic without reproducing their private payload.

Discovery checks platform application bundles and executables, including Windows/Linux release
archives extracted side-by-side beneath one parent. It recognizes the shipped `*-win-x64` folder
names and only the active `*-linux-x64|arm64` architecture, then checks the repository development
project. If nothing starts, diagnostics list every candidate by safe kind/display path and provide
a repository-relative `dotnet run --project ...` command plus the equivalent current CLI command
(`session show`, `campaign get`, `spell get`, `prompt get`, `apprentice get`, or `config edit`).
Copyable fallback arguments are quoted for PowerShell on Windows and a POSIX shell on macOS/Linux;
this display-only formatting is separate from the direct structured process launch.
Launching a new process is the portable baseline. A platform integration may truthfully report
reuse/focus only when it actually supports activation; otherwise Arcanum starts another instance
and does not claim that an existing window was focused.

### `arcanum center`

Explicitly open Command Center in the current process. This is an alias for `arcanum open center`;
the full interactive input table is in [Bare `arcanum`: Command Center](#bare-arcanum-command-center).

**Syntax:** `arcanum center`

### `arcanum serve`

Hosts the Arcanum Minimal API.

Starts the local ASP.NET Core host. The nested `quit` command sends an authenticated shutdown request to an already-running host; it does not kill an arbitrary process. A host started automatically by an interactive client sets `ARCANUM_AUTO_LAUNCHED=1`, suppresses normal bootstrap/key output, and writes it to the owner-only auto-serve bootstrap log instead.

**Syntax:** `arcanum serve`

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum serve quit` | Requests the running host to shut down. | None beyond global or inherited family options. |

### `arcanum ask`

Ask the Mage.

Runs one inference turn and streams the answer. Prompt words are joined in order. Interactive use can auto-start the local host; `--new` and `--session` are mutually exclusive.

**Syntax:** `arcanum ask [<prompt>...]`

| Option | Meaning |
|---|---|
| `-m, --model <model>` | Use this configured model instead of the effective context or server default. |
| `-n, --new` | Start a new session rather than continuing the active or last session. |
| `--unattended` | Disable blocking human prompts and apply unattended Ward behavior. |
| `-c, --campaign <campaign>` | Use the selected campaign GUID, name, or unique prefix. |
| `--workspace <workspace>` | Use the selected workspace ID, name, or server-host path. |
| `--session <session>` | Continue the selected session by GUID, exact title, or unique title prefix. |
| `--temperature <temperature>` | Sampling temperature from 0 through 2. |
| `--top-p <top-p>` | Nucleus sampling cutoff from 0 through 1. |
| `--max-tokens <max-tokens>` | Maximum output tokens; any positive integer is accepted. |
| `--seed <seed>` | Optional signed 64-bit sampling seed; provider support varies. |
| `--stop <stop>` | Stop sequence; repeat the option to supply several sequences. |
| `--response-format <response-format>` | Response format: text, json (alias of json_object), json_object, or json_schema. |
| `--presence-penalty <presence-penalty>` | Presence penalty from -2 through 2. |
| `--frequency-penalty <frequency-penalty>` | Frequency penalty from -2 through 2. |
| `--image <image>` | Local image path to stage as a Scrying focus; repeatable, constrained by configured size and allowed MIME types, and requires a vision-capable model. |
| `--attachment <attachment>` | Bound attachment GUID to include; repeatable. |

### `arcanum run`

Run one prompt through the unified execution entry point. Interactive use can auto-start the local
host.

The optional positional words are joined in order as the instruction. Redirected standard input
is additional, untrusted turn context rather than a replacement for that instruction, so
`cat error.log | arcanum run "Explain this"` preserves both values. With no positional input and
an interactive stdin, `run` prompts once for one line. Redirected input is buffered to an exact
10 MiB (10,485,760 UTF-8 byte) ceiling; one byte beyond the ceiling or a stream read failure fails
clearly with no partial dispatch, silent truncation, or positional-only fallback.

Repeat `--with @path` to stage files for this turn. Relative paths resolve from the effective
working directory, while an explicitly supplied absolute path is honored. Text staging uses strict
UTF-8 and does not impose a filename-extension allowlist; recognized images use the existing
Scrying MIME, size, and model-capability checks. Text and stdin share the existing request authority:
1 MiB UTF-8-safe `AttachedFileDto` chunks and a 32 MiB aggregate, with no incidental file/part-count
ceiling. The 10 MiB stdin
reader ceiling is not a separate per-file ceiling for `--with`. Diagnostics report UTF-8 byte count,
part count, and SHA-256 for text; image diagnostics report decoded byte count and SHA-256. The client
sends images as `ScryingFocusDto` values, and the client filesystem
path is never treated as server authority. On a live route, these values enter the normal attachment
pipeline: an Attachments-enabled host persists and Session-binds them before inference, while an
Attachments-disabled host keeps them in memory for the current turn. A dry-run never persists them.

The default route is the ordinary Agent Loop. `--research` selects the progress-driven server-owned web
research workflow. `--spell <spell>` forces a named Spell resolved by exact case-insensitive name
or unique case-insensitive prefix. `--research` and `--spell` are the only route conflict; prompt,
stdin, `--with`, context, sampling, output, and dry-run options otherwise compose. `--dry-run`
performs a spend-free static, pre-inference preview of the resolved route, context, staged values,
Spell override, and inference options without search, embedding/RAG, automatic semantic Spell
routing, provider inference, tools, or persistence. A forced named Spell still resolves without
retrieval. The preview is not an exact copy of the eventual live `PingRequest`: a live Agent handoff
may add locally produced `PatternSnapshot` and `ChronosyncDelta` context.

Explicit context options follow the shared precedence over active local context, current-directory
detection, and server defaults. Campaign, Workspace, Session, and Model are resolved before the
route is dispatched; `--no-context` bypasses only saved context. Recursive `--plain` and `--json`
retain their global meanings and may appear before or after `run`.

**Syntax:** `arcanum run [<prompt>...]`

| Option | Meaning |
|---|---|
| `--research` | Route through progress-driven server-side research. Cannot be combined with `--spell`. |
| `--spell <spell>` | Force a Spell by exact name or unique name prefix. Cannot be combined with `--research`. |
| `--with <@path>` | Stage one turn-scoped text file or image; repeat for several files. Relative and explicitly supplied absolute paths are supported. |
| `--dry-run` | Preview the resolved static pre-inference payload/context plan without provider spend, search, tools, or persistence. |
| `--show-content` | With `--dry-run`, include model-visible content in the authenticated preview. |
| `-m, --model <model>` | Use this configured model instead of the effective context or server default. |
| `-n, --new` | Start without continuing the effective Session. If `--session` is also supplied, `--new` wins instead of creating another option conflict. |
| `--unattended` | Apply unattended human-prompt and Ward behavior to the selected live route; dry-run reflects the resulting tool policy. |
| `-c, --campaign <campaign>` | Use the selected Campaign GUID, exact name, or unique prefix. |
| `-w, --workspace <workspace>` | Use the selected Workspace ID, name, or server-host path. This is also the base for relative `--with` paths in the bundled local client. |
| `-s, --session <session>` | Continue the selected Session by GUID, exact title, or unique title prefix. |
| `--temperature <temperature>` | Sampling temperature from 0 through 2. |
| `--top-p <top-p>` | Nucleus sampling cutoff from 0 through 1. |
| `--max-tokens <max-tokens>` | Maximum Agent/Spell output tokens; any positive integer is accepted. Research uses `--token-budget`. |
| `--seed <seed>` | Optional signed 64-bit sampling seed; provider support varies. |
| `--stop <stop>` | Stop sequence; repeat the option to supply several sequences. |
| `--response-format <response-format>` | Response format: text, json (alias of json_object), json_object, or json_schema. |
| `--presence-penalty <presence-penalty>` | Presence penalty from -2 through 2. |
| `--frequency-penalty <frequency-penalty>` | Frequency penalty from -2 through 2. |
| `--sources <sources>` | Optional positive unique-source target. Omit it to continue until source exhaustion or deterministic no-progress. |
| `--token-budget <token-budget>` | Explicit positive research synthesis output-token budget (default 2000). |
| `--cost-budget <cost-budget>` | Optional nonnegative research search-provider cost limit in USD. |

### `arcanum chat`

Interactive multi-turn REPL with the Mage.

Starts the multi-turn Mage REPL. Inference controls apply to every turn, while `--attachment` values are staged for the next successful turn. Complete assistant Markdown renders lazily in allocation-safe chunks; content after the former 256 Ki-character display cutoff is not discarded. See the REPL slash-command table above.

**Syntax:** `arcanum chat`

| Option | Meaning |
|---|---|
| `-m, --model <model>` | The specific model to use for this inference request. |
| `-n, --new` | Start a new session thread, clearing the previous session at REPL startup. |
| `--no-tools` | Disable MCP-provided tools for this REPL session (built-in tools still apply). |
| `--unattended` | Force unattended for this run; skips ask_human blocking and uses Ward auto-deny. |
| `-c, --campaign <campaign>` | Campaign GUID to resolve the workspace from. |
| `--workspace <workspace>` | Workspace ID or path for this chat. |
| `--session <session>` | Session GUID, exact title, or unique title prefix to resume. |
| `--temperature <temperature>` | Sampling temperature 0-2 (lower = more deterministic). Applies to every turn. |
| `--top-p <top-p>` | Nucleus sampling cutoff 0-1. Applies to every turn. |
| `--max-tokens <max-tokens>` | Maximum output tokens per turn. |
| `--seed <seed>` | Seed for sampling determinism (provider support varies). Applies to every turn. |
| `--stop <stop>` | Stop sequence(s); pass --stop multiple times for several stops. |
| `--response-format <response-format>` | Response format: text \| json (alias of json_object) \| json_object \| json_schema. |
| `--presence-penalty <presence-penalty>` | Presence penalty -2..2. |
| `--frequency-penalty <frequency-penalty>` | Frequency penalty -2..2. |
| `--attachment <attachment>` | Bound attachment GUID to use on the next successful turn; repeatable. |

### `arcanum look`

Eye of the World: situational snapshot of the current directory (domain + TOC).

Builds an Eye of the World snapshot for the current directory locally, without requiring the HTTP host.

**Syntax:** `arcanum look`

### `arcanum doctor`

Run environment diagnostics (version, paths, API health).

Runs System, Paths, Configuration, MCP, Tokenizer, File Encryption, durable-operation, and authenticated API-health diagnostics. Embedding status distinguishes sqlite-vec from the complete streamed managed SIMD fallback; managed compatibility budget `0` means no total row budget. The health probe has a code-owned two-second timeout: an unreachable API is a warning, while hard local checks return a nonzero exit. `--json` emits the typed doctor report rather than decorated panels.

**Syntax:** `arcanum doctor`

| Option | Meaning |
|---|---|
| `--fix-permissions` | Apply owner-only permissions to configuration, preset state and recovery sidecars, the Grimoire database, and secret stores. |

### `arcanum key`

Master and native-provider API key utilities (secure local stores; no HTTP).

Reads and writes secure local credentials without an HTTP request. Master-key output is deliberately written to stderr. `key set` accepts its value as an argument, redirected stdin, or a hidden prompt; `key provider set` accepts only redirected stdin or a hidden prompt. Native-provider secret values are never displayed.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum key show` | Print the stored master API key to stderr (stdout piping does not capture the secret). | None beyond global or inherited family options. |
| `arcanum key set [<api-key>]` | Store a master API key in the OS credential store (mirrors to security.dat when possible). | None beyond global or inherited family options. |
| `arcanum key provider [command]` | Manage native provider credentials. Stored values are never displayed. | None beyond global or inherited family options. |
| `arcanum key provider set <provider>` | Store a native provider credential from redirected stdin or a secure prompt. | None beyond global or inherited family options. |
| `arcanum key provider status <provider>` | Report whether a native provider credential is configured. | None beyond global or inherited family options. |
| `arcanum key provider delete <provider>` | Delete a native provider credential from local secure stores. | None beyond global or inherited family options. |

### `arcanum lore`

Manage Grimoire explicit memory (lore) directly.

Maintains the legacy operator-owned key/value MageSettings store. Lore is distinct from Lexicon entities, Saga memories, session entries, and attachments. `lore list` follows every advancing server offset with no client-owned total-page ceiling; a non-advancing or overflowing continuation fails explicitly instead of looping or silently returning a prefix.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum lore list` | List all scribed lore keys. | None beyond global or inherited family options. |
| `arcanum lore get <key>` | Read a specific lore entry by key. | None beyond global or inherited family options. |
| `arcanum lore set <key> <value>` | Create or update a lore entry. | None beyond global or inherited family options. |
| `arcanum lore delete <key>` | Delete a lore entry. | None beyond global or inherited family options. |

### `arcanum daemon`

Manage the Arcanum background daemon.

Controls the OS background service and the server-owned Unseen Servant scheduler. `jobs`, `initiative`, and `alert` require a running authenticated host. Alert defaults are title `Arcanum alert`, severity `Warning`, and source `cli:daemon alert`.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum daemon install` | Install and start the Arcanum background daemon. | None beyond global or inherited family options. |
| `arcanum daemon uninstall` | Stop and uninstall the Arcanum background daemon. | None beyond global or inherited family options. |
| `arcanum daemon status` | Show whether the Arcanum daemon is running. | None beyond global or inherited family options. |
| `arcanum daemon jobs` | List Unseen Servant jobs (requires API: arcanum serve). | None beyond global or inherited family options. |
| `arcanum daemon initiative <job-name> <minutes>` | Set the adaptive polling interval for a job; minutes must be at least 1 (requires API: arcanum serve). | None beyond global or inherited family options. |
| `arcanum daemon alert <message>` | Send a Comm Link test alert (requires API: arcanum serve). | `-t, --title <title>` — Alert title.<br>`-s, --severity <severity>` — Severity: Info, Warning, or Critical.<br>`--source <source>` — The alert source label. |

### `arcanum campaign`

Persistent project containers for sessions, spells, prompts, Codex, and Sanctum; filesystem access and indexing remain Workspace responsibilities.

Manages persistent project containers. Campaigns own sessions, prompts, spells, Codex, and Sanctum policy; Workspaces separately own filesystem access and indexing.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum campaign list` | List registered campaigns. | `--type <type>` — Filter by exact type: spell, campaign, data, or custom. |
| `arcanum campaign get [<id>]` | Show campaign detail. | None beyond global or inherited family options. |
| `arcanum campaign use <campaign>` | Select a Campaign in the shared persistent CLI context. | None beyond global or inherited family options. |
| `arcanum campaign create` | Register a new campaign. | `--name <name>` — Campaign name.<br>`--path <path>` — Absolute server-host filesystem path represented by the campaign.<br>`--type <type>` — Campaign type: spell, campaign, data, or custom; defaults to campaign.<br>`--description <description>` — Optional human-readable campaign description. |
| `arcanum campaign update [<id>]` | Update a campaign. | `--name <name>` — Supplies the resource name stored by this command. |
| `arcanum campaign delete [<id>]` | Remove a campaign. | None beyond global or inherited family options. |
| `arcanum campaign export [<id>]` | Export a campaign's spells and prompts as JSON. | `--output <output>` — Writes the command result to this file instead of stdout. |
| `arcanum campaign import [<id>]` | Import spells and prompts into a campaign. | `--file <file>` — Reads the command's source document from this file path. |
| `arcanum campaign spells [<id>]` | List spells scoped to a campaign, shadowing built-ins. | `-q, --query <query>` — Applies the supplied free-text query.<br>`--tag <tag>` — Supplies a tag value; mutations may accept repeated tags, while list/search commands use it as a filter.<br>`--tool <tool>` — Filters by tool name. |
| `arcanum campaign prompts [<id>]` | List prompts scoped to a campaign. | `-q, --query <query>` — Applies the supplied free-text query.<br>`--tag <tag>` — Supplies a tag value; mutations may accept repeated tags, while list/search commands use it as a filter. |
| `arcanum campaign sessions [<id>]` | List sessions scoped to a campaign. | `--status <status>` — Filters by the exact lifecycle status accepted by the server.<br>`--search <search>` — Filter campaign sessions by search text.<br>`--limit <limit>` — Limits the maximum number of returned rows or results.<br>`--before-updated-at <before-updated-at>` — Return sessions updated before this ISO-8601 cursor. |
| `arcanum campaign codex [command]` | Manage the campaign's CODEX.md scratchpad. | None beyond global or inherited family options. |
| `arcanum campaign codex get [<id>]` | Print CODEX.md. | None beyond global or inherited family options. |
| `arcanum campaign codex put [<id>]` | Write CODEX.md from a file. | `--file <file>` — Reads the command's source document from this file path. |
| `arcanum campaign codex delete [<id>]` | Delete CODEX.md. | None beyond global or inherited family options. |

### `arcanum session`

Manage and continue sessions through the Arcanum API.

Provides the complete durable session lifecycle. Optional session selectors accept a GUID, exact title, unique title prefix, saved session context, or an interactive picker when allowed.

Sessions do not impose a total entry-count or fork-depth ceiling. The existing
`sessions.maxPinnedEntries` admission setting remains unchanged outside issue #55. Entry listings
page, long unsummarized history consolidates in timestamp-group-safe checkpoints, and
provider-context materialization adds no second pin-count ceiling: it retains per-item/per-turn byte
protections while explicitly reporting deferred accepted pins.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum session list` | List recent sessions. | `--campaign <campaign>` — Filter by campaign GUID.<br>`--status <status>` — Filter by session status.<br>`--search <search>` — Filter by search text.<br>`--model <model>` — Filter by model.<br>`--from <from>` — Include sessions on or after this ISO-8601 timestamp.<br>`--to <to>` — Include sessions on or before this ISO-8601 timestamp.<br>`--limit <limit>` — Maximum sessions per page. |
| `arcanum session show [<session>]` | Summarize a session, including telemetry and lineage. | None beyond global or inherited family options. |
| `arcanum session get [<session>]` | Compatibility alias for session show. | None beyond global or inherited family options. |
| `arcanum session chat [<session>]` | Continue a session by GUID, title, prefix, or interactive selection. | None beyond global or inherited family options. |
| `arcanum session entries [<session>]` | List transcript entries for a session. | `--offset <offset>` — Number of entries to skip.<br>`--limit <limit>` — Maximum entries to return. |
| `arcanum session watch [<session>]` | Watch replayed and live session entries. | `--since <since>` — Resume after this entry GUID. |
| `arcanum session fork [<session>]` | Fork a session through the server fork API. | `--title <title>` — Optional fork title.<br>`--up-to-entry <up-to-entry>` — Copy through this entry GUID.<br>`--campaign <campaign>` — Optional destination campaign GUID. |
| `arcanum session rename [<session>]` | Rename a session. | `--title <title>` — New session title. |
| `arcanum session archive [<session>]` | Archive a session without deleting it. | None beyond global or inherited family options. |
| `arcanum session export [<session>]` | Export an active or archived session. | `--format <format>` — Export format: json or markdown. |
| `arcanum session rest [<session>]` | Queue Campaign Log consolidation for a session. | None beyond global or inherited family options. |
| `arcanum session attachments [<session>]` | List bound session attachments. | None beyond global or inherited family options. |
| `arcanum session delete-entry [<entry>]` | Delete an entry and its derived Entry embedding rows after confirmation. | `--session <session>` — Session GUID, exact title, or unique title prefix; omit for an interactive picker. |
| `arcanum session pin-entry [<entry>]` | Pin an entry when memory management is enabled. | `--session <session>` — Session GUID, exact title, or unique title prefix; omit for an interactive picker. |
| `arcanum session unpin-entry [<entry>]` | Unpin an entry when memory management is enabled. | `--session <session>` — Session GUID, exact title, or unique title prefix; omit for an interactive picker. |
| `arcanum session compact [<session>]` | Compact session context when memory management is enabled. | None beyond global or inherited family options. |
| `arcanum session divine <query>` | Semantic search over Grimoire entries. | `--limit <limit>` — Maximum number of matching entries to return.<br>`--campaign <campaign>` — Campaign GUID filter; names and prefixes are not accepted here.<br>`--status <status>` — Filter semantic session search by status; defaults to active. |

### `arcanum saga`

Saga long-term associative memory (requires arcanum serve).

Automatic extraction has no public interval/window/output-token controls or total memory-count
ceiling. It processes durable history oldest-first in timestamp-group-safe checkpoint pages and
retries a failed page without advancing its watermark. Listing and semantic search remain paged;
explicit deletion, retention policy, provider capability, and cancellation own the real boundaries.

Inspects and deletes long-term associative Saga memory. These commands do not merge Saga with Lexicon, session, attachment, or workspace stores.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum saga list` | Paginated listing of Saga memories. | `--query <query>` — Free-text query.<br>`--session <session>` — Filter by session GUID.<br>`--limit <limit>` — Maximum number of memories to return.<br>`--offset <offset>` — Pagination offset. |
| `arcanum saga divine <query>` | Semantic search over Saga memories. | `--limit <limit>` — Maximum number of results to return. |
| `arcanum saga delete <id>` | Delete a single Saga memory. | None beyond global or inherited family options. |
| `arcanum saga stats` | Aggregate summary of Saga memory storage. | None beyond global or inherited family options. |

### `arcanum memory`

Inspect distinct Arcanum memory sources and retention policies.

Provides read-only cross-store inspection plus explicit Lexicon deletion. Search results retain their source scope; there is intentionally no generic delete-all-memory command.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum memory status [<session>]` | Show feature gates and counts by memory store, including the `Tapestry` row (published hierarchical nodes only; gated by `Arcanum:Features:Tapestry`). | None beyond global or inherited family options. |
| `arcanum memory sources [<session>]` | Describe provenance and retention for every memory source. | None beyond global or inherited family options. |
| `arcanum memory search <query>` | Search persisted memory with an explicit or displayed scope. | `--scope <scope>` — session, attachments, workspace, saga, lexicon, or all (default).<br>`--session <session>` — Optional session GUID, exact title, or unique title prefix.<br>`--workspace <workspace>` — Optional workspace ID; omit to search every indexed workspace. |
| `arcanum memory explain [<session>]` | Explain what can be eligible for the next turn and why. | None beyond global or inherited family options. |
| `arcanum memory lexicon [command]` | Inspect or explicitly delete Lexicon entities. | None beyond global or inherited family options. |
| `arcanum memory lexicon list` | List Lexicon entities. | None beyond global or inherited family options. |
| `arcanum memory lexicon show <name>` | Show a Lexicon entity by name. | None beyond global or inherited family options. |
| `arcanum memory lexicon search <query>` | Search Lexicon names, types, and facts. | None beyond global or inherited family options. |
| `arcanum memory lexicon delete <name>` | Delete one explicitly named Lexicon entity. | None beyond global or inherited family options. |

### `arcanum spell`

Spell utilities (requires arcanum serve).

Manages built-in and workspace spells, named version files, validation, dry-run casting, and execution. Workspace selectors resolve server-host resources. Legacy direct listing retains the array response, while resource selection uses the 50-item opaque-cursor catalog and follows pages as needed. Command Center `/spell list [opaque-cursor]` fetches one metadata page and prints the exact continuation; a changed/missing cursor anchor or mismatched filter instructs a cursor-free restart.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum spell list` | List spells. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell get [<name>]` | Show spell detail. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell create` | Create a spell. | `--name <name>` — Name of the spell to create.<br>`--workspace <workspace>` — Selects the workspace by ID, name, or server-host path.<br>`--description <description>` — Stores the supplied human-readable description.<br>`--body <body>` — Supplies body text inline or from `@filename`, as accepted by the command.<br>`--tag <tag>` — Supplies a tag value; mutations may accept repeated tags, while list/search commands use it as a filter.<br>`--declared-tool <declared-tool>` — Declared tool name; repeat for several tools.<br>`--dependency <dependency>` — Spell dependency name; repeat for several dependencies. |
| `arcanum spell update <name>` | Update a spell. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path.<br>`--description <description>` — Replacement spell description.<br>`--tag <tag>` — Replacement tag; repeat for several tags. |
| `arcanum spell delete <name>` | Delete a spell. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell search` | Search spells by query, tag, tool, or source. | `-q, --query <query>` — Free-text spell search query.<br>`--tag <tag>` — Supplies a tag value; mutations may accept repeated tags, while list/search commands use it as a filter.<br>`--tool <tool>` — Filter spells by declared or attuned tool name.<br>`--source <source>` — Filter by spell source, such as built-in or workspace.<br>`--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell validate <name>` | Validate a spell's frontmatter and dependencies. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell execute <name>` | Execute a spell, writing assistant response text to stdout and any tool-call summary to stderr. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path.<br>`--version <version>` — Selects the named prompt or spell version label.<br>`--input <input>` — User input passed inline or as `@filename`. |
| `arcanum spell versions <name>` | List spell versions. | `--workspace <workspace>` — Workspace selector used to resolve the spell and its version files. |
| `arcanum spell export <name>` | Export a spell as portable JSON. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path.<br>`--output <output>` — Write portable JSON to this path instead of stdout. |
| `arcanum spell import` | Import a spell from portable JSON. | `--file <file>` — Path to the portable spell JSON document.<br>`--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell cast <name>` | Dry-run preview of a spell's assembled system prompt. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path.<br>`--session <session>` — Session selector used while assembling the dry-run context.<br>`--campaign <campaign>` — Campaign selector used while assembling the dry-run context. |
| `arcanum spell clone <name>` | Clone a spell to a new name. | `--new-name <new-name>` — Required destination spell name.<br>`--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell version [command]` | Manage named spell file versions. | None beyond global or inherited family options. |
| `arcanum spell version create <name>` | Create a new spell version. | `--version <version>` — Selects the named prompt or spell version label.<br>`--body <body>` — Supplies body text inline or from `@filename`, as accepted by the command.<br>`--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell version update <name>` | Update an existing spell version's body. | `--version <version>` — Named version label to update.<br>`--body <body>` — Replacement version body as inline text or @filename.<br>`--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell version activate <name>` | Activate a spell version, swapping it into SPELL.md. | `--version <version>` — Selects the named prompt or spell version label.<br>`--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |

### `arcanum prompt`

The Forge prompt utilities (requires arcanum serve).

Manages versioned prompt templates. Template and input values support inline text or `@filename`; repeatable `--param` values use `key=value`.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum prompt list` | List prompts. | `--campaign-id, --campaignId <campaign-id>` — Filter by campaign GUID.<br>`-q, --query <query>` — Free-text query.<br>`--tag <tag>` — Filter by tag. |
| `arcanum prompt get [<id>]` | Show prompt detail. | None beyond global or inherited family options. |
| `arcanum prompt versions <name>` | List versions of a prompt by name. | `--campaign-id, --campaignId <campaign-id>` — Filter by campaign GUID. |
| `arcanum prompt create` | Create a prompt. | `--name <name>` — Prompt name.<br>`--version <version>` — Prompt version label.<br>`--template <template>` — Prompt template: inline text, or @filename to read from a file.<br>`--campaign-id, --campaignId <campaign-id>` — Campaign GUID to associate with.<br>`--description <description>` — Prompt description.<br>`--tag <tag>` — Tag; pass multiple times for several tags. |
| `arcanum prompt update [<id>]` | Update a prompt. | `--template <template>` — Prompt template: inline text, or @filename to read from a file.<br>`--tag <tag>` — Tag; pass multiple times for several tags. |
| `arcanum prompt delete [<id>]` | Delete a prompt. | None beyond global or inherited family options. |
| `arcanum prompt render [<id>]` | Render a prompt template with parameters. | `--param <param>` — Template parameter as key=value; pass multiple times for several parameters. |
| `arcanum prompt test [<id>]` | Assemble the system prompt without LLM cost. | None beyond global or inherited family options. |
| `arcanum prompt execute [<id>]` | Render and run session-backed inference, writing assistant response text to stdout and any tool-call summary to stderr. | `--input <input>` — User message for the prompt turn: inline text, or @filename to read from a file.<br>`--param <param>` — Template parameter as key=value; pass multiple times for several parameters.<br>`--session-id, --sessionId <session-id>` — Session GUID to bind context from. |
| `arcanum prompt clone [<id>]` | Clone a prompt to a new name/version. | `--new-name <new-name>` — New prompt name.<br>`--new-version <new-version>` — New prompt version label.<br>`--campaign <campaign>` — Campaign GUID to associate the clone with. |
| `arcanum prompt export [<id>]` | Export a prompt as portable JSON. | `--output <output>` — Write exported JSON to this file instead of stdout. |
| `arcanum prompt import` | Import a prompt from portable JSON. | `--file <file>` — Path to a prompt export JSON file.<br>`--campaign-id, --campaignId <campaign-id>` — Campaign GUID to associate the import with. |

### `arcanum ward`

Ward approval gates for Forbidden Arts (requires arcanum serve).

Lists Forbidden Arts approval gates and resolves one gate. `--allow` and `--deny` are mutually exclusive.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum ward list` | List active wards. | None beyond global or inherited family options. |
| `arcanum ward get <id>` | Show ward detail. | None beyond global or inherited family options. |
| `arcanum ward resolve <id>` | Allow or deny a ward. | `--allow` — Allow the warded tool call to proceed.<br>`--deny` — Deny the warded tool call.<br>`--reason <reason>` — Optional reason recorded with the resolution. |

### `arcanum trial`

Run Trials against spells, prompts, or Apprentice goals (requires arcanum serve).

Runs Proving Grounds evaluation against a spell, prompt, or Apprentice goal. It renders the
Passed/Failed summary, a verdict table, and at most the first 500 characters of Trial output. A
completed Trial that does not pass returns exit code 1.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum trial run` | Run a Trial with Inquisitors. | `--target <target>` — Trial target kind: spell, prompt, apprenticeGoal.<br>`--target-value <target-value>` — Spell name, prompt GUID, or apprentice goal text.<br>`--model <model>` — Model override for the Trial.<br>`--workspace <workspace>` — Workspace root to scope the Trial.<br>`--name <name>` — Trial display name; defaults to '{targetKind}:{target}'.<br>`--inquisitor <inquisitor>` — Inquisitor spec: inline JSON, or @filename. Pass multiple times for several inquisitors.<br>`--var <var>` — Trial variable as key=value; pass multiple times for several variables. |

### `arcanum apprentice`

The Forge Apprentice orchestration (requires arcanum serve).

Manages durable Apprentice orchestration, intervention, replanning, child delegation, and Chronicle streaming. Lifecycle commands resolve an Apprentice selector before mutation.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum apprentice list` | List Apprentices. | `--campaign-id, --campaignId <campaign-id>` — Filter by campaign GUID.<br>`--status <status>` — Filter by status.<br>`--limit <limit>` — Maximum number of Apprentices to return. |
| `arcanum apprentice get [<id>]` | Show Apprentice detail. | None beyond global or inherited family options. |
| `arcanum apprentice create` | Create an Apprentice. | `--goal <goal>` — Apprentice goal: inline text, or @filename to read from a file.<br>`--name <name>` — Display name; defaults to a truncated form of the goal.<br>`--campaign-id, --campaignId <campaign-id>` — Campaign GUID to associate with.<br>`--workspace <workspace>` — Workspace root to scope the Apprentice. |
| `arcanum apprentice delete [<id>]` | Delete a terminal Apprentice. | None beyond global or inherited family options. |
| `arcanum apprentice start [<id>]` | Persist the start and begin plan generation/execution when a host concurrency slot is available. Temporary capacity queues the start instead of rejecting it; Chronicle/status surfaces progress and `cancel` removes queued work. | None beyond global or inherited family options. |
| `arcanum apprentice pause [<id>]` | Pause at the next step boundary. | None beyond global or inherited family options. |
| `arcanum apprentice resume [<id>]` | Resume from checkpoint. | None beyond global or inherited family options. |
| `arcanum apprentice cancel [<id>]` | Cancel execution. | None beyond global or inherited family options. |
| `arcanum apprentice reweave [<id>]` | Replace the remaining plan steps. | `--plan <plan>` — JSON array of plan steps: inline text, or @filename to read from a file. |
| `arcanum apprentice intervene [<id>]` | Provide Divine Intervention guidance to an escalated Apprentice. | `--guidance <guidance>` — Guidance text for the escalated Apprentice. |
| `arcanum apprentice cast [<id>]` | Delegate a child Apprentice via The Conclave. | `--goal <goal>` — Child Apprentice goal text.<br>`--name <name>` — Display name for the child Apprentice. |
| `arcanum apprentice chronicle [<id>]` | Stream live Apprentice events (SSE). | None beyond global or inherited family options. |

### `arcanum model`

Native model listing across configured providers (requires arcanum serve).

Lists or selects models from the latest successfully persisted configuration without exposing
provider endpoints or credentials. Other inference/runtime consumers still require a host restart
before they adopt a configuration change.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum model list` | List configured models across all providers (GET /api/models). | None beyond global or inherited family options. |
| `arcanum model get [<model>]` | Show a configured model without exposing its endpoint. | None beyond global or inherited family options. |

### `arcanum provider`

Native provider listing and configuration summary (requires arcanum serve).

Lists or selects providers from the latest successfully persisted configuration while keeping
endpoints and credential details redacted. Other inference/runtime consumers still require a host
restart before they adopt a configuration change.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum provider list` | List configured providers with redacted secrets (GET /api/providers). | None beyond global or inherited family options. |
| `arcanum provider get [<provider>]` | Show a configured provider without exposing endpoint or credential details. | None beyond global or inherited family options. |

### `arcanum workspace`

Workspace = registered filesystem access/indexing boundary. Campaign = persistent project container with sessions, spells, prompts, Codex, and Sanctum. Paths are resolved on the server host.

Manages registered server-host filesystem and indexing boundaries. Optional workspace selectors use explicit value, saved context, then current-directory containment. `register` defaults to the current directory, derives the name from the final path segment, and uses type `custom`.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum workspace list` | List registered workspaces. | None beyond global or inherited family options. |
| `arcanum workspace current` | Map the client current directory to registered server Workspace and Campaign resources. | None beyond global or inherited family options. |
| `arcanum workspace register [<path>]` | Register a server-host path; omit path to register this directory when client and server share a host. | `--name <name>` — Workspace display name; defaults to the path's final segment.<br>`--type <type>` — Workspace type: spell, campaign, data, or custom (default). |
| `arcanum workspace show [<workspace>]` | Show one registered workspace and its server-host path. | None beyond global or inherited family options. |
| `arcanum workspace get [<workspace>]` | Compatibility alias for 'workspace show'. | None beyond global or inherited family options. |
| `arcanum workspace tree [<workspace>]` | List the complete server-side workspace tree recursively by following every opaque `nextCursor` from the 500-entry workspace-files API; each page keeps a bounded 501-candidate heap, and a changed/missing exact checkpoint returns an actionable restart error instead of offset-shift skips or duplicates. | `--path <path>` — Optional relative path inside the selected workspace. |
| `arcanum workspace info <path>` | Inspect a path through the server workspace API. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum workspace read <path>` | Read a file through the bounded server workspace API. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum workspace search <query>` | Semantically search the selected workspace's server-side index. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection.<br>`--limit <limit>` — Optional bounded result count. |
| `arcanum workspace index [<workspace>]` | Request a complete cancellable server-side workspace re-index. Internal file/checkpoint pages continue until eligible work is visited; repository size is not a total-work rejection. | None beyond global or inherited family options. |
| `arcanum workspace index-status [<workspace>]` | Show server-side workspace indexing status. | None beyond global or inherited family options. |
| `arcanum workspace chunks [<workspace>]` | Inspect bounded previews of server-side indexed chunks. | `--path <path>` — Optional relative-path filter.<br>`--limit <limit>` — Maximum indexed chunks to return.<br>`--offset <offset>` — Number of indexed chunks to skip. |
| `arcanum workspace unregister [<workspace>]` | Remove a workspace registration without deleting files. | None beyond global or inherited family options. |

### `arcanum mcp`

Operate MCP lifecycle, trust, discovery, and external-only diagnostics without exposing server secrets.

Administers MCP server lifecycle, trust, tool discovery, and external diagnostics. Safe projections omit URLs, commands, arguments, environment variables, working directories, and credentials. `invoke [arguments]` accepts inline JSON, `@file`, or redirected stdin and uses `{}` when omitted interactively; input is bounded to 1 MiB of UTF-8 JSON and depth 64. Initialization and HTTP connection establishment keep local lifecycle deadlines, but a connected invocation has no total request clock and runs until completion, terminal protocol/provider failure, or Ctrl+C/caller cancellation.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum mcp list` | List safe MCP scope, transport, trust, lifecycle, tool-count, and last-error status. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum mcp show [<server>]` | Show one MCP server's safe status summary. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum mcp get [<server>]` | Compatibility alias for 'mcp show'. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum mcp start [<server>]` | Start one trusted MCP server. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum mcp stop [<server>]` | Stop one MCP server. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum mcp restart [<server>]` | Restart one trusted MCP server. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum mcp reload` | Clear MCP partitions and reload global or explicitly scoped workspace configuration. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum mcp trust [<workspace>]` | Trust the current workspace mcp.json bytes; defaults to the current directory. | None beyond global or inherited family options. |
| `arcanum mcp tools [<server>]` | List tools exposed by one selected MCP server. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum mcp invoke <tool> [<arguments>]` | Invoke one external MCP tool diagnostically; internal and Forbidden Art tools remain blocked server-side. | `--server <server>` — External MCP server name or unique prefix; omit for tool-based selection.<br>`--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |

### `arcanum tool`

Discover and invoke built-in diagnostic tools through the authenticated API.

Discovers and invokes built-in diagnostic tools through the authenticated API. `invoke [arguments]` accepts inline JSON, `@file`, or redirected stdin and uses `{}` when omitted interactively; input is bounded to 1 MiB of UTF-8 JSON and depth 64.

The inference-only internal MCP surface pairs `execute_command` with automatically attuned
`read_command_output`. Oversized stdout/stderr yields a bounded preview plus an opaque
connection-lifetime handle and stream names. Continue each stream from byte offset `0` through each
returned `nextOffset`; strict UTF-8 page size is a JSON-RPC-safe allocation bound, not a total-output
ceiling. Each stream is deleted immediately after its final page; the handle expires after all
streams finish or when the connection closes. Complete stdout and stderr share the existing explicit
Sanctum `MaxFileWriteMb` policy, whose classified error reports the measured bytes and exact rerun or
configuration action.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum tool list` | List built-in diagnostic tools available for the selected workspace scope. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum tool show <tool>` | Show one built-in diagnostic tool. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum tool invoke <tool> [<arguments>]` | Invoke one built-in diagnostic tool with inline JSON, @file JSON, or redirected stdin. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |

### `arcanum search`

Search the live web with bounded results and citations.

Runs one bounded web search and returns citations without starting a chat turn.

**Syntax:** `arcanum search <query>`

| Option | Meaning |
|---|---|
| `--count <count>` | Maximum results/citations (1-20; default 5). |
| `--freshness <freshness>` | Freshness filter: day, week, month, or year. |
| `--include-domain <include-domain>` | Restrict search to a domain; repeat for several. |
| `--exclude-domain <exclude-domain>` | Exclude a domain; repeat for several. |
| `--save <save>` | Atomically save the final Markdown content to a local path. |
| `--attach-to-session <attach-to-session>` | Persist the final Markdown as an attachment on a session. |

### `arcanum browse`

Read a bounded web page as Markdown with explicit rendering behavior.

Reads one bounded URL as Markdown. JavaScript rendering is explicit; when unavailable, retry with static rendering.

**Syntax:** `arcanum browse <url>`

| Option | Meaning |
|---|---|
| `--render <render>` | Rendering mode: static (default) or javascript. |
| `--save <save>` | Atomically save the final Markdown content to a local path. |
| `--attach-to-session <attach-to-session>` | Persist the final Markdown as an attachment on a session. |

### `arcanum research`

Run progress-driven server-side research with citations and cancellation.

The server performs another research pass while it discovers new unique sources. It stops when an
optional source target is reached, a pass discovers no new sources, the user/host cancels, an
explicit token/cost policy is reached, or a provider/safety boundary fails. Progress and the exact
target/no-progress terminal reason are written to stderr; the selected final format is written to
stdout. There is no hop counter or default total-source ceiling.

**Syntax:** `arcanum research <question>`

| Option | Meaning |
|---|---|
| `--sources <sources>` | Optional positive unique-source target; omit for source exhaustion/deterministic no-progress. |
| `--model <model>` | Server-configured model for final synthesis. |
| `--token-budget <token-budget>` | Explicit positive synthesis output-token budget (default 2000). |
| `--cost-budget <cost-budget>` | Optional nonnegative reported search-provider cost policy in USD. |
| `--continue-session <continue-session>` | Continue an existing session by GUID, exact title, or unique prefix. |
| `--format <format>` | Final output: terminal (default), markdown, or json. |
| `--save <save>` | Atomically save the final Markdown content to a local path. |
| `--attach-to-session <attach-to-session>` | Persist the final Markdown as an attachment on a session. |

### `arcanum file`

Upload, inspect, download, and delete OpenAI-compatible files.

Uses the OpenAI-compatible `/v1/files` surface. Uploads and downloads stream bytes; downloads use a staged atomic replacement, choose a safe default filename when `--output` is omitted, and require confirmation before overwrite.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum file upload <path>` | Stream a local file to /v1/files. | `--purpose <purpose>` — OpenAI file purpose (default: batch).<br>`--content-type <content-type>` — Declared MIME type; otherwise inferred conservatively from the extension. |
| `arcanum file list` | List uploaded file metadata. | `--purpose <purpose>` — Filter by exact file purpose. |
| `arcanum file show <id>` | Show one uploaded file's metadata. | None beyond global or inherited family options. |
| `arcanum file download <id>` | Stream a file to a safe local filename; existing files require confirmation. | `--output <output>` — Explicit local destination path. |
| `arcanum file delete <id>` | Delete uploaded file metadata and content after confirmation. The server returns a conflict and preserves both when any batch input/output/error role still references the file, including a terminal batch. Concurrent batch and artifact-reference writes are database-conditional: either their reference commits and blocks deletion, or deletion wins and the reference write is rejected. | None beyond global or inherited family options. |

### `arcanum batch`

Create and operate asynchronous OpenAI-compatible batches.

Uses the OpenAI-compatible `/v1/batches` surface. Local JSONL preflight catches basic shape errors; the server remains authoritative. Output/error downloads use safe filenames and overwrite confirmation. `batch watch --json` emits the final terminal batch object, unlike live `watch ... --json` NDJSON. There is no total request-count or wall-clock age ceiling: the server streams internal 64-line processing pages through explicit budget reservation and durable per-line dispatch/result checkpoints. One physical record keeps a pooled 256 KiB prefix before owner-only spill; the retained 64 MiB one-request materialization boundary reports measured bytes and lets later records continue. Durable 64-bit counters make show/list independent of artifact recounting. Batch metadata pages default to 20 and cap one response at 100, with an opaque status-bound cursor; worker pickup likewise selects only the oldest rows needed for free concurrency slots. Completed lines publish in input order and are skipped on resume; cancellation, host interruption, or an unexpected pre-publication failure leaves every claimed line either published as `batch_interrupted_after_dispatch` or durable for startup reconciliation, never silently deleted or replayed. A budget rejection preserves completed output and identifies the first remaining line and continuation action. The OpenAI `completion_window` field is accepted as compatibility metadata only; explicit cancellation, terminal retention, and startup reconciliation own the lifecycle.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum batch create <input-file>` | Create a batch from a local JSONL file or an existing uploaded file ID. | None beyond global or inherited family options. |
| `arcanum batch list` | List one batch metadata page with durable request counts and status; human output prints the exact next command when more rows remain and JSON preserves `next_cursor`. | `--status <status>` — Filter by exact batch status.<br>`--cursor <cursor>` — Continue from the opaque cursor returned by the same status query. |
| `arcanum batch show <id>` | Show one batch with request counts and artifact IDs. | None beyond global or inherited family options. |
| `arcanum batch watch <id>` | Poll with bounded exponential backoff until the batch reaches a terminal state. | `--poll-interval <poll-interval>` — Initial poll interval in milliseconds (1-10000; default: 1000). |
| `arcanum batch cancel <id>` | Request cancellation using the server's idempotent semantics. | None beyond global or inherited family options. |
| `arcanum batch reset <id>` | Reset a server-classified stuck batch for retry. | None beyond global or inherited family options. |
| `arcanum batch output <id>` | Download the batch output JSONL file. | `--output <output>` — Explicit local destination path. |
| `arcanum batch errors <id>` | Download the batch error JSONL file. | `--output <output>` — Explicit local destination path. |

### `arcanum attachment`

Manage session attachment snapshots, live references, versions, pins, and exports.

Manages session-bound snapshot and live-reference attachments. `list [session] --session <session>` accepts either selector form and gives the option precedence. Other attachment selectors are optional and use context/picker resolution. `show --privacy` needs no attachment. Exports always write to a safe file, never stdout, and require confirmation before overwrite.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum attachment list [<session>]` | List the latest version of each attachment in a session. | `--session <session>` — Session GUID, title, or unique title prefix. |
| `arcanum attachment add <path>` | Create a snapshot from any local path, or use '-' to stream stdin. | `--content-type, --mime <mime>` — Optional MIME type hint; the server remains authoritative.<br>`--name <name>` — Filename metadata, especially useful with stdin.<br>`--session <session>` — Session GUID, title, or unique title prefix. |
| `arcanum attachment reference <workspace-path>` | Create a refreshable reference to a server workspace path. | `--workspace <workspace>` — Registered workspace ID, name, or saved workspace path.<br>`--name <name>` — Optional logical attachment key.<br>`--session <session>` — Session GUID, title, or unique title prefix. |
| `arcanum attachment show [<attachment>]` | Show attachment metadata, or use --privacy for the attachment privacy model. | `--privacy` — Explain snapshot, reference, export, and terminal-byte privacy semantics.<br>`--session <session>` — Session GUID, title, or unique title prefix. |
| `arcanum attachment versions [<attachment>]` | List every version for an attachment logical key. | `--session <session>` — Session GUID, title, or unique title prefix. |
| `arcanum attachment refresh [<attachment>]` | Ask the server to refresh a live reference through the shared refresh service. | `--session <session>` — Session GUID, title, or unique title prefix. |
| `arcanum attachment pin [<attachment>]` | Pin an attachment version into durable session context. | `--session <session>` — Session GUID, title, or unique title prefix. |
| `arcanum attachment unpin [<attachment>]` | Remove an attachment version from durable session context. | `--session <session>` — Session GUID, title, or unique title prefix. |
| `arcanum attachment export [<attachment>]` | Export decrypted attachment content atomically to a local file. | `-o, --output <output>` — Destination file. Attachment bytes are never written to stdout.<br>`--session <session>` — Session GUID, title, or unique title prefix. |
| `arcanum attachment reveal [<attachment>]` | Reveal the encrypted stored snapshot artifact in the operating system file manager. | `--session <session>` — Session GUID, title, or unique title prefix. |

### `arcanum operation`

Inspect and repair durable long-running operations.

Inspects and repairs durable long-running operations. Safe detail omits checkpoint payloads; cancellation and retry use server-side lifecycle transitions.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum operation list` | List durable operations. | `--kind <kind>` — Filter by registered operation kind.<br>`--state <state>` — Filter by lifecycle state. |
| `arcanum operation show <id>` | Show safe operation detail (checkpoint payloads are never returned). | None beyond global or inherited family options. |
| `arcanum operation cancel <id>` | Request cancellation through a compare-and-swap transition. | None beyond global or inherited family options. |
| `arcanum operation retry <id>` | Reset a failed, abandoned, or repair-required operation to Pending. | None beyond global or inherited family options. |
| `arcanum operation reconcile` | Process every recoverable operation in bounded internal pages/concurrency; exit 2 means automatic recovery completed but operator repair is still required. | None beyond global or inherited family options. |

### `arcanum backup`

Plan, create, inspect, verify, and list versioned encrypted portable backups. This is a safe local
operation over canonical Arcanum state: it takes a live snapshot through SQLite's online backup API and
does not copy `arcanum.db`/WAL/SHM files directly. It accepts only the typed scopes and components
below; no option admits an arbitrary source path.

The scope catalog is `full` (default), `configuration-and-authored-assets`,
`sessions-and-memory`, `specific-session`, and `metadata-only`. `specific-session` requires the
exact GUID passed to `--session-id`; broader scopes may also record a Session GUID as provenance
without narrowing their inventory. Version 1 includes only matching Session attachments by default
for `specific-session` and omits global uploaded/batch files unless those typed components are
explicitly included. Its physical Grimoire snapshot remains indivisible, so the encrypted manifest
warns about collateral global/accounting rows. Metadata-only creates an encrypted manifest with no
state entries and does not need installation secrets.

The repeatable/multi-value component catalog is `grimoire-database`, `grimoire-kdf-metadata`,
`portable-recovery-keys`, `configuration`, `session-attachments`, `uploaded-files`,
`batch-artifacts`, `global-codex`, `global-spells`, `mcp-configuration`,
`trusted-mcp-workspace-metadata`, `cli-state`, `the-forge-state`, `compendium-settings`,
`compendium-certificates`, `audit-logs`, `guardrail-logs`, and `master-api-key`. Matching is
case-insensitive but otherwise exact; numeric enum spellings and unknown values are rejected.
Duplicates are harmlessly collapsed. If the same component appears in both `--include` and
`--exclude`, exclusion wins. Trusted MCP metadata, both log families, and the master API key are
omitted by default and must be explicitly included.

`compendium-settings` and `configuration` name the same physical `arcanum.json` state. Selecting
only `compendium-settings` captures the file under that component even when `configuration` is
excluded. Selecting both stores one configuration entry and reports `compendium-settings` as a
complete zero-entry alias. The shared planner also records a bounded-stream SHA-256 fingerprint for
every source; creation rejects identity, size, or fingerprint drift before capture, including an
in-place change that preserves the inode and byte count.

Passphrases are never accepted as literal command arguments. With no explicit source, creation and
verification read hidden terminal input; creation also confirms it. `--passphrase-env <name>` reads
the value of that named environment variable. `--passphrase-fd <fd>` reads one UTF-8 line
from an inherited descriptor, including descriptor `0`. When a command consumes a passphrase,
negative descriptors are rejected and the two source options are mutually exclusive. `backup
create --dry-run` consumes neither source, so a parsed negative descriptor or both source flags do
not block its structurally valid inventory plan; parser syntax and type errors still fail before
the handler. Prefer a descriptor for automation when practical, and do not put a secret value
itself in shell-history guidance. The CLI rejects an empty passphrase but does not impose an
arbitrary composition rule. `--json` can return plans/manifests and verification facts, but never
includes the passphrase or portable key bytes.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum backup create` | Create an owner-only `.arcbackup`. The default destination is `~/.config/arcanum/backups/arcanum-<UTC timestamp>.arcbackup`. Required-component, source-identity, staging-identity, checksum, self-verification, cancellation, or publication failure leaves no new archive and returns a non-success result. Existing output is no-clobber by default. | `--scope <scope>` — Typed scope; default `full`.<br>`--session-id <guid>` — Required for `specific-session`; optional provenance for broader scopes.<br>`--include <component>...` — Repeat or supply several typed additions.<br>`--exclude <component>...` — Repeat or supply several typed omissions; exclusion wins over inclusion.<br>`-o, --output <path>` — Explicit `.arcbackup` destination.<br>`--dry-run` — Show the shared inventory plan, estimates, missing/nonportable paths, and warnings without prompting for the recovery passphrase or writing an archive.<br>`--overwrite` — Explicitly permit atomic replacement of an existing destination.<br>`--passphrase-env <name>` / `--passphrase-fd <fd>` — Noninteractive passphrase source. |
| `arcanum backup inspect <archive>` | Read only the bounded safe outer header by default. Decrypted inspection authenticates bounded chunks in memory, skips selected content, and shows the capped final manifest without creating plaintext staging. | `--decrypt` — Decrypt the manifest; prompts securely when neither explicit source is present.<br>`--passphrase-env <name>` / `--passphrase-fd <fd>` — Supply a passphrase source; supplying one also makes the manifest available. |
| `arcanum backup verify <archive>` | Authenticate the complete archive, validate its bounded structure/manifest and every entry size/SHA-256, and validate any Grimoire snapshot in protected temporary storage. Invalid archives return exit `1`; temporary plaintext is removed. | `--passphrase-env <name>` / `--passphrase-fd <fd>` — Optional noninteractive source; otherwise prompt securely once. |
| `arcanum backup list` | List safe outer headers for valid top-level `.arcbackup` files, newest first, without decrypting manifests. Missing directories yield an empty result; malformed/unreadable candidates are omitted. | `--directory <path>` — Directory to scan; defaults to `~/.config/arcanum/backups`. |

The encrypted manifest reports each component as `complete`, `omitted-by-policy`, `unavailable`, or
`failed`, with requested includes/excludes, warnings, files, sizes, and SHA-256 values. The backup
does not resolve environment references or separately export their values, raw OS credential/Data
Protection stores, external workspace trees, daemon registration, or ephemeral process state;
literal values already authored into a selected file remain part of that file. Issue #37 adds no
`backup restore` command: verify the artifact before relying on it and treat database, blobs,
configuration/assets, and portable recovery keys as one recovery generation.
When the configuration component has a committed preset generation, its authenticated entries also
contain the paired `arcanum.preset.json` and `arcanum.preset.rollback.json`; the transient preset
journal is never included. Restore the pair only beside its matching `arcanum.json` during a
coordinated recovery. An incomplete or mismatched pair fails the configuration component. A pending
journal prevents capture of a possibly mid-transaction configuration until preset recovery runs.

### `arcanum data`

Inspect and maintain persisted Arcanum data.

Read-only lifecycle inspection and every destructive retention command use the authenticated host
API; the CLI never opens or mutates the Grimoire directly. `data prune` requires exactly one of
`--dry-run` and `--apply`. Every mutation below prompts in an interactive terminal and requires the
global `--yes` switch when confirmation cannot be obtained; cancellation sends no mutation request.
Human mode prints concise status, settings, plan, and apply summaries. Global `--json` preserves the
exact API payload; `--json --yes data prune --apply` emits one final apply result rather than a
preview/result sequence.
The separate encryption migration, verification, and key-rotation workers are resumable local
operator operations with bounded worker settings.
Inference and guardrail audit writers never delete historical JSONL files on a write; dated-log
age removal is available only through the bounded server-owned `data prune` plan/apply path.

Retention-class matching is case-insensitive and ignores hyphens, underscores, and spaces.
Grouped names such as `attachments`, `workspace-indexes`, `accounting`, and `daemon-history` are
accepted; a typed attachment, batch-file, workspace, or accounting subclass updates the rule that
governs its dependency group. Setting a rule to `disabled` preserves its current day value.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum data status` | Show rows, files, estimated bytes, effective policy, store, and provenance for every typed retention class, plus aggregate totals and categories preserved outside the selected root. | None beyond global or inherited family options. |
| `arcanum data retention show` | Show the effective unified retention settings and per-class rules. | None beyond global or inherited family options. |
| `arcanum data retention set <class> <days\|disabled>` | Set one named typed rule to clamped retention days or disable policy selection for that class. Numeric enum spellings are rejected before confirmation; disabling preserves the prior day value and does not hide status or authorize/deauthorize an explicit item-scoped deletion. | Mutation confirmation; use global `--yes` for automation. |
| `arcanum data prune --dry-run` | Build the current bounded deletion plan without mutating data. The plan reports class rows/files/estimated bytes/derived records, candidates, blockers, active-operation conflicts, and its content-derived `planId`. | `--dry-run` — Required mutually exclusive preview mode. |
| `arcanum data prune --apply` | Fetch and display the exact current plan, confirm its id and totals, then send that `planId` as `expectedPlanId` to a durable, checkpointed apply. The server rechecks blockers/conflicts and verifies each selected candidate's owned rows, derived records, and files after deletion; repeated runs converge. JSON mode keeps the preview silent and emits exactly one final result. | `--apply` — Required mutually exclusive apply mode.<br>Mutation confirmation; use global `--yes` for automation without another step. |
| `arcanum data delete-session <id>` | Delete one session and its Entries, session-scoped attachment metadata/bytes, and derived Entry/attachment indexes. Pinned Entries/context, operator holds, active work, and outstanding accounting state block the plan. | Mutation confirmation; use global `--yes` for automation. |
| `arcanum data delete-attachment <id>` | Delete one attachment version, its encrypted bytes, chunks, embeddings, and index state. A pinned attachment/context blocks deletion; independently retained Saga/Lexicon facts keep typed provenance and report the source unavailable. | Mutation confirmation; use global `--yes` for automation. |
| `arcanum data reset-memory --scope <scope>` | Reset exactly one named store: `entry`, `attachments`, `workspace`, `saga`, or `lexicon`. There is no ambiguous generic memory delete, and numeric enum spellings are rejected before any API request. | `--scope <scope>` — Required explicit memory scope.<br>Mutation confirmation; use global `--yes` for automation. |
| `arcanum data factory-reset` | Delete managed data under the configured Arcanum data root after conflict checks. The prompt explicitly names external backups, registered workspace data outside that root, `arcanum.json`, security credentials, and key material as preserved. Prior terminal operation history is cleared, and the reset leaves its own completed durable-operation marker. | Mutation confirmation; use global `--yes` for automation. |
| `arcanum data encryption [command]` | Migrate, verify, and rotate authenticated encrypted blob storage. | None beyond global or inherited family options. |
| `arcanum data encryption status` | Show encrypted, legacy, invalid, and remaining blob counts. | None beyond global or inherited family options. |
| `arcanum data encryption migrate` | Encrypt every verified legacy plaintext blob through a resumable operation. | `--max-concurrency <max-concurrency>` — Bounded worker count (1-8; default 2).<br>`--max-bytes-per-second <max-bytes-per-second>` — Aggregate I/O throttle in bytes/second (default 67108864). |
| `arcanum data encryption verify` | Verify metadata, envelope authentication, plaintext length, and SHA-256. | `--max-concurrency <max-concurrency>` — Bounded worker count (1-8; default 2).<br>`--max-bytes-per-second <max-bytes-per-second>` — Aggregate I/O throttle in bytes/second (default 67108864). |
| `arcanum data encryption rotate-key` | Create a new key, incrementally re-encrypt, verify, then retire unreferenced prior keys. | `--max-concurrency <max-concurrency>` — Bounded worker count (1-8; default 2).<br>`--max-bytes-per-second <max-bytes-per-second>` — Aggregate I/O throttle in bytes/second (default 67108864). |

### `arcanum use`

Select persistent local CLI context defaults.

Writes owner-local active CLI defaults. It never mutates the selected Campaign, Workspace, Model, or Session record.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum use campaign <identifier>` | Select an active campaign. | None beyond global or inherited family options. |
| `arcanum use workspace <identifier>` | Select an active workspace. | None beyond global or inherited family options. |
| `arcanum use model <identifier>` | Select an active model. | None beyond global or inherited family options. |
| `arcanum use session <identifier>` | Select an active session. | None beyond global or inherited family options. |
| `arcanum use clear [<scope>]` | Clear all saved context when scope is omitted, or clear campaign, workspace, model, or session context. | None beyond global or inherited family options. |

### `arcanum context`

Inspect effective CLI context and its sources.

Explains the effective values and previews model context without running main inference. Content remains hidden unless `--show-content` is supplied.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum context current` | Show effective campaign, workspace, model, and session context. | None beyond global or inherited family options. |
| `arcanum context inspect [<prompt>...]` | Inspect the complete effective turn context without running main inference. | `--show-content` — Include model-visible content for explicit operator inspection.<br>`--no-retrieval` — Skip embedding and RAG retrieval work.<br>`-c, --campaign <campaign>` — Campaign GUID or name; defaults to saved/detected context.<br>`-w, --workspace <workspace>` — Workspace ID or path; defaults to saved/detected context.<br>`-m, --model <model>` — Model name; defaults to saved/server context.<br>`-s, --session <session>` — Session GUID, title, or prefix; defaults to saved context. |
| `arcanum context tools [<prompt>...]` | Inspect effective turn tools without main inference. | `--show-content` — Include model-visible content for explicit operator inspection.<br>`--no-retrieval` — Skip embedding and RAG retrieval work.<br>`-c, --campaign <campaign>` — Campaign GUID or name; defaults to saved/detected context.<br>`-w, --workspace <workspace>` — Workspace ID or path; defaults to saved/detected context.<br>`-m, --model <model>` — Model name; defaults to saved/server context.<br>`-s, --session <session>` — Session GUID, title, or prefix; defaults to saved context. |
| `arcanum context sources [<prompt>...]` | Inspect effective turn sources without main inference. | `--show-content` — Include model-visible content for explicit operator inspection.<br>`--no-retrieval` — Skip embedding and RAG retrieval work.<br>`-c, --campaign <campaign>` — Campaign GUID or name; defaults to saved/detected context.<br>`-w, --workspace <workspace>` — Workspace ID or path; defaults to saved/detected context.<br>`-m, --model <model>` — Model name; defaults to saved/server context.<br>`-s, --session <session>` — Session GUID, title, or prefix; defaults to saved context. |

### `arcanum mana`

Estimate the effective turn token allocation without main inference.

Shows the same effective context budget used by inference without executing the main model turn.

**Syntax:** `arcanum mana [<prompt>...]`

| Option | Meaning |
|---|---|
| `--show-content` | Include model-visible content for explicit operator inspection. |
| `--no-retrieval` | Skip embedding and RAG retrieval work. |
| `-c, --campaign <campaign>` | Campaign GUID or name; defaults to saved/detected context. |
| `-w, --workspace <workspace>` | Workspace ID or path; defaults to saved/detected context. |
| `-m, --model <model>` | Model name; defaults to saved/server context. |
| `-s, --session <session>` | Session GUID, title, or prefix; defaults to saved context. |

### `arcanum preset`

Inspect, preview, apply, and reset transparent onboarding presets.

The six built-in version-1 presets are `general-assistant` (**General Assistant**),
`coding-workspace` (**Coding Workspace**), `research` (**Research**), `private-offline`
(**Private/Offline**), `automation` (**Automation**), and `advanced-custom`
(**Advanced/Custom**). `<name>` accepts an exact ID or exact display name; quote display names that
contain spaces or shell punctuation. Definitions are partial overlays: only their declared owned
paths can change, and Advanced/Custom owns none.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum preset list` | List each ID, version, display name, purpose, and effective state. The active item is `Active` or `Drifted`; other entries are `Available`. | None beyond global or inherited family options. |
| `arcanum preset show <name>` | Show the shared purpose, exact owned values, restart flags, enables/disables, security and provider requirements, resource/cost behavior, prerequisite setup commands, first essential choice, deferred advanced features, recommendations, and Ward/Sanctum/Weave/Saga/Lexicon glossary. | None beyond global or inherited family options. |
| `arcanum preset diff <name>` | Read-only plan showing applicability, idempotency, prerequisite status, completion summary, and every owned persisted/effective/proposed value with its source, environment-override name/effectiveness, ownership, and restart/change flags. | None beyond global or inherited family options. |
| `arcanum preset apply <name>` | Build and canonically validate the complete candidate, then atomically write only the preset-owned overlay with provenance and rollback state. Reapplying the same version and owned values is a successful no-op. | None beyond global or inherited family options. |
| `arcanum preset reset` | Restore unchanged preset-owned values to their pre-apply baseline, preserve user drift and all unrelated settings, clear active provenance, and report restored/preserved counts plus rollback status. No active preset is a successful no-op. | None beyond global or inherited family options. |

Plain and `--json` modes are projections of the same shared service. Secret-shaped canonical values
are `***`; environment-variable names may be shown, but their values never are. Persisted value
means the value in `arcanum.json`; effective value includes recognized environment layering;
proposed persisted value is what apply would write. When an environment override is effective,
the persisted value can change without an effective-value change, and `diff` reports both flags
instead of misrepresenting runtime truth. Only an effective override that contradicts an owned
safety/privacy boundary blocks Apply. Benign feature masks remain authoritative and are reported as
drift without making the plan inapplicable. The secure research-credential store is consulted only
for Research `diff` and `apply`; listing, showing, state inspection, reset, and other presets do not
probe it.

Preset state is separate owner-only provenance, not a setting: no provenance is `Custom`, an exact
owned-value match is `Active`, and a later persisted or effective difference is `Drifted`. Apply
uses an expected-settings hash, the current-user cross-process coordinator shared by all canonical
configuration writers, an owner-only rollback baseline, a prepared transaction journal, atomic
replacement, and post-write verification. The journal stores only owned before/after values and
hashes plus previous/next provenance. Bounded no-follow sidecar reads and exact catalog ownership,
value, hash, and state/rollback validation reject forged or stale provenance. Reset and recovery
restore a baseline path only while its persisted value still matches the transaction's applied
value; manual drift and unrelated edits win. Apply/reset are already explicit mutation commands and
do not prompt or require `--yes`.

Required provider/model, workspace, research-credential, loopback-provider, or positive-budget
prerequisites are reported with exact setup commands. A plan applies only when required
prerequisites and complete canonical validation succeed. Presets never supply provider secrets,
invent budgets, bypass Ward or Sanctum, silently enable network exposure/unsandboxed children/
untrusted MCP/destructive memory, or add retry, timeout, loop-count, or other arbitrary tuning
knobs. Every plan concludes with active preset, provider/model, workspace/campaign, memory sources,
tool policy, privacy state, and next recommended command. Recommendations are directly executable;
Coding Workspace uses
`arcanum run --workspace . "Inspect this workspace and summarize it."`, including the required
prompt.

The future guided `arcanum setup` wizard is tracked separately by issue #19. These commands expose
the reusable preset service directly; they do not simulate or add that wizard.

### `arcanum config`

Safely inspect, validate, edit, and open Arcanum configuration.

Inspects and changes `arcanum.json` through descriptor-backed parsing and validation. `get` and
`set` use dotted descriptor paths such as `host.port` or `providers.0.endpoint`. Secrets stay
redacted; sensitive provider endpoint values come from stdin or a hidden prompt.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum config path` | Print the exact arcanum.json path. | None beyond global or inherited family options. |
| `arcanum config show` | Show the effective configuration with secrets redacted. | None beyond global or inherited family options. |
| `arcanum config get <key>` | Show one value selected by its dotted descriptor path. | None beyond global or inherited family options. |
| `arcanum config set <key> [<value>]` | Parse, validate, and atomically set one dotted descriptor path; sensitive values use redirected stdin or a hidden prompt. | None beyond global or inherited family options. |
| `arcanum config validate` | Validate the complete effective configuration without writing it. | None beyond global or inherited family options. |
| `arcanum config edit` | Edit an owner-only temporary copy, validate it, and atomically apply it. | None beyond global or inherited family options. |
| `arcanum config open` | Launch Compendium for visual configuration editing. | None beyond global or inherited family options. |

### `arcanum watch`

Follow an authenticated Arcanum live stream with consistent terminal or NDJSON output.

Provides independent authenticated watchers for session, Apprentice, log, MCP, daemon, and health sources. SSE streams are not merged; `--json` emits source events as NDJSON while diagnostics remain on stderr.

These family options are accepted by the subcommands shown in their generated help:

| Option | Meaning |
|---|---|
| `--reconnect` | Reconnect after an unexpected disconnect with capped exponential backoff; possible event gaps are always reported. |
| `--event-type <event-type>` | Show matching event types; repeat for multiple free-form, case-insensitive values. |
| `--tool, --tool-name <tool>` | Show events for matching tool names; repeat for multiple free-form, case-insensitive values. |

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum watch session [<session>]` | Follow replayed and live Session entries. | `--since <since>` — Begin after this Session Entry GUID. |
| `arcanum watch apprentice [<apprentice>]` | Follow an Apprentice Chronicle. | None beyond global or inherited family options. |
| `arcanum watch logs` | Follow live host log entries. | `--level <level>` — Minimum server log level (free-form; validated by the API).<br>`--category <category>` — Match a log category, case-insensitively.<br>`--search <search>` — Search log messages and categories, case-insensitively. |
| `arcanum watch mcp` | Follow live MCP server lifecycle events. | None beyond global or inherited family options. |
| `arcanum watch daemons` | Follow live Unseen Servant daemon events. | None beyond global or inherited family options. |
| `arcanum watch health` | Poll authenticated host health snapshots. | `--interval <interval>` — Seconds between health observations (default: 5; any positive integer). |

## Compatibility aliases

| Alias | Canonical command |
|---|---|
| `arcanum workspace get` | `arcanum workspace show` |
| `arcanum mcp get` | `arcanum mcp show` |
| `arcanum session get` | `arcanum session show` |
| `arcanum session watch` | `arcanum watch session`; preserves the legacy selector/`--since` surface and does not add the root watch filter/reconnect options. |
| `arcanum apprentice chronicle` | `arcanum watch apprentice`; preserves the legacy selector surface and does not add the root watch filter/reconnect options. |
| `arcanum campaign use` | `arcanum use campaign` |
| `--tool-name` on watch commands | `--tool` |
| Camel-case prompt aliases such as `--campaignId` and `--sessionId` | Their kebab-case forms, retained for compatibility. |

## Watch stream details

Watch terminal output uses UTC timestamps and source-specific colors. The shared SSE parser joins multi-line `data:` fields, treats comment frames as stderr liveness diagnostics, and treats `[DONE]` as successful completion. `--json` writes only compact source event objects to stdout.

`--reconnect` retries network failures, unexpected EOF, and transient HTTP 408/425/429/5xx responses with exponential delays capped at 30 seconds. Authentication, validation, not-found, and connection-cap denials are terminal. Every reconnect warns that events may have been missed; only session watch carries the last valid Entry ID forward, and that cursor is not a replay guarantee.

## Related documentation

- [`Arcanum.README.md`](Arcanum.README.md) — installation and quick-start workflows.
- [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md) — architecture, ownership, security, and implementation rationale.
- [`Arcanum.API.md`](Arcanum.API.md) — HTTP routes, wire shapes, status mapping, and public error codes.
- [`Arcanum.DEBUGGING.Human.md`](Arcanum.DEBUGGING.Human.md) — operator troubleshooting.
