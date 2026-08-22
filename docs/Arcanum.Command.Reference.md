# Arcanum Command Reference

This is the canonical user-facing reference for the Arcanum CLI command tree, arguments, options, aliases, interactive commands, output modes, and exit behavior. It is verified against the live `System.CommandLine` tree in `RetroDownfall.Arcanum.Cli`; architectural rationale remains in [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md), and HTTP wire contracts remain in [`Arcanum.API.md`](Arcanum.API.md).

## Invocation and notation

Use `arcanum [global-options] <command> [command-options]`. In the syntax tables, `<value>` is required, `[<value>]` is optional, and `<value>...` accepts remaining tokens. Run `arcanum --help` or `arcanum <command> --help` for the executable's current short help.

Every option and argument reachable from the root command carries a help description, and the descriptions in this reference are the same strings the parser reports. A symbol declared without a description fails the build, so `--help` and this document cannot diverge.

`<command> --help` ends with an `Examples:` section for every runnable command. Those examples are parse-tested against the live tree, so a renamed verb or removed option breaks the build rather than shipping help that no longer works. A command that deliberately has no safe example — a credential write, an irreversible deletion, an OS-service registration — says so and why, instead of appearing to have been forgotten.

Use the standard `--` end-of-options marker before positional text that begins with a hyphen; for example, `arcanum run -- --explain-this` treats `--explain-this` as the prompt. Without it, an option-shaped word in the free-text prompt of `run` or `context inspect|tools|sources|cost` is a command-line error (exit 2) rather than prompt text — a mistyped `--dry-run` must not quietly become a live turn. A word that merely starts with a digit after the dash, such as `-40 degrees in Fahrenheit?`, is ordinary prompt text and needs no marker.

Options marked repeatable may be supplied more than once. System.CommandLine response-file expansion is disabled: an `@filename` value is application syntax only where this reference says the command reads from a file. Supported values include spell bodies and execution input, prompt templates and execution input, Apprentice goals/plans, Trial inquisitors, and MCP/tool invocation JSON. Redirected stdin is used where explicitly documented, notably `run`, secret, and tool-argument input.

## Global options

| Option | Meaning |
|---|---|
| `--output-format <text\|json>` | Select the output shape. `json` makes non-streaming commands emit one JSON payload; watch streams emit one source event per line as NDJSON. Diagnostics and progress remain on stderr. |
| `--json` | Shorthand for `--output-format json`. Combining it with `--output-format text` is a contradiction and exits `2`. |
| `--plain` | Disable ANSI color, styling, and terminal animations without changing persisted configuration. |
| `--yes` | Automatically approve commands that otherwise require confirmation, including overwrites and explicit deletion flows. It is the only automatic confirmation switch; it does not change unrelated mutations. |
| `--no-context` | Ignore saved `cli-context.json` defaults for this invocation. Independent current-directory Campaign/Workspace detection still applies. |
| `-p`, `--print` | Headless mode. A real terminal behaves like a redirected one: no interactive picker opens, no prompt blocks, and no color is emitted. Use it so a scripted invocation cannot stall on a terminal it happens to be attached to. |
| `-v`, `--verbose` | Emit additional operator diagnostics on stderr. It never changes payload content or the exit code. |
| `-?`, `-h`, `--help` | Show help for the current command path and exit without running it. |
| `--version` | Show the CLI version. This option is available at the root command. |

The Arcanum process options (`--output-format`, `--json`, `--plain`, `--yes`, `--no-context`, `--print`, and `--verbose`) are recursive and may appear before or after subcommands. Help aliases are available at every command path; `--version` is root-only.

### Short-option contract

A short flag means exactly one thing everywhere in the tree. Claude Code parity does not justify ambiguous parsing, so `-c` is `--continue` at every scope and `--campaign` takes `-C`. The complete table is generated from the live parser into [`Arcanum.CommandMap.json`](Arcanum.CommandMap.json) and verified by test.

| Short | Long | Notes |
|---|---|---|
| `-c` | `--continue` | Continue the most recent Session. |
| `-C` | `--campaign` | Campaign selector. Uppercase because `-c` is Claude-aligned continuation. |
| `-r` | `--resume` | Resume a named Session; omit the value for a picker. |
| `-p` | `--print` | Headless marker (recursive). |
| `-v` | `--verbose` | Extra stderr diagnostics (recursive). |
| `-m` | `--model` | Model selector. |
| `-s` | `--session` | Session selector. |
| `-w` | `--workspace` | Workspace selector. |
| `-n` | `--new` | Start without continuing a Session. |
| `-o` | `--output` | Destination file path. |
| `-q` | `--query` | Free-text query. |
| `-t` | `--title` | Title value. |

`--output-format` deliberately has no short form: `-o` already means `--output`, and a second meaning would reintroduce exactly the ambiguity this table exists to prevent.

## Shared selection and context behavior

Resource-taking commands resolve an explicit exact ID first, then an exact case-insensitive name, then a unique case-insensitive name prefix. Omitted selectors may open a searchable picker only when stdin and stdout are interactive and output is not JSON. Cursor catalogs are followed until exhaustion, cancellation, a fetch error, or a repeated-token no-progress failure; there is no 100-page product ceiling. Redirected, ambiguous, or cancelled selection never guesses.

Effective inference context precedence is: explicit command option, saved active CLI context, current-directory detection, then server default. `--no-context` skips only the saved-context layer. Workspace paths are always paths on the server host, even when the bundled client and host run on the same machine.

## Output and exit behavior

| Exit code | Meaning |
|---:|---|
| `0` | Success, normal stream completion, or a cancelled interactive picker that performed no action. |
| `1` | Generic validation, API, execution, failed Trial, or unexpected stream-disconnect failure. |
| `2` | Command-line/configuration failure, a confirmation that cannot be obtained non-interactively, a non-positive watch-health interval, an unrecognized `doctor` diagnostic or repair id, or an operation reconciliation that still requires operator attention. |
| `3` | Network failure where the command exposes the public network exit classification. |
| `130` | Caller cancellation or Ctrl+C for non-interactive streaming/watch commands. Ctrl+C typed into a streaming `ask_human` prompt counts: the prompt captures it as a keystroke rather than letting it raise SIGINT, and the interrupt is handed back to the command so the turn unwinds instead of waiting for a question the operator has abandoned. In Command Center, Ctrl+C cancels the active turn and returns to the composer. |

Structured stdout is never mixed with diagnostics: every command renders its failures on stderr, so redirecting stdout to a file captures the payload and nothing else, and `--output-format json` puts exactly one document there. `--plain` strips presentation only; it does not change payload content. Watch reconnect is opt-in and always warns that a gap may exist.

Long-running turns, commands, research, indexing, and durable operations are not assigned an Arcanum-owned expected duration. They continue while they complete work and emit progress; Ctrl+C or the corresponding `cancel` command is the normal operator stop. A local page, frame, buffer, or checkpoint bound protects one allocation only and must expose or automatically follow its continuation. Retained-boundary diagnostics name the owner, safe measurement/limit, saved or checkpointed state, and exact continuation or recovery action.

When `arcanum.json` cannot be loaded at all — malformed JSON, an I/O failure, or a permissions failure — the parse error and the remedy `Run 'arcanum config edit' to repair <path>, or 'arcanum doctor' for full diagnostics.` go to stderr before the dispatcher exists. The repair and diagnosis verbs still run on defaults so they can name the fault: `doctor`, `config`, and `help`, each located by the parser rather than by position, so a leading global option (`arcanum --json doctor`) does not defeat the one command that can diagnose the file; and `--help`, `-h`, `-?`, `/?`, or `--version` anywhere before `--`, since everything after `--` belongs to the command being run rather than to Arcanum. Every other invocation, including a bare `arcanum`, exits `2` without dispatching. No invocation aborts the process on an unloadable configuration file.

Command-specific refinements:

- `serve` returns `2` when host startup configuration validation fails. The validation message, one `  - <pointer>: <detail>` line per failing pointer, and the remedy `Run 'arcanum config validate' to re-check, or 'arcanum config edit' to repair arcanum.json.` are written to stderr, so the per-pointer detail is on the console and not only in the rolling JSON log.
- `run` returns `0` when its selected route or dry-run preview succeeds, `1` for a live execution/stream/API failure, `2` for invalid input, staging, context, or route selection, and `130` when cancelled. An over-limit redirected input exits `2` before dispatch and is never truncated. Supplying more than one of `--session`, `--continue`, and `--resume` exits `2`, as does `--continue` with no previous Session to continue.
- Bare Command Center returns `0` after `/exit` or `/quit`, for non-interactive usage, and when `ARCANUM_NO_COMMAND_CENTER=1`; terminal-size or UI-bootstrap failure returns `1`.
- `center` and `open center` return the same in-process Command Center result. Resource/application launch commands return `0` after a successful start or cancelled picker, `1` when selection or launch fails, and `130` for caller cancellation.
- Watch commands return `0` on normal completion, `2` on parse failure or a non-positive health interval, `1` on validation/API/unexpected-disconnect failure, and `130` on cancellation.
- `completion <shell>` returns `0` after writing a script to stdout and `2` for an unsupported shell. `completion install` returns `0` after writing or after a declined confirmation (having changed nothing), `2` when confirmation cannot be obtained non-interactively, and `1` when the target cannot be written. `completion resolve` always returns `0`: it is called from a shell keystroke, so an unavailable host yields no suggestions rather than an error. It writes one candidate per line, which is the only separator bash, zsh, fish, and PowerShell all split on.
- `help <topic>` returns `0`, or `2` for an unknown topic.
- A removed or mistyped command exits `2` with a diagnostic naming the canonical replacement or the nearest command. A suggestion is printed only; it is never executed. The mistyped verb is located by the parser rather than by position, so `arcanum campain list` names `campaign` and a leading global option (`arcanum --json campain`) does not hide the diagnostic. A parse that failed for any other reason — a missing argument, or a value outside a closed set such as `help <topic>` — keeps System.CommandLine's own message, which names the argument or lists every legal value.
- `doctor --fix-permissions` returns `0` unless the permission repair itself failed, matching its pre-existing contract; the diagnostic it now prints alongside does not change its exit code.
- `doctor` returns `0` when every diagnostic is `Healthy` or `Skipped`, and also when one is `Degraded` or `Unavailable` — an unreachable host has always been a warning, and a diagnostic that failed the build whenever `arcanum serve` was not running would be unusable in CI. `--strict` promotes `Degraded` and `Unavailable` to `1`. Any `Unhealthy` diagnostic, or any repair that reaches `Failed`, returns `1`. An unrecognized `--only`/`--skip`/`--repair` id, or `--apply` without `--repair`, returns `2`. `3` is deliberately never returned: provider and host unreachability are findings, not command failures. A declined repair confirmation returns `0` having changed nothing, matching the other mutation commands. A `--only`/`--skip` combination that selects no diagnostic at all returns `2` rather than an empty report claiming health, and a repair id passed to `--only`/`--skip` returns `2` naming `--repair` instead.
- `trial run` returns `1` when the completed Trial result is not passing, independently of HTTP or validation failure.
- `operation reconcile` returns `2` when all recoverable pages were processed but one or more operations still require operator repair; otherwise it returns `0`.
- `backup create` returns `1` for an incomplete result and never labels it complete or publishes an archive; `backup verify` returns `1` when authentication, structure, checksums, or database verification fail. Typed backup-plan validation returns `2`. Commands that consume a passphrase also return `2` for invalid or conflicting passphrase-source options; `backup create --dry-run` does not consume or semantically validate those source options.
- `preset list` still returns `0` when definitions can be listed but effective-state inspection is unavailable; that diagnostic stays on stderr and state is shown as unavailable. Unknown presets, missing prerequisites, invalid complete candidates, stale configuration, failed apply/reset, or failed rollback return `2`; a `Connection.*` service failure returns `3`.

## Handler-validated required values

Some options are nullable in the generated parser so handlers can resolve saved context, read a secure value, or produce a better error. The following requirements are therefore enforced after parsing even when short help displays square brackets:

| Command | Runtime requirement |
|---|---|
| `run` | A positional/interactive instruction, non-empty redirected stdin, or at least one valid `--with @path` source is required. At most one of `--session`, `--continue`, and `--resume` may be supplied, and `--continue` requires a previous Session. |
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

`arcanum center` is the explicit alias, and `arcanum open center` reaches the same in-process host. Unlike the automatic bare launch, an explicit request is not suppressed by `ARCANUM_NO_COMMAND_CENTER`; the normal terminal and UI requirements still apply. All three accept `-c`/`--continue` to reopen the most recent Session and `-r`/`--resume [<id>]` to reopen a named one, matching the one-shot entry.

Command Center is the only interactive turn entry. A terminal that cannot host it — redirected stdin or stdout, `ARCANUM_NO_COMMAND_CENTER=1`, or a window under 80×12 — gets usage naming `arcanum run` rather than a degraded second REPL.

Interactive auto-start uses short connection/readiness observation only: two seconds per health probe, three seconds for an already-listening unhealthy host, and 20 seconds after spawn. A launcher timeout never kills the spawned host; retry, run `arcanum doctor`, verify `arcanum key show`, or inspect `~/.config/arcanum/logs/auto-serve-bootstrap.log`.

### The model drop-down

`/model <name>` needs you to know the model id before you can type it. That is fine for models you wrote into `arcanum.json` yourself, but a Familiar's catalogue belongs to the vendor and changes without a configuration edit, so Command Center also carries a model control in the header.

It is a full focus region: `Tab` / `Shift+Tab` reach it alongside Composer, Sessions, Transcript, and Incantations. `Enter`, `Space`, or `↓` opens it; typing narrows by model name or provider name; `↑`/`↓` (or `k`/`j`) move; `Enter` selects; `Esc` cancels back to the composer. No mouse anywhere.

The list is `GET /api/models`, so it spans every provider kind, groups by provider, marks the model prompts currently go to, and already excludes anything on a Familiar's `hiddenModels` list. Selecting sets exactly the session model `/model <name>` sets — the two cannot disagree. On a terminal under 72 columns the control is not rendered and drops out of the `Tab` cycle; `/model <name>` is unchanged, and so is `-m` / `--model` on `arcanum run`.

### Slash commands

One registry defines every slash command, its help text, and the canonical replacement for each removed spelling. Names track Claude Code wherever a direct analog exists; thematic names survive only where the capability has no Claude analog at all.

| Command Center input | Action |
|---|---|
| `/help`, `/?` | Show Command Center help, rendered from the registry. |
| `/keys` | Show keyboard shortcuts. |
| `/status` | Show current session and serve status. |
| `/doctor` | Run a compact health check. |
| `/clear` | Start a fresh session thread and clear the transcript view. |
| `/compact` | Queue memory consolidation for the current session. |
| `/context` | Show the effective turn context and token allocation. |
| `/cost` | Show token and spend totals for the current session. |
| `/memory` | Show the compressed Campaign Summary for this session. |
| `/config` | Show the effective configuration summary and its file path. |
| `/model [<name>]` | With no name, list configured models; with a name, select it for this session. The header model drop-down sets the same session model — see below. |
| `/provider list` | List configured providers. |
| `/mcp [reload]` | Show MCP server status, or reload MCP configuration. |
| `/tools` | Show native tools. |
| `/arsenal` | Show the effective workspace arsenal. |
| `/look` | Show the working directory; the full Eye of the World snapshot is `arcanum look`. |
| `/resume <id>` | Load a transcript and continue the selected session. |
| `/session list` | Refresh and list sessions. |
| `/session archive <id>` | Archive a session; archiving the active one starts a fresh thread. |
| `/campaign list [offset]` | List a 50-line terminal page of campaigns. When more state exists, the result prints the exact next offset command. |
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
| `/pins` | List persistent session context pins. |
| `/pin <kind> <target>` | Pin a file, directory snapshot, symbol range, session entry, attachment, URL, or diagnostic. |
| `/unpin <id>` | Remove one context pin. |
| `/spell list [cursor]` | List a 50-line terminal page of spells with exact next-cursor continuation. |
| `/ward list [offset]` | List a 50-line terminal page of open Wards with exact next-offset continuation. |
| `/ward allow [<id>]` | Allow the supplied Ward. |
| `/ward deny [<id>]` | Deny the supplied Ward. |
| `/exit`, `/quit` | Leave Command Center. |

The Ward confirmation modal is answered with its own keys — `Enter`/`A` always allow this tool for the session, `O` allow once, `Esc`/`D` deny — never with a slash command: while the modal is displayed it owns the keyboard and swallows every printable key, so its choice list names only keys it actually handles.

Persistent pins use `/pins`, `/pin`, and `/unpin` rather than overloading `/context`, because `/context` is the Claude-aligned context-window view. An unrecognized slash command names the canonical replacement when the spelling was removed, and otherwise suggests the nearest registered name; it is never executed automatically. `/context` takes no sub-command: the removed `/context list|pin|unpin` forms are denied with a message naming `/pins`, `/pin <kind> <target>`, and `/unpin <pin-id>`.

List offsets must be nonnegative integers. Campaign pages are fetched from the API at the requested offset; Spell and Ward pages slice the complete fetched state for terminal rendering. When another page exists, Command Center states that server/durable state was not changed and prints the exact next command instead of silently truncating the list.

The Sessions pane keeps one 40-session page and the Transcript pane keeps one 200-entry page; these are view allocations, not history totals. In Sessions, `Ctrl+PgDn` loads older sessions and `Ctrl+PgUp` returns toward recent sessions. In Transcript, `Ctrl+PgUp` loads older entries and `Ctrl+PgDn` returns toward the latest entries. Paging uses exact server cursors/offsets, refuses a repeated or missing checkpoint as no progress, honors cancellation, and rebuilds Incantations from the current transcript page.

## Turn entry points

Arcanum has exactly two ways to start a turn, and neither is a second implementation of the other:

| Entry | Use |
|---|---|
| Bare `arcanum` (Command Center) | Interactive work. The analog of bare `claude`. |
| `arcanum run [prompt…]` | One-shot and scripted work. The analog of `claude -p`. |

Continuation is spelled the same on both: `-c`/`--continue` for the most recent Session, `-r`/`--resume [<id>]` for a named one. The `session` family is management only — it lists, shows, forks, renames, exports, and compacts Sessions, and it never starts a turn.

**Campaign resolution is verified, not inferred.** Every turn entry point resolves its Campaign through the one canonical resolver before any inference happens, so `--session`, `--campaign`, and the working directory must agree. A resumed Session that names a different Campaign than the current directory is a typed conflict rather than a silent choice between them, and a `--session` value naming a Session that does not exist is an error rather than a quietly created new conversation. Both exit `1` with the error code on stderr, or on stdout under `--json` ([DESIGN §10.12](Arcanum.DESIGN.md#1012-covenant-invocation-authority-and-campaign-binding)). No dedicated `arcanum memory covenant` command is registered yet. The CLI payloads and the contract they answer to are frozen, and the authenticated HTTP boundary those commands will call through is in place ([API §8.29](Arcanum.API.md#829-x-arcanum-context-policy-and-the-covenant-pre-binding-boundary)) and the `Arcanum:Features:Covenant` gate, but not the dedicated management routes or commands themselves. The existing `arcanum data reset-memory --scope covenant` lifecycle command is the deliberate exception described below. One consequence is visible today: `--no-context` still suppresses context the way it always has and does **not** yet emit `X-Arcanum-Context-Policy: none`, so a stateless turn is not yet eligible for the explicit-none response-cache arm. The frozen shape of the dedicated management contract is recorded under [`arcanum memory covenant`](#arcanum-memory) below.

## CLI command tree

Top-level families group as follows. The core mirrors Claude Code; the rest is Arcanum-specific capability that Claude Code has no analog for and that this reference does not attempt to reduce.

| Group | Families |
|---|---|
| Core | *(bare)* Command Center, `center`, `run`, `serve`, `setup`, `config`, `mcp`, `doctor`, `key`, `completion`, `help` |
| Inspection | `context`, `watch`, `look`, `operation`, `data`, `open` |
| Domain | `campaign`, `session`, `saga`, `memory`, `spell`, `prompt`, `ward`, `trial`, `apprentice`, `conclave`, `lore`, `daemon`, `model`, `provider`, `workspace`, `tool`, `attachment`, `backup`, `preset`, `use` |
| Web and bulk | `search`, `browse`, `research`, `file`, `batch` |

A family stays top-level when it owns durable server state or a distinct lifecycle. Anything that only modifies how one turn runs is an option on `run`, and anything that only makes sense inside a live session is a slash command, not a verb.

### `arcanum setup`

Guided, resumable first-run setup: provider, credentials, workspace, and preset.

Runs an explicit state machine over eight steps — runtime edition and privacy posture, provider endpoint and model, provider credential, optional web-research credential, live provider validation, workspace and Campaign, onboarding preset, and the final diff — then commits. Every answer stays in an in-memory draft until the final plan is accepted, so Ctrl+C, end of input, a validation failure, or a failed dependency check leaves the prior configuration, credentials, CLI context, and workspace registry unchanged. The wizard composes the existing authorities (canonical configuration reader/validator/atomic writer, outbound endpoint guard, OS-backed credential stores, preset engine, CLI context store); it does not introduce a second configuration model.

The wizard authors OpenAI-compatible provider endpoints, including Ollama and other local model servers through their own `/v1` endpoint. The provider templates are OpenAI, Local/Ollama, and a custom endpoint you supply. A Familiar (`ClaudeCodeCli` / `CodexCli`) has no endpoint and no credential to collect, so add one in Compendium or by editing `arcanum.json`; `arcanum doctor` then reports whether it is installed and signed in.

Live validation performs one guarded `GET {endpoint}/models` with a strict five-second timeout. It is non-billable — no completion is requested, so validation never spends inference tokens — and it runs in-process, so it works before `arcanum serve` has ever started. Results distinguish endpoint rejection by the outbound guard, TLS/certificate failure, authentication failure, model absence, malformed response, timeout, and unreachable host.

**Syntax:** `arcanum setup [options]`

| Option | Meaning |
|---|---|
| `--plan` | Compute and print the plan without writing anything. Exits `2` when the plan is not applicable. |
| `--apply` | Apply the plan without prompting. Mutually exclusive with `--plan`. |
| `--preset <preset>` | Onboarding preset ID to apply (see `arcanum preset list`). |
| `--provider <name>` | Provider name to create or update. |
| `--endpoint <url>` | OpenAI-compatible provider endpoint, including the `/v1` suffix. |
| `--model <model>` | Default model advertised by the provider. |
| `--provider-key-env <variable>` | Environment variable holding the provider API key. No secret is read or stored. |
| `--provider-key-stdin` | Read the provider API key as the first line of redirected stdin and store it securely. |
| `--no-provider-key` | Delete any stored provider credential, for keyless local model servers. |
| `--research` | Enable (`true`) or skip (`false`) the Perplexity web-research credential step. |
| `--research-key-env <variable>` | Environment variable holding the web-research API key. No secret is read or stored. |
| `--research-key-stdin` | Read the web-research API key from redirected stdin, after the provider key when both are supplied. |
| `--workspace <path>` | Default workspace root to record in configuration. |
| `--campaign <name>` | Campaign name recorded in the completion summary and CLI context. |
| `--edition <edition>` | Runtime edition: `local` or `development`. |
| `--listen-any` | Privacy posture: bind all network interfaces (requires HTTPS) instead of loopback. |
| `--allow-unreachable-provider` | Commit even when live validation fails, for air-gapped hosts or a local server that is not running yet. |

Secrets are never accepted as arguments. A credential may only arrive on redirected stdin (`--provider-key-stdin`, `--research-key-stdin`) or as an environment-variable reference (`--provider-key-env`, `--research-key-env`); nothing that carries a secret appears in argv, the process table, or shell history.

The wizard owns exactly these configuration paths: `edition`, `host.listenAny`, `defaultModel`, `workspaces.defaultRoot`, the selected `providers[]` entry, and — only when an environment reference is chosen — that entry's `credentialEnvironmentVariable` plus `integrations.webResearch.credentialEnvironmentVariable`. Every other persisted value is carried through untouched, so re-running setup and accepting the current values is a no-op rather than a reset. Provider endpoints are sensitive configuration values: the diff masks them and the completion summary reports only the endpoint class (`Loopback`, `PrivateNetwork`, `Public`, or `Unknown`).

The commit is ordered by dependency: credentials first (the preset engine reads them when evaluating prerequisites), then the validated configuration, then the preset, then the CLI context selection. On failure the wizard restores the previous configuration and deletes any credential this run created. A credential that *replaced* an existing one cannot be restored — the wizard never reads a prior credential value — and a credential the run *cleared* cannot be recovered from Arcanum at all, because both the OS entry and its encrypted mirror are deleted; each of those cases is reported as an actionable partial-commit state naming the exact `arcanum key provider set <provider>` command to run. Cancelling the wizard while the commit is in flight is handled the same way once anything irreversible has landed: the commit unwinds what it can and reports the partial-commit state instead of claiming nothing changed.

The completion summary reports the active preset, provider and model, endpoint class, workspace and Campaign, enabled network and memory capabilities, tool security posture, privacy state, and the exact next command to run.

Exit codes follow the standard table: `0` when the plan is applicable (`--plan`) or committed (`--apply`), `2` for an inapplicable plan, invalid input, or a failed commit, and `130` when the interactive wizard is cancelled before the commit begins. A cancellation that interrupts the commit after part of it has been applied is a failed commit, so it reports the partial-commit state and exits `2`.

### `arcanum open`

Launch Command Center, The Forge, or Compendium, optionally at one server-owned resource. Resource selectors use the shared ID/exact-name/unique-prefix behavior and finish before any application process starts. A cancelled picker performs no launch and returns success; an ambiguous or failed selection reports the selector error and performs no launch.

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

The launch envelope is versioned and contains only the target application, resource kind, canonical server resource identifier, optional opaque Workspace scope ID, initial view, and optional connection profile ID. API keys, endpoints, prompt or file content, attachments, and server paths never enter the child process arguments. The launcher passes the encoded envelope as one `ProcessStartInfo.ArgumentList` value without shell interpolation, so spaces, quotes, Unicode, and leading hyphens cannot create additional arguments.

The launcher-only `--arcanum-deep-link` argument is consumed before the normal CLI parser. For a Command Center target, a target-only envelope enters the current host and a Session envelope resumes the canonical Session GUID. Malformed, wrong-target, or unsupported-resource envelopes fail with a fixed diagnostic without reproducing their private payload.

Discovery checks platform application bundles and executables, including Windows/Linux release archives extracted side-by-side beneath one parent. It recognizes the shipped `*-win-x64` folder names and only the active `*-linux-x64|arm64` architecture, then checks the repository development project. If nothing starts, diagnostics list every candidate by safe kind/display path and provide a repository-relative `dotnet run --project ...` command plus the equivalent current CLI command (`session show`, `campaign show`, `spell show`, `prompt show`, `apprentice show`, or `config edit`). Copyable fallback arguments are quoted for PowerShell on Windows and a POSIX shell on macOS/Linux; this display-only formatting is separate from the direct structured process launch. Launching a new process is the portable baseline. A platform integration may truthfully report reuse/focus only when it actually supports activation; otherwise Arcanum starts another instance and does not claim that an existing window was focused.

### `arcanum center`

Explicitly open Command Center in the current process. This is an alias for `arcanum open center`; the full interactive input table is in [Bare `arcanum`: Command Center](#bare-arcanum-command-center).

**Syntax:** `arcanum center [-c] [-r [<session>]]`

| Option | Meaning |
|---|---|
| `-c, --continue` | Reopen the most recent Session. Cannot be combined with `--resume`. |
| `-r, --resume [<session>]` | Reopen a Session by GUID, exact title, or unique title prefix; omit the value for an interactive picker. |

### `arcanum serve`

Hosts the Arcanum Minimal API.

Starts the local ASP.NET Core host. The nested `quit` command sends an authenticated shutdown request to an already-running host; it does not kill an arbitrary process. A host started automatically by an interactive client sets `ARCANUM_AUTO_LAUNCHED=1`, suppresses normal bootstrap/key output, and writes it to the owner-only auto-serve bootstrap log instead.

An active installation reset normally blocks startup. The sole exception is the owning operation's authenticated V2 record, or an exact bounded eligible V1 record that is migrated to V2 before its next effect, at global/all `Prepared + HostFactoryErasure` with no durable online-completion proof. Recovery-mode `serve` admits only health, quit, and that marked factory replay; unrelated API and background writers remain closed. Once proof exists, and for every later, partial, ambiguous, malformed, or ineligible legacy state, startup is blocked until exact offline continuation completes.

**Syntax:** `arcanum serve`

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum serve quit` | Requests the running host to shut down. | None beyond global or inherited family options. |

### `arcanum run`

Run one prompt through the unified execution entry point. Interactive use can auto-start the local host.

The optional positional words are joined in order as the instruction. Redirected standard input is additional, untrusted turn context rather than a replacement for that instruction, so `cat error.log | arcanum run "Explain this"` preserves both values. With no positional input and an interactive stdin, `run` prompts once for one line. Redirected input is buffered to an exact 10 MiB (10,485,760 UTF-8 byte) ceiling; one byte beyond the ceiling or a stream read failure fails clearly with no partial dispatch, silent truncation, or positional-only fallback.

Repeat `--with @path` to stage files for this turn. Relative paths resolve from the effective working directory, while an explicitly supplied absolute path is honored. Text staging uses strict UTF-8 and does not impose a filename-extension allowlist; recognized images use the existing Scrying MIME, size, and model-capability checks. Text and stdin share the existing request authority: 1 MiB UTF-8-safe `AttachedFileDto` chunks and a 32 MiB aggregate, with no incidental file/part-count ceiling. The 10 MiB stdin reader ceiling is not a separate per-file ceiling for `--with`. Diagnostics report UTF-8 byte count, part count, and SHA-256 for text; image diagnostics report decoded byte count and SHA-256. The client sends images as `ScryingFocusDto` values, and the client filesystem path is never treated as server authority. On a live route, these values enter the normal attachment pipeline: an Attachments-enabled host persists and Session-binds them before inference, while an Attachments-disabled host keeps them in memory for the current turn. A dry-run never persists them.

The default route is the ordinary Agent Loop. `--research` selects the progress-driven server-owned web research workflow. `--spell <spell>` forces a named Spell resolved by exact case-insensitive name or unique case-insensitive prefix. `--research` and `--spell` are the only route conflict; prompt, stdin, `--with`, context, sampling, output, and dry-run options otherwise compose. `--dry-run` performs a spend-free static, pre-inference preview of the resolved route, context, staged values, Spell override, and inference options without search, embedding/RAG, automatic semantic Spell routing, provider inference, tools, or persistence. A forced named Spell still resolves without retrieval. The preview is not an exact copy of the eventual live `PingRequest`: a live Agent handoff may add locally produced `PatternSnapshot` and `ChronosyncDelta` context.

Explicit context options follow the shared precedence over active local context, current-directory detection, and server defaults. Campaign, Workspace, Session, and Model are resolved before the route is dispatched; `--no-context` bypasses only saved context. Recursive `--plain`, `--output-format`, `--print`, and `--verbose` retain their global meanings and may appear before or after `run`.

`--session`, `--continue`, and `--resume` all fill the same slot, so supplying more than one exits `2` rather than resolving a precedence. `--new` keeps its documented behavior of winning over an explicit selector instead of adding a second conflict. `--continue` with no previous Session exits `2` naming how to start one; `--resume` with no value opens the Session picker, and cancelling that picker exits `0` having done nothing.

**Syntax:** `arcanum run [<prompt>...]`

| Option | Meaning |
|---|---|
| `--research` | Route through progress-driven server-side research. Cannot be combined with `--spell`. |
| `--spell <spell>` | Force a Spell by exact name or unique name prefix. Cannot be combined with `--research`. |
| `--with <@path>` | Stage one turn-scoped text file or image; repeat for several files. Relative and explicitly supplied absolute paths are supported. |
| `--attachment <attachment>` | Bound attachment GUID to include on this turn; repeatable. Use `--with @path` for a local file that is not yet an attachment. |
| `--dry-run` | Preview the resolved static pre-inference payload/context plan without provider spend, search, tools, or persistence. |
| `--show-content` | With `--dry-run`, include model-visible content in the authenticated preview. |
| `-m, --model <model>` | Use this configured model instead of the effective context or server default. |
| `-n, --new` | Start without continuing the effective Session. If a session selector is also supplied, `--new` wins instead of creating another option conflict. |
| `--unattended` | Apply unattended human-prompt and Ward behavior to the selected live route; dry-run reflects the resulting tool policy. |
| `-c, --continue` | Continue the most recent Session. Cannot be combined with `--resume` or `--session`. |
| `-r, --resume [<session>]` | Resume a Session by GUID, exact title, or unique title prefix; omit the value for an interactive picker. |
| `-C, --campaign <campaign>` | Use the selected Campaign GUID, exact name, or unique prefix. |
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

### `arcanum look`

Eye of the World: situational snapshot of the current directory (domain + TOC).

Builds an Eye of the World snapshot for the current directory locally, without requiring the HTTP host.

**Syntax:** `arcanum look`

### `arcanum doctor`

Run subsystem diagnostics, plan safe repairs, and name the exact remediation command.

Every diagnostic carries a stable `subsystem.snake_case` **id**, an **outcome**, and zero or more **remedies**. No diagnostic mutates: each one opens the encrypted Grimoire read-only, stats files, and reads local state, and none creates a path, takes a lock, installs schema, or upgrades key material. Nothing changes on disk as a result of a diagnostic unless you pass `--repair <id> --apply` or the `--fix-permissions` alias.

Two things do touch the disk regardless, and neither is a diagnostic. Every `arcanum` command's bootstrap ensures its own Data Protection key-ring directory exists before any verb runs. And SQLite materializes the `-wal`/`-shm` sidecars of a write-ahead-logged database on any open, including a read-only one; `grimoire.wal_size` therefore measures the log *before* the integrity checks open the database, so it reports the log the last host shutdown left rather than one this command created.

Outcomes, least to most severe:

| Outcome | Meaning | Effect on the exit code |
|---|---|---|
| `Skipped` | The precondition is absent (no database yet, web research disabled, a network probe you did not opt into). Not a fault. | none |
| `Healthy` | The subsystem is in its intended state. | none |
| `Unavailable` | The subsystem could not be consulted, so its state is unknown — most often an unreachable host. | `1` only with `--strict` |
| `Degraded` | It works, but something needs attention. | `1` only with `--strict` |
| `Unhealthy` | It is broken. | `1` |

**Syntax:** `arcanum doctor [options]`

| Option | Meaning |
|---|---|
| `--only <id>` | Run only these diagnostic ids or subsystems; repeatable. An unrecognized value exits `2` rather than silently running a smaller diagnostic. |
| `--skip <id>` | Skip these diagnostic ids or subsystems; repeatable. |
| `--include-network` | Also probe every configured provider endpoint with one non-billable `GET {endpoint}/models`. No completion is requested, so this never spends inference tokens. Without it, `providers.reachability` reports `Skipped`. |
| `--repair <id>` | Plan a repair by id; repeatable. Shows the plan and changes nothing unless `--apply` is also passed. |
| `--apply` | Perform the planned repairs after confirmation. Requires `--repair`; `--apply` alone exits `2` rather than repairing everything. Use the global `--yes` for automation. |
| `--strict` | Exit nonzero when any diagnostic is `Degraded` or `Unavailable`, not only when one is `Unhealthy`. |
| `--fix-permissions` | Apply the owner-only permission repair. It does not prompt, preserving its pre-existing automation contract, and its exit code still reflects only whether that repair succeeded — not whether the rest of the installation is healthy. Unlike previous releases it no longer short-circuits: the full diagnostic runs and reports alongside it. The exemption is granted to this repair alone; any other `--repair` named on the same command line still needs `--apply` and still goes through confirmation. |

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum doctor list` | List every diagnostic id, its subsystem, and every repair id, so `--only`/`--skip`/`--repair` values are discoverable rather than guessed. | None beyond global or inherited family options. |
| `arcanum doctor explain <id>` | Explain one diagnostic or repair: what it reads, what it changes, which detector justifies it, and how to run it. | None beyond global or inherited family options. |

#### Diagnostic catalog

`arcanum doctor list` is authoritative; this table is the shape of it. Ids are stable and part of the `--json` contract.

| Check id | Subsystem | What it reads | Remediation |
|---|---|---|---|
| `system.version` | System | Build, OS, runtime, TTY, and colour posture. | — |
| `system.tokenizer` | System | The configured encoding, via one smoke count. | — |
| `paths.required` | Paths | Presence of the Grimoire directory, database, and secret store. Named individually when missing. | `arcanum serve` — the database and secret store are created on first host start, not by a directory repair |
| `paths.managed_directories` | Paths | Which managed directories Arcanum owns are missing. | `arcanum doctor --repair paths.create_managed_directories --apply` |
| `permissions.posture` | Permissions | Owner-only mode of every sensitive file and directory, including per-provider credential mirrors. Reads modes only, never contents. | `arcanum doctor --repair permissions.apply_owner_only --apply` |
| `configuration.file` | Configuration | `arcanum.json` size, JSON syntax, and tree schema. The file is optional, so its absence is `Skipped`, not a warning — otherwise `--strict` would fail every default installation. | `arcanum config edit` |
| `configuration.semantics` | Configuration | The full semantic validation the host runs at startup — provider shapes, model references, HTTPS certificate and port coherence, embeddings, path allowlists. | `arcanum config edit` |
| `configuration.environment_overrides` | Configuration | Environment variables that claim a configuration path but did not take effect. Names and paths only, never values. | `arcanum config show` |
| `credentials.os_store` | Credentials | Whether the OS credential backend is reachable at all, which is what separates "no credential stored" from "keychain unreachable". | `arcanum key list` |
| `credentials.key_ring` | Credentials | The Data Protection key ring that decrypts every `.dat` mirror, and whether mirrors exist without it. The ring is machine-local and is never carried by a backup archive. | `arcanum key list` |
| `credentials.providers` | Credentials | Per-credential presence and source, resolved exactly as the run time resolves it. | `arcanum key provider set <provider>` |
| `credentials.master_key` | Credentials | Whether the master API key is readable. | `arcanum key set` |
| `webresearch.credential` | WebResearch | Web-research credential presence and decryptability. Reachability is deliberately not probed: the provider bills every request. | `arcanum key provider set perplexity --kind web-research` |
| `grimoire.key_material` | Grimoire | Encryption-secret status and a stranded pending KDF upgrade. The opening checks try every derivation this installation supports — committed sidecar, pending sidecar, and both legacy forms — so a pre-upgrade or mid-upgrade database is not misreported as unopenable. | `arcanum backup restore <archive>` |
| `grimoire.integrity` | Grimoire | `PRAGMA quick_check` on the encrypted database, opened read-only. | `arcanum backup create --scope grimoire` |
| `grimoire.foreign_keys` | Grimoire | `PRAGMA foreign_key_check`; reports table names only. | `arcanum backup create --scope grimoire` |
| `grimoire.wal_size` | Grimoire | Write-ahead log size, which grows when no clean shutdown checkpoints it. | `arcanum daemon status` |
| `storage.file_encryption` | Storage | Encrypted-blob inventory and file-encryption secret status. | `arcanum data encryption status` |
| `operations.awaiting_repair` | Operations | Durable operations in `ReconciliationRequired`, `Failed`, or `Abandoned`, counted from the database over a read-only connection so a crashed host cannot hide them. | `arcanum operation list --state ReconciliationRequired` |
| `operations.stale_leases` | Operations | Operation leases that expired while still claiming to run. | `arcanum operation list` |
| `operations.durable_state` | Operations | The host's own durable-operation summary when it is reachable. | `arcanum operation list --state ReconciliationRequired` |
| `runtime.pid_file` | Runtime | Whether the PID file names a live process. | `arcanum doctor --repair runtime.remove_stale_pid --apply` |
| `runtime.maintenance_lock` | Runtime | Whether another process holds the installation maintenance lock. Read-only: it opens the existing file rather than acquiring the lock. | — |
| `runtime.disk_space` | Runtime | Free space on the Arcanum volume. | `arcanum data prune --dry-run` |
| `runtime.tool_child_sandbox` | Runtime | Filesystem jail, resource limits, and escape-hatch posture for tool children. | — |
| `weave.embeddings` | Weave | Configured embedding provider, model, and vector mode. Managed compatibility budget `0` means no total row budget. | — |
| `mcp.global_config` | Mcp | Global `mcp.json` syntax and server-entry count. Optional, so absence is `Skipped`. | `arcanum mcp list` |
| `host.api_health` | Host | Authenticated reachability of the local API. | `arcanum serve` |
| `host.health_components` | Host | The running host's own per-subsystem verdicts, relayed rather than re-implemented. No answer at all is `Unavailable`, never a failure; a host that answers and reports itself unhealthy is `Unhealthy`; something that answers as no Arcanum host would — a foreign service holding the configured port — is `Unhealthy` and is never told to start a host that could not bind. | `arcanum serve`, `arcanum lore`, `arcanum config edit` |
| `providers.familiars` | Providers | Whether each configured `ClaudeCodeCli`/`CodexCli` provider is installed and signed in. Local only — it resolves the binary on `PATH` and reads the CLI's own status surface, so it needs no `--include-network` and spends nothing. Account e-mail, organisation identifiers, and local paths are never printed. | `claude auth login`, `codex login`, `arcanum config edit` |
| `providers.reachability` | Providers | One non-billable model listing per configured provider. **Requires `--include-network`.** Endpoints are never printed. | `arcanum key provider set <provider>`, `arcanum config edit`, `arcanum model list` |

#### Repairs

Every repair has a read-only detector, a no-change dry-run plan, and converges: applying a successful repair a second time reports `AlreadyConverged` and changes nothing. No repair ever regenerates a key over existing ciphertext, deletes user data, or rewrites a corrupt encrypted database — those states fail closed with restore guidance instead.

| Repair id | Detector | What it changes | Safety |
|---|---|---|---|
| `permissions.apply_owner_only` | `permissions.posture` | Sets owner-only mode on every inventoried sensitive path whose posture differs. | Only ever narrows access; sets an absolute mode rather than a delta; never creates a path. |
| `paths.create_managed_directories` | `paths.managed_directories` | Creates the missing managed directories with owner-only permissions. | Creation only — never deletes, moves, or touches an existing directory. |
| `runtime.remove_stale_pid` | `runtime.pid_file` | Deletes a PID file whose process is gone or which holds no process id. | Re-reads the posture at apply time and refuses while any process holds that id, so a host that started between plan and apply keeps its claim. |

The legacy `API Health` check answers the same question `host.health_components` does, and it agrees with it about what a port already held by something else means: an address that accepts a connection and then answers as no Arcanum host would is a `fail` naming the foreign responder, not the `warn` that reads "not reachable" and points at starting a host which could not bind the port anyway.

The `ProviderCredentials` check resolves each credential the same way the run time does — the environment reference first, then the OS-backed secure store — and reports which source satisfied it. A missing explicit reference or a stored-but-undecryptable credential is a warning naming the exact recovery command. Credential values are never shown, and no finding, remedy, or repair step ever carries a credential value, a raw response body, or a file's contents.

`--json` emits one typed `DoctorReport` on stdout rather than decorated panels, with diagnostics on stderr. `arcanum doctor list` and `arcanum doctor explain` emit a typed `DoctorCatalog` under `--json`. The report keeps its pre-existing shape — `healthy`, and per check `name`, `status` (`ok`/`warn`/`fail`), and `detail` — and adds `id`, `subsystem`, `outcome`, and `remedies` per check plus a top-level `outcome` and `repairs`, so an existing consumer is unaffected while a new one can key on the stable id. `status` is derived from `outcome`.

### `arcanum key`

Master, inference-provider, and web-research credential utilities (secure local stores; no HTTP).

Reads and writes secure local credentials without an HTTP request. Master-key output is deliberately written to stderr. `key set` accepts its value as an argument, redirected stdin, or a hidden prompt; `key provider set` accepts only redirected stdin or a hidden prompt. The hidden prompt needs a terminal on stdin and is skipped whenever the invocation declares itself headless: under `-p/--print` or `--output-format json` the value must arrive on redirected stdin (or, for `key set`, as the argument), and a missing one returns `2` with a diagnostic rather than blocking on a prompt that no script would answer. `--plain` only strips colour, so it keeps the hidden prompt. Provider secret values are never displayed, including under `--json` and in debug output.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum key show` | Print the stored master API key to stderr (stdout piping does not capture the secret). | None beyond global or inherited family options. |
| `arcanum key set [<api-key>]` | Store a master API key in the OS credential store (mirrors to security.dat when possible). | None beyond global or inherited family options. |
| `arcanum key list` | Report every Arcanum-owned credential identity with presence, status, storage class, resolved source, environment reference, and fixed recovery guidance. | None beyond global or inherited family options. |
| `arcanum key provider [command]` | Manage inference-provider and web-research credentials. Stored values are never displayed. | None beyond global or inherited family options. |
| `arcanum key provider set <provider>` | Store a provider credential from redirected stdin or a secure prompt. | `--kind <kind>` — `inference` or `web-research`. |
| `arcanum key provider status <provider>` | Report whether a provider credential is configured. | `--kind <kind>` — `inference` or `web-research`. |
| `arcanum key provider delete <provider>` | Delete a provider credential from local secure stores. | `--kind <kind>` — `inference` or `web-research`. |

`<provider>` is a configured inference provider name. The single reserved name `perplexity` routes to the native web-research credential by default; `--kind` overrides that routing in either direction, so an inference provider actually named `perplexity` remains addressable.

#### Credential inventory

`arcanum key list` reports Arcanum's closed credential catalog. It never enumerates unrelated OS credentials, and it reports presence and status only — never a value, and never a value-derived hint.

| Credential | Storage | Notes |
|---|---|---|
| Master API key | OS credential store with an owner-only Data Protection mirror (`security.dat`) | Generated on first `arcanum serve`; readable with `arcanum key show`. |
| Grimoire encryption secret | OS credential store with an owner-only Data Protection mirror | A corrupt secret fails closed and is never replaced while encrypted data exists. |
| File-encryption master key | OS credential store with an owner-only Data Protection mirror | Recover from the OS store, the mirror plus key ring, or one verified `.arcbackup` generation. |
| Web research (Perplexity) | `ARCANUM_PERPLEXITY_API_KEY` (or the configured reference), otherwise the OS credential store with an encrypted mirror | The environment reference wins when both are present. |
| Inference provider API key | `ARCANUM_PROVIDER_<NORMALIZED_NAME>_API_KEY` (or the configured reference), otherwise the OS credential store with an encrypted mirror | One credential per provider name; the environment reference wins when both are present. |

Status values are `configured`, `missing`, and `corrupt`. A `corrupt` credential means the encrypted mirror is present but could not be decrypted with the current Data Protection key ring; Arcanum fails closed and never generates a replacement. Store the credential again with `arcanum setup` or `arcanum key provider set <provider>`.

Provider credentials are resolved in a fixed order at run time: the configured (or derived) environment reference first, then the OS-backed secure store. That order lets an operator override a stored credential for one process without editing stored state, and lets `arcanum setup` leave a new installation ready to run without exporting anything.

.NET cannot reliably zero an immutable managed `string`, so Arcanum does not claim to erase the credential strings crossing a store boundary. It minimizes their lifetime and number of copies, and zeroes every `byte[]` buffer it owns in a `finally`.

### `arcanum lore`

Manage Grimoire explicit memory (lore) directly.

Maintains the legacy operator-owned key/value MageSettings store. Lore is distinct from Lexicon entities, Saga memories, session entries, and attachments. `lore list` follows every advancing server offset with no client-owned total-page ceiling; a non-advancing or overflowing continuation fails explicitly instead of looping or silently returning a prefix.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum lore list` | List all scribed lore keys. | None beyond global or inherited family options. |
| `arcanum lore get <key>` | Read a specific lore entry by key. Text output is the value verbatim on stdout with the key on stderr, so `VALUE=$(arcanum lore get <key>)` captures the value alone; `--output-format json` emits `{ "key", "value" }`, which preserves trailing newlines and any escape sequences the value contains. | None beyond global or inherited family options. |
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
| `arcanum daemon initiative <job-name> <minutes>` | Set the adaptive polling interval for a job; minutes must be between 1 and 10080 (requires API: arcanum serve). A name not configured under `Arcanum:Daemon:Jobs` is rejected with `404 Daemon.NotFound` naming `arcanum daemon jobs` rather than reported as applied. | None beyond global or inherited family options. |
| `arcanum daemon alert <message>` | Send a Comm Link test alert (requires API: arcanum serve). | `-t, --title <title>` — Alert title.<br>`-s, --severity <severity>` — Severity: Info, Warning, or Critical.<br>`--source <source>` — The alert source label. |

### `arcanum campaign`

Persistent project containers for sessions, spells, prompts, Codex, and Sanctum; filesystem access and indexing remain Workspace responsibilities.

Manages persistent project containers. Campaigns own sessions, prompts, spells, Codex, and Sanctum policy; Workspaces separately own filesystem access and indexing.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum campaign list` | List registered campaigns. | `--type <type>` — Filter by exact type: spell, campaign, data, or custom. |
| `arcanum campaign show [<campaign>]` | Show campaign detail. | None beyond global or inherited family options. |
| `arcanum campaign create` | Register a new campaign. | `--name <name>` — Campaign name.<br>`--path <path>` — Absolute server-host filesystem path represented by the campaign.<br>`--type <type>` — Campaign type: spell, campaign, data, or custom; defaults to campaign.<br>`--description <description>` — Optional human-readable campaign description. |
| `arcanum campaign update [<id>]` | Update a campaign. | `--name <name>` — Supplies the resource name stored by this command. |
| `arcanum campaign delete [<id>]` | Remove a campaign. | None beyond global or inherited family options. |
| `arcanum campaign export [<id>]` | Export a campaign's spells and prompts as JSON. | `--output <output>` — Writes the command result to this file instead of stdout. |
| `arcanum campaign import [<id>]` | Import spells and prompts into a campaign. | `--file <file>` — Reads the command's source document from this file path. |
| `arcanum campaign spells [<id>]` | List spells scoped to a campaign, shadowing built-ins. | `-q, --query <query>` — Applies the supplied free-text query.<br>`--tag <tag>` — Supplies a tag value; mutations may accept repeated tags, while list/search commands use it as a filter.<br>`--tool <tool>` — Filters by tool name. |
| `arcanum campaign prompts [<id>]` | List prompts scoped to a campaign. Under `--json` the prompt summaries are written as one array document instead of the table. | `-q, --query <query>` — Applies the supplied free-text query.<br>`--tag <tag>` — Supplies a tag value; mutations may accept repeated tags, while list/search commands use it as a filter. |
| `arcanum campaign sessions [<id>]` | List sessions scoped to a campaign. A page that reports more rows without an advancing cursor is a host fault: the command reports `Api.PaginationNoProgress` and returns `1` rather than paging from a row it does not have, in every output mode. Under `--json` the session summaries are written as one array document instead of the table, and the paging hint — which names the `--before-updated-at` value to pass next — is omitted, because the cursor is `updatedAt` on the last summary the document already carries. | `--status <status>` — Filters by the exact lifecycle status accepted by the server.<br>`--search <search>` — Filter campaign sessions by search text.<br>`--limit <limit>` — Limits the maximum number of returned rows or results.<br>`--before-updated-at <before-updated-at>` — Return sessions updated before this ISO-8601 cursor. |
| `arcanum campaign codex [command]` | Manage the campaign's CODEX.md scratchpad. | None beyond global or inherited family options. |
| `arcanum campaign codex get [<id>]` | Print CODEX.md. | None beyond global or inherited family options. |
| `arcanum campaign codex put [<id>]` | Write CODEX.md from a file. | `--file <file>` — Reads the command's source document from this file path. |
| `arcanum campaign codex delete [<id>]` | Delete CODEX.md. | None beyond global or inherited family options. |

**A campaign bundle says what it left behind.** `arcanum campaign export` has never carried Covenant memory, and it still carries only the campaign's settings, its non-builtin spells, and its prompts — no Covenant content, version, receipt, hash, provenance, or tainted artifact. With `Arcanum:Features:Covenant` on the JSON now also carries an `exclusions` object with two content-free counts: `covenantEntryCount`, the campaign's canonical Covenant memory, and `taintedArtifactCount`, its Covenant-derived artifacts. They are separate because they answer separate questions, and an operator moving a campaign to another machine needs to know which of the two the bundle is short of. With the gate off the field is omitted entirely rather than reported as zero — a zero reads as a measurement where the honest answer is that nothing was measured. See [DESIGN §10.19.11](Arcanum.DESIGN.md#101911-refusing-tainted-plaintext-export-and-reporting-what-a-campaign-export-left-behind).

### `arcanum session`

Manage and continue sessions through the Arcanum API.

Provides the complete durable session lifecycle. Optional session selectors accept a GUID, exact title, unique title prefix, saved session context, or an interactive picker when allowed.

Sessions do not impose a total entry-count or fork-depth ceiling. The existing `sessions.maxPinnedEntries` admission setting remains unchanged. Entry listings page, long unsummarized history consolidates in timestamp-group-safe checkpoints, and provider-context materialization adds no second pin-count ceiling: it retains per-item/per-turn byte protections while explicitly reporting deferred accepted pins.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum session list` | List recent sessions. | `--campaign <campaign>` — Filter by campaign GUID.<br>`--status <status>` — Filter by session status.<br>`--search <search>` — Filter by search text.<br>`--model <model>` — Filter by model.<br>`--from <from>` — Include sessions on or after this ISO-8601 timestamp.<br>`--to <to>` — Include sessions on or before this ISO-8601 timestamp.<br>`--limit <limit>` — Maximum sessions per page. |
| `arcanum session show [<session>]` | Summarize a session, including telemetry and lineage. | None beyond global or inherited family options. |
| `arcanum session entries [<session>]` | List transcript entries for a session. | `--offset <offset>` — Number of entries to skip.<br>`--limit <limit>` — Maximum entries to return. |
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

**A tainted session cannot be exported as plaintext.** With `Arcanum:Features:Covenant` on, `arcanum session export` is refused with `Covenant.PlaintextExportRefused` when the session carries any Covenant-derived entry, tool artifact, summary, title, Saga, Lexicon, attachment-derived artifact, or projection. The refusal lands before the transcript is read, so no content reaches the file or the terminal, and there is no option or confirmation that overrides it: a plaintext file is nonrevocable the moment it exists, and the supported way to move such a session is `arcanum backup`. A session whose Covenant artifacts were purged is still refused, because the purge does not unmake the fact that the session held them. With the gate off — the default — the command behaves exactly as it always has. `--format` accepts `json` or `markdown` in any case; anything else is `Session.InvalidFormat`. See [DESIGN §10.19.11](Arcanum.DESIGN.md#101911-refusing-tainted-plaintext-export-and-reporting-what-a-campaign-export-left-behind).

### `arcanum saga`

Saga long-term associative memory (requires arcanum serve).

Automatic extraction has no public interval/window/output-token controls or total memory-count ceiling. It processes durable history oldest-first in timestamp-group-safe checkpoint pages and retries a failed page without advancing its watermark. Listing and semantic search remain paged; explicit deletion, retention policy, provider capability, and cancellation own the real boundaries.

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

#### Dedicated Covenant management commands (contract frozen, not yet registered)

**None of the dedicated commands below exists yet.** The request boundary, configuration gate, and existing `arcanum data` erasure lifecycle are delivered; these management routes and their command-tree branches are not. Their structured `--json` payloads are frozen (`CovenantEntryPayload`, `CovenantListPayload`, `CovenantShowPayload`, `CovenantMutationPlanPayload`, `CovenantMutationResultPayload`, `CovenantDoctorPayload`, `CampaignPathStatusPayload`, `CampaignPathPlanPayload`, `CampaignPathResultPayload`, `SessionBindingStatusPayload`, `SessionBindingResultPayload`) in `CliJsonContext`, together with the HTTP shapes they project ([API §8.28](Arcanum.API.md#828-covenant-public-contract-frozen-not-yet-routed)). They are documented here so the surfaces that implement them have one contract to implement rather than one each.

| Planned command | Input source and confirmation | Output and exit |
|---|---|---|
| `arcanum memory covenant set <key> (--global \| --campaign <id>) [--file <path>] --expected-revision <n> [--reactivate]` | Content from `--file`, stdin, or the interactive console — never argv, so a preference never lands in shell history. Calls the set-preflight route first and shows the server's compiled hash, framed byte cost, and resolution effects, then confirms. Redirected stdin and every `--json` mutation require `--yes`. | `CovenantMutationPlanPayload` then `CovenantMutationResultPayload`; standard exit codes. |
| `arcanum memory covenant list (--global \| --campaign <id> \| --all-scopes) [--lane <lane>] [--lifecycle <lifecycle>] [--query <text>]` | Read-only. One scope selector is required; there is no default that would sweep every Campaign. | `CovenantListPayload`. |
| `arcanum memory covenant show <key> (--global \| --campaign <id>) [--history]` | Read-only. | `CovenantShowPayload`. |
| `arcanum memory covenant retire <key> (--global \| --campaign <id>) --lane <lane> --expected-revision <n>` | Calls the retire-preflight route and shows the exact targeted revision, dependent-head digest, Global and Proposed eligibility effects, full affected-Campaign count, and any truncated examples, then confirms through `IConfirmationPrompt`. `--json` requires `--yes` independently of stdout redirection. | `CovenantMutationPlanPayload` then `CovenantMutationResultPayload`. |
| `arcanum memory covenant doctor [--repair-schema \| --rebuild-index \| --reinitialize-family]` | `--reinitialize-family` displays every server-authoritative loss, local-erasure, nonrevocable-disclosure, free-space, and preserved-core count and requires explicit destructive confirmation; redirected, plain, and JSON modes require `--yes`. It never touches the database or filesystem from the CLI process. | `CovenantDoctorPayload`; rebuild and reinitialize return the existing long-operation descriptor and use the established watch path. |
| `arcanum campaign path status (--all \| --campaign <id>)` | Read-only. `--all` is the bulk legacy-upgrade inventory; each unresolved row carries the exact next verb. | `CampaignPathStatusPayload`. |
| `arcanum campaign path register\|update\|repair\|deregister\|takeover <campaign-id> [<path>]` | The server opens and identifies the path; the CLI never inspects or writes a marker. Displays the opened path, old and new identity digests, marker effect, and drained-turn impact before confirmation. Redirected, plain, and JSON modes require `--yes`. | `CampaignPathPlanPayload` then `CampaignPathResultPayload`. |
| `arcanum session campaign-binding status [--all \| --session <id>]` | Read-only. | `SessionBindingStatusPayload`. |
| `arcanum session campaign-binding resolve <session-id> (--global \| --campaign <id>)` | Operates only on `LegacyUnresolved`; a final binding cannot be changed. Displays the immutable choice and the affected Session, then confirms. | `SessionBindingResultPayload`. |
| `arcanum security host-process-tools enable --yes` | The one offline Covenant maintenance exception: it acquires the installation lock and never starts or calls the HTTP host. Every other Covenant command is HTTP-thin and reaches Infrastructure through `ArcanumApiClient` only. | One `--json` document, plain mode, confirmation, cancellation, and the standard exit codes. |

Exit behavior for the whole family follows the existing contract: command validation `2`, transport failure `3`, cancellation `130`, and typed operational or policy failure `1` with no sensitive detail printed. `--campaign` accepts the repository-wide `-C` alias and resolves a Campaign by GUID, exact name, or unique name prefix; an ambiguous prefix fails with candidates and mutates nothing.

### `arcanum spell`

Spell utilities (requires arcanum serve).

Manages built-in and workspace spells, named version files, validation, dry-run casting, and execution. Workspace selectors resolve server-host resources. Legacy direct listing retains the array response, while resource selection uses the 50-item opaque-cursor catalog and follows pages as needed. Command Center `/spell list [opaque-cursor]` fetches one metadata page and prints the exact continuation; a changed/missing cursor anchor or mismatched filter instructs a cursor-free restart.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum spell list` | List spells. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
| `arcanum spell show [<spell>]` | Show spell detail. | `--workspace <workspace>` — Selects the workspace by ID, name, or server-host path. |
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
| `arcanum prompt list` | List prompts. | `--campaign-id <campaign-id>` — Filter by campaign GUID.<br>`-q, --query <query>` — Free-text query.<br>`--tag <tag>` — Filter by tag. |
| `arcanum prompt show [<prompt-name>]` | Show prompt detail. | None beyond global or inherited family options. |
| `arcanum prompt versions <name>` | List versions of a prompt by name. | `--campaign-id <campaign-id>` — Filter by campaign GUID. |
| `arcanum prompt create` | Create a prompt. | `--name <name>` — Prompt name.<br>`--version <version>` — Prompt version label.<br>`--template <template>` — Prompt template: inline text, or @filename to read from a file.<br>`--campaign-id <campaign-id>` — Campaign GUID to associate with.<br>`--description <description>` — Prompt description.<br>`--tag <tag>` — Tag; pass multiple times for several tags. |
| `arcanum prompt update [<id>]` | Update a prompt. | `--template <template>` — Prompt template: inline text, or @filename to read from a file.<br>`--tag <tag>` — Tag; pass multiple times for several tags. |
| `arcanum prompt delete [<id>]` | Delete a prompt. | None beyond global or inherited family options. |
| `arcanum prompt render [<id>]` | Render a prompt template with parameters. | `--param <param>` — Template parameter as key=value; pass multiple times for several parameters. |
| `arcanum prompt test [<id>]` | Assemble the system prompt without LLM cost. | None beyond global or inherited family options. |
| `arcanum prompt execute [<id>]` | Render and run session-backed inference, writing assistant response text to stdout and any tool-call summary to stderr. | `--input <input>` — User message for the prompt turn: inline text, or @filename to read from a file.<br>`--param <param>` — Template parameter as key=value; pass multiple times for several parameters.<br>`--session-id <session-id>` — Session GUID to bind context from. |
| `arcanum prompt clone [<id>]` | Clone a prompt to a new name/version. | `--new-name <new-name>` — New prompt name.<br>`--new-version <new-version>` — New prompt version label.<br>`--campaign <campaign>` — Campaign GUID to associate the clone with. |
| `arcanum prompt export [<id>]` | Export a prompt as portable JSON. | `--output <output>` — Write exported JSON to this file instead of stdout. |
| `arcanum prompt import` | Import a prompt from portable JSON. | `--file <file>` — Path to a prompt export JSON file.<br>`--campaign-id <campaign-id>` — Campaign GUID to associate the import with. |

### `arcanum ward`

Ward approval gates for Forbidden Arts (requires arcanum serve).

Lists Forbidden Arts approval gates and resolves one gate. `--allow` and `--deny` are mutually exclusive.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum ward list` | List active wards. | None beyond global or inherited family options. |
| `arcanum ward show <id>` | Show ward detail. | None beyond global or inherited family options. |
| `arcanum ward resolve <id>` | Allow or deny a ward. | `--allow` — Allow the warded tool call to proceed.<br>`--deny` — Deny the warded tool call.<br>`--reason <reason>` — Optional reason recorded with the resolution. |

### `arcanum trial`

Run Trials against spells, prompts, or Apprentice goals (requires arcanum serve).

Runs Proving Grounds evaluation against a spell, prompt, or Apprentice goal. It renders the Passed/Failed summary, a verdict table, and at most the first 500 characters of Trial output. A completed Trial that does not pass returns exit code 1.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum trial run` | Run a Trial with Inquisitors. | `--target <target>` — Trial target kind: spell, prompt, apprenticeGoal.<br>`--target-value <target-value>` — Spell name, prompt GUID, or apprentice goal text.<br>`--model <model>` — Model override for the Trial.<br>`--workspace <workspace>` — Workspace root to scope the Trial.<br>`--name <name>` — Trial display name; defaults to '{targetKind}:{target}'.<br>`--inquisitor <inquisitor>` — Inquisitor spec: inline JSON, or @filename. Pass multiple times for several inquisitors.<br>`--var <var>` — Trial variable as key=value; pass multiple times for several variables. |

### `arcanum apprentice`

The Forge Apprentice orchestration (requires arcanum serve).

Manages durable Apprentice orchestration, intervention, replanning, child delegation, and Chronicle streaming. Lifecycle commands resolve an Apprentice selector before mutation.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum apprentice list` | List Apprentices. | `--campaign-id <campaign-id>` — Filter by campaign GUID.<br>`--status <status>` — Filter by status.<br>`--limit <limit>` — Maximum number of Apprentices to return. |
| `arcanum apprentice show [<apprentice>]` | Show Apprentice detail. | None beyond global or inherited family options. |
| `arcanum apprentice create` | Create an Apprentice. | `--goal <goal>` — Apprentice goal: inline text, or @filename to read from a file.<br>`--name <name>` — Display name; defaults to a truncated form of the goal.<br>`--campaign-id <campaign-id>` — Campaign GUID to associate with.<br>`--workspace <workspace>` — Workspace root to scope the Apprentice. |
| `arcanum apprentice delete [<id>]` | Delete a terminal Apprentice. | None beyond global or inherited family options. |
| `arcanum apprentice start [<id>]` | Persist the start and begin plan generation/execution when a host concurrency slot is available. Temporary capacity queues the start instead of rejecting it; Chronicle/status surfaces progress and `cancel` removes queued work. | None beyond global or inherited family options. |
| `arcanum apprentice pause [<id>]` | Pause at the next step boundary. | None beyond global or inherited family options. |
| `arcanum apprentice resume [<id>]` | Resume from checkpoint. | None beyond global or inherited family options. |
| `arcanum apprentice cancel [<id>]` | Cancel execution. | None beyond global or inherited family options. |
| `arcanum apprentice reweave [<id>]` | Replace the remaining plan steps. | `--plan <plan>` — JSON array of plan steps: inline text, or @filename to read from a file. |
| `arcanum apprentice intervene [<id>]` | Provide Divine Intervention guidance to an escalated Apprentice. | `--guidance <guidance>` — Guidance text for the escalated Apprentice. |
| `arcanum apprentice cast [<id>]` | Delegate a child Apprentice via The Conclave. | `--goal <goal>` — Child Apprentice goal text.<br>`--name <name>` — Display name for the child Apprentice. |

### `arcanum budget`

Today's spend against the configured daily budget (requires arcanum serve). See DESIGN §22.2.

| Command | Purpose | Options |
|---|---|---|
| `arcanum budget` | Show today's spend against the daily limit, separating this instance's own inference (`Local`) from delegated A2A work (`Delegated`), and naming any Sendings whose peer reported no cost at all — those are counted, never costed, so a non-zero count means the figures are a floor rather than the whole bill. | None beyond global options. |

### `arcanum conclave`

The Conclave and its A2A surface (requires arcanum serve). See DESIGN §5.7.1 for the end-to-end workflow.

| Command | Purpose | Options |
|---|---|---|
| `arcanum conclave status` | Show whether A2A is disabled, configured, degraded, or healthy, with the effective server and Agent Card paths and the next action when something is missing. | None beyond global or inherited family options. |
| `arcanum conclave dispatch` | Dispatch a Sending to a remote A2A agent and wait for its terminal result. Reports the remote task id, the remote cost (or "unknown"), and the remote wall-clock. Cancelling also cancels the remote task. | `--agent-url <url>` — Remote agent base URL or Agent Card URL.<br>`--goal <goal>` — Goal text delegated to the remote agent.<br>`--name <name>` — Optional display name for the Sending.<br>`--continuable` — Return a continuation task id when the remote asks for more input or authentication, instead of ending the Sending.<br>`--skill <id>` — Agent Card skill id to target; the Sending fails before the remote task is created if the peer advertises no such skill.<br>`--accept <media-type>` — Media type to accept back (repeatable). Omit to accept whatever this instance can consume.<br>`--callback` — Ask the remote agent to report back when it finishes instead of holding one of this instance's concurrent-Sending slots for the whole remote run. Requires `Arcanum:Integrations:A2A:PushNotifications` and a reachable `PushCallbackBaseUrl`; falls back to the ordinary wait when the peer cannot accept a callback. |
| `arcanum conclave continue <task-id>` | Answer a Sending the remote parked at `input-required` or `auth-required`, resuming the same remote task rather than re-running the goal. | `--agent-url <url>` — The same remote agent the Sending was dispatched to.<br>`--message <text>` — The input or credential the remote asked for.<br>`--continuable` — Keep returning a continuation if the remote asks again.<br>`--skill <id>` — Agent Card skill id to target, validated against the peer's card before sending.<br>`--accept <media-type>` — Media type to accept back (repeatable). |

Remote cost is reported as **unknown** when the peer publishes no usage — never as zero. A2A has no standard usage field, so only a peer that supplies one (which includes another Arcanum) yields real figures.

An inbound Sending *is* an Apprentice, so `arcanum apprentice list/get/cancel` and `arcanum watch apprentice` are the surfaces for observing and cancelling work other agents send here. `arcanum watch apprentice` renders the four Sending frames distinguishably — dispatched, in-flight remote state, and the terminal frame with its response or failure reason, external cost, and remote duration.

### `arcanum model`

Native model listing across configured providers (requires arcanum serve).

Lists or selects models from the latest successfully persisted configuration without exposing provider endpoints or credentials. Other inference/runtime consumers still require a host restart before they adopt a configuration change.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum model list` | List configured models across all providers (GET /api/models). Models on a Familiar provider's `hiddenModels` list are omitted; hiding is a display preference, so a hidden model still runs when you name it with `-m`. | None beyond global or inherited family options. |
| `arcanum model show [<model>]` | Show a configured model without exposing its endpoint. | None beyond global or inherited family options. |

### `arcanum provider`

Native provider listing and configuration summary (requires arcanum serve).

Lists or selects providers from the latest successfully persisted configuration while keeping endpoints and credential details redacted. Other inference/runtime consumers still require a host restart before they adopt a configuration change.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum provider list` | List configured providers with redacted secrets (GET /api/providers). A Familiar reports no endpoint and no credential reference — it signs in through its own CLI. | None beyond global or inherited family options. |
| `arcanum provider show [<provider>]` | Show a configured provider without exposing endpoint or credential details. | None beyond global or inherited family options. |

### `arcanum workspace`

Workspace = registered filesystem access/indexing boundary. Campaign = persistent project container with sessions, spells, prompts, Codex, and Sanctum. Paths are resolved on the server host.

Manages registered server-host filesystem and indexing boundaries. Optional workspace selectors use explicit value, saved context, then current-directory containment. `register` defaults to the current directory, derives the name from the final path segment, and uses type `custom`.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum workspace list` | List registered workspaces. | None beyond global or inherited family options. |
| `arcanum workspace current` | Map the client current directory to registered server Workspace and Campaign resources. | None beyond global or inherited family options. |
| `arcanum workspace register [<path>]` | Register a server-host path; omit path to register this directory when client and server share a host. | `--name <name>` — Workspace display name; defaults to the path's final segment.<br>`--type <type>` — Workspace type: spell, campaign, data, or custom (default). |
| `arcanum workspace show [<workspace>]` | Show one registered workspace and its server-host path. | None beyond global or inherited family options. |
| `arcanum workspace tree [<workspace>]` | List the complete server-side workspace tree recursively by following every opaque `nextCursor` from the 500-entry workspace-files API; each page keeps a bounded 501-candidate heap, and a changed/missing exact checkpoint returns an actionable restart error instead of offset-shift skips or duplicates. | `--path <path>` — Optional relative path inside the selected workspace. |
| `arcanum workspace info <path>` | Inspect a path through the server workspace API. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum workspace read <path>` | Read a file through the bounded server workspace API. Text output is the file's bytes verbatim; `--output-format json` emits `{ "path", "content" }`, which preserves trailing newlines and any escape sequences the file contains. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
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

The inference-only internal MCP surface pairs `execute_command` with automatically attuned `read_command_output`. Oversized stdout/stderr yields a bounded preview plus an opaque connection-lifetime handle and stream names. Continue each stream from byte offset `0` through each returned `nextOffset`; strict UTF-8 page size is a JSON-RPC-safe allocation bound, not a total-output ceiling. Each stream is deleted immediately after its final page; the handle expires after all streams finish or when the connection closes. Complete stdout and stderr share the existing explicit Sanctum `MaxFileWriteMb` policy, whose classified error reports the measured bytes and exact rerun or configuration action.

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

The server performs another research pass while it discovers new unique sources. It stops when an optional source target is reached, a pass discovers no new sources, the user/host cancels, an explicit token/cost policy is reached, or a provider/safety boundary fails. Progress and the exact target/no-progress terminal reason are written to stderr; the selected final format is written to stdout. There is no hop counter, but the fetch phase that follows the discovery passes is bounded even when no `--sources` target is given: a code-owned ceiling of 50 sources applies, and fetching also stops once the retained page text already covers the synthesis prompt's character budget. An explicit `--sources` target is the operator's own authority and is honoured as written. A transport failure — the host unreachable, the request timed out, the stream disconnected, or the stream ended without a result — reports the same copy and the same `Run 'arcanum doctor' to diagnose, or confirm 'arcanum serve' is running.` next step that `arcanum ask` appends, because it is the same failure; a provider or policy error keeps its own remedy untouched.

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
| `arcanum batch wait <id>` | Poll with bounded exponential backoff until the batch reaches a terminal state. | `--poll-interval <poll-interval>` — Initial poll interval in milliseconds (1-10000; default: 1000). |
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
| `arcanum attachment add <path>` | Create a snapshot from any local path, or use '-' to stream stdin. | `--mime <mime>` — Optional MIME type hint; the server remains authoritative.<br>`--name <name>` — Filename metadata, especially useful with stdin.<br>`--session <session>` — Session GUID, title, or unique title prefix. |
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
| `arcanum operation retry <id>` | Reset a failed, abandoned, repair-required, or unobserved-cancelling operation to Pending. A `Cancelling` row is admitted only once its lease has lapsed, so a cancellation still in progress cannot be yanked out from under its owner. | None beyond global or inherited family options. |
| `arcanum operation reconcile` | Process every recoverable operation in bounded internal pages/concurrency; exit 2 means automatic recovery completed but operator repair is still required. | None beyond global or inherited family options. |

A retried row does not wait for the original caller to come back. The reconciler treats a `Pending` row with a prior attempt as recoverable and re-drives it under its kind's registered recovery policy; a row still at attempt zero is left alone because its creator is about to lease it. `Cancelling` is recoverable on the same terms once its lease lapses, so a cancellation nobody observed is settled by the reconciler through the kind's handler instead of waiting forever for an owner that will never poll the flag.

Every registered operation kind has an owning recovery handler and an explicit recovery class (`DESIGN.md` §10.8.1). A `ReconciliationRequired` operation is therefore always a state recovery deliberately declined to resolve, never one nobody thought about; `arcanum operation list --state ReconciliationRequired` is the list of things that need you. Its terminal error code says why:

| Terminal error code | Meaning | What to do |
|---|---|---|
| `operation.checkpoint_version_unsupported` | The checkpoint was written outside the version window this build's handler can read — from a newer build, or from one whose format the handler no longer understands. | Run the build that wrote it, or `arcanum operation retry <id>` after confirming the work can safely restart from scratch. |
| `operation.checkpoint_corrupt` | The checkpoint payload could not be parsed. | The work cannot be resumed; `retry` restarts it from durable inputs where its policy allows. |
| `operation.recovery_handler_missing` | No handler is registered for the kind. This is a build defect, not a runtime state. | Report it. Recovery will not guess an outcome. |
| `operation.recovery_result_invalid` | A handler returned a non-terminal state. Also a build defect. | Report it. |
| `operation.link_missing` | The ledger row lacks the inference run, claim, or reservation id its handler needs, so recovery cannot tell which entity the crashed work owned. | Inspect the linked domain state with `arcanum operation show <id>`, then `retry` or `cancel`. A cancelled row is settled by the reconciler through the kind's handler once its lease lapses; no owner has to observe the flag. |
| `Covenant.ManualRecoveryRequired` | A Covenant erasure has a content-free manual blocker or malformed recovery evidence that automatic recovery cannot safely interpret. Valid current reset and factory-erasure checkpoints are adopted under their exact owner and resumed by the production coordinator. Factory V1 reruns ordinary cleanup when `ManagedArtifactsProcessed` is durable and skips it at `HandlesClosed` or later; reset V3 has no ordinary continuation, and legacy factory V0 is unchanged. | Keep admission closed, inspect `arcanum operation show <id>`, and follow the reported restore, Covenant-family reinitialize, or full-installation-reset remedy. Do not start a second erasure. |
| `Covenant.ErasureIncomplete` / `Covenant.MaintenanceFailed` / `Covenant.ManualArtifactErasureRequired` | Local proof, lifecycle finalization, or an owned-artifact deletion did not reconcile. These typed failures keep the exact operation recoverable and never imply that external disclosures were erased. | Keep admission closed and inspect the operation. Repair the named content-free blocker, then retry or reconcile the same operation; do not start another reset. |

`arcanum doctor` reports the same states as part of its `DurableOperations` panel, including stale operations (expired leases nobody has claimed), the count awaiting repair, per-kind repair guidance keyed by terminal error code, and any kind with no registered recovery handler. That detail comes from the host's `DurableOperations` health component: when the host cannot be reached the check warns and names the repair path rather than being omitted, and `--json` emits it as a `DurableOperations` check alongside the other diagnostics. Neither surface emits operation ids, public summaries, or checkpoint content into that detail.

### `arcanum backup`

Plan, create, inspect, verify, list, restore, and migrate versioned encrypted portable backups. This is a safe local operation over canonical Arcanum state: it takes a live snapshot through SQLite's online backup API and does not copy `arcanum.db`/WAL/SHM files directly. It accepts only the typed scopes and components below; no option admits an arbitrary source path.

The scope catalog is `full` (default), `configuration-and-authored-assets`, `sessions-and-memory`, `specific-session`, and `metadata-only`. `specific-session` requires the exact GUID passed to `--session-id`; broader scopes may also record a Session GUID as provenance without narrowing their inventory. Version 1 includes only matching Session attachments by default for `specific-session` and omits global uploaded/batch files unless those typed components are explicitly included. Its physical Grimoire snapshot remains indivisible, so the encrypted manifest warns about collateral global/accounting rows. Metadata-only creates an encrypted manifest with no state entries and does not need installation secrets.

The repeatable/multi-value component catalog is `grimoire-database`, `grimoire-kdf-metadata`, `portable-recovery-keys`, `configuration`, `session-attachments`, `uploaded-files`, `batch-artifacts`, `global-codex`, `global-spells`, `mcp-configuration`, `trusted-mcp-workspace-metadata`, `cli-state`, `the-forge-state`, `compendium-settings`, `compendium-certificates`, `audit-logs`, `guardrail-logs`, and `master-api-key`. Matching is case-insensitive but otherwise exact; numeric enum spellings and unknown values are rejected. Duplicates are harmlessly collapsed. If the same component appears in both `--include` and `--exclude`, exclusion wins. Trusted MCP metadata, both log families, and the master API key are omitted by default and must be explicitly included.

`compendium-settings` and `configuration` name the same physical `arcanum.json` state. Selecting only `compendium-settings` captures the file under that component even when `configuration` is excluded. Selecting both stores one configuration entry and reports `compendium-settings` as a complete zero-entry alias. The shared planner also records a bounded-stream SHA-256 fingerprint for every source; creation rejects identity, size, or fingerprint drift before capture, including an in-place change that preserves the inode and byte count.

Passphrases are never accepted as literal command arguments. With no explicit source, creation and verification read hidden terminal input; creation also confirms it. `--passphrase-env <name>` reads the value of that named environment variable. `--passphrase-fd <fd>` reads one UTF-8 line from an inherited descriptor, including descriptor `0`. When a command consumes a passphrase, negative descriptors are rejected and the two source options are mutually exclusive. `backup create --dry-run` consumes neither source, so a parsed negative descriptor or both source flags do not block its structurally valid inventory plan; parser syntax and type errors still fail before the handler. Prefer a descriptor for automation when practical, and do not put a secret value itself in shell-history guidance. The CLI rejects an empty passphrase but does not impose an arbitrary composition rule. `--json` can return plans/manifests and verification facts, but never includes the passphrase or portable key bytes.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum backup create` | Create an owner-only `.arcbackup`. The default destination is `~/.config/arcanum/backups/arcanum-<UTC timestamp>.arcbackup`. Required-component, source-identity, staging-identity, checksum, self-verification, cancellation, or publication failure leaves no new archive and returns a non-success result. Existing output is no-clobber by default. | `--scope <scope>` — Typed scope; default `full`.<br>`--session-id <guid>` — Required for `specific-session`; optional provenance for broader scopes.<br>`--include <component>...` — Repeat or supply several typed additions.<br>`--exclude <component>...` — Repeat or supply several typed omissions; exclusion wins over inclusion.<br>`-o, --output <path>` — Explicit `.arcbackup` destination.<br>`--dry-run` — Show the shared inventory plan, estimates, missing/nonportable paths, and warnings without prompting for the recovery passphrase or writing an archive.<br>`--overwrite` — Explicitly permit atomic replacement of an existing destination.<br>`--passphrase-env <name>` / `--passphrase-fd <fd>` — Noninteractive passphrase source. |
| `arcanum backup inspect <archive>` | Read only the bounded safe outer header by default. Decrypted inspection authenticates bounded chunks in memory, skips selected content, and shows the capped final manifest without creating plaintext staging. | `--decrypt` — Decrypt the manifest; prompts securely when neither explicit source is present.<br>`--passphrase-env <name>` / `--passphrase-fd <fd>` — Supply a passphrase source; supplying one also makes the manifest available. |
| `arcanum backup verify <archive>` | Authenticate the complete archive, validate its bounded structure/manifest and every entry size/SHA-256, and validate any Grimoire snapshot in protected temporary storage. Invalid archives return exit `1`; temporary plaintext is removed. | `--passphrase-env <name>` / `--passphrase-fd <fd>` — Optional noninteractive source; otherwise prompt securely once. |
| `arcanum backup list` | List safe outer headers for valid top-level `.arcbackup` files, newest first, without decrypting manifests. Missing directories yield an empty result; malformed/unreadable candidates are omitted. | `--directory <path>` — Directory to scan; defaults to `~/.config/arcanum/backups`. |
| `arcanum backup restore <archive>` | Verify an archive completely, stage the whole generation under a protected root, converge it onto this build's schema and this machine's paths and secret protection, then commit atomically or leave the installation exactly as it was. Refuses while a host or another restore holds the maintenance lock. Exit `1` for a rejected, rolled-back, or reconciliation-required outcome. | `--conflict-mode <mode>` — `replace-installation` (default), `new-profile-root`, or `import-selected-sessions`.<br>`--destination <path>` — Empty or absent profile root; required by `new-profile-root` and rejected elsewhere.<br>`--session-id <guid>...` — Sessions to import; required by `import-selected-sessions` and rejected elsewhere.<br>`--map <kind>=<from>=<to>...` — Typed root rewrite. Kinds: `campaign-root`, `workspace-root`, `codex-root`, `spell-root`, `attachment-source`.<br>`--map-campaign <source-campaign-id>=<destination-campaign-id>...` — Bind an archived Campaign to one that exists on this machine. Two Campaign ids, never names; `import-selected-sessions` needs one for every Campaign-bound Session and rejects the option elsewhere. A destination Campaign this installation does not have is a typed refusal naming it.<br>`--protected-state <mode>` — What the restore may do with the protected state the archive carries: `reject` (default), `restore-protected-state`, or `purge-protected-state`. Either non-default mode applies only to `replace-installation`, and asks its own confirmation after the nonrevocable-disclosure statement.<br>`--restore-master-api-key` — Adopt the archived master API key; off by default, and accepted only with `replace-installation`, the one mode that rebuilds local secret protection.<br>`--dry-run` — Authenticate, validate, plan, and check capacity without mutating anything.<br>`--no-safety-backup` — Skip the pre-restore safety backup; the decision is recorded in the result.<br>`--passphrase-env <name>` / `--passphrase-fd <fd>` — Noninteractive passphrase source.<br>The shared global `--yes` answers every destructive confirmation this command asks without prompting — the replacement **and**, when `--protected-state` names a non-default mode, the separate protected-state question. The nonrevocable-disclosure statement, the possible-attempt count, and the retention help targets are still written first, so an unattended run records what it was told. They share the question's stream (stderr) in every mode, so `--output-format json` still emits exactly one JSON document on stdout. When the protected-state plan carries blockers the restore refuses there, before any staging root, journal, or exclusive owner exists; text mode prints the blockers as `Issue: <code> - <message>` lines, and `--output-format json` emits the same `BackupRestorePlan` document `--dry-run` publishes, with the blockers on it as data. |
| `arcanum backup migrate <archive>` | Rewrite a supported archive at the current container format through the authoritative codec. Entry bytes carry across verbatim; the source archive is never modified, and a refusal writes nothing. | `-o, --output <path>` — **Required** destination for the migrated archive; it may not equal the source.<br>`--overwrite` — Explicitly permit replacing an existing migrated archive.<br>`--passphrase-env <name>` / `--passphrase-fd <fd>` — Noninteractive passphrase source. |

The encrypted manifest reports each component as `complete`, `omitted-by-policy`, `unavailable`, or `failed`, with requested includes/excludes, warnings, files, sizes, and SHA-256 values. The backup does not resolve environment references or separately export their values, raw OS credential/Data Protection stores, external workspace trees, daemon registration, or ephemeral process state; literal values already authored into a selected file remain part of that file.

Restore consumes exactly that artifact. It classifies the declared format against the supported matrix before staging, so an archive written by a newer Arcanum fails with upgrade guidance while the current installation is intact. It requires the archive to carry portable recovery material, and for `replace-installation` a Grimoire snapshot as well. Capacity planning reserves room for the restored generation *and* the displaced installation at the same time. Older supported snapshots converge through the same declarative schema installer the host uses at startup — never by editing migration history. Commit is two directory renames guarded by a filesystem journal; a fault at any phase yields a complete commit, a complete rollback, or an explicit reconciliation request, never a mixture of old and new trees. The Data Protection key ring (`keys/`) and existing archives (`backups/`) are carried across the swap because they belong to the destination, not the archive.

**The protected-state modes and the Campaign mapping are on the command line.** `BackupProtectedStateMode` (`reject`, `restore-protected-state`, `purge-protected-state`) decides what a restore may do with the protected state an archive carries, and all three are covered by the restore effect digest ([DESIGN §10.19.2](Arcanum.DESIGN.md#10192-the-restore-effect-digest)). `reject` is the default and it **refuses** — before the staged generation is composed, before admission is closed, and before any recovery state exists — when the archive carries a Covenant canonical row, a search projection, or a sensitivity label. `restore-protected-state` preserves that state only from a source whose own authority state is provably clean; a source-tainted archive carrying protected state fails closed under both, and the only supported continuation is a separately confirmed `purge-protected-state`, which securely removes the whole Covenant family and every protected artifact from staging before replacement while preserving this machine's own host-tools taint, its joined disclosure counts, and the receipts behind them. Either destructive mode requires its **own** confirmation, separate from the one that authorized replacing the installation, and before that prompt the operator reads the shared nonrevocable-disclosure statement, the receipt-backed possible-attempt count with exact or lower-bound semantics, and every resolved retention help target. Declining creates no staging root, no journal, and no exclusive owner. Both choices are on the command line. `--protected-state` reads the same `reject` / `restore-protected-state` / `purge-protected-state` table a plan already prints, so what an operator types and what a rehearsal reports are one vocabulary; the effect digest keeps its own domain-separated encoding of the mode, so renaming a command-line word can never invalidate an anchored owner. Omitting the option is `reject`, and a value this build does not know is refused as a configuration error before the passphrase is read, so a typo creates no staging root, no journal, and no exclusive owner. `--map-campaign <source>=<destination>` is repeatable and takes two Campaign **ids**, never names: the archive's Campaign names mean nothing here, and a mapping resolved by name would follow a rename on either machine into a binding the operator never chose. It is a different option from `--map campaign-root=<from>=<to>`, which rewrites a machine-specific path rather than deciding which Campaign an imported Session belongs to. A value that is not two `=`-separated Campaign ids, or that names the nil identity on either side, is a configuration error at parse time; everything else is validated by the plan before anything is staged — a mapping outside `import-selected-sessions`, one archived Campaign mapped to two destinations, and a destination Campaign this installation does not have are each a typed blocker naming what is wrong — and a Campaign-bound Session with no mapping is still refused with `Covenant.CampaignBindingConflict` rather than silently unbound, now naming the archived Campaign and the `--map-campaign` value that answers it. With the gate **off** a selective import takes the plaintext path, which writes every Session's `CampaignId` as `NULL`, so a mapping cannot be honoured at all there: `--map-campaign` is refused with `backup.restore_campaign_mapping_covenant_required` rather than accepted and dropped. An import that names no mapping behaves exactly as it always has. With the gate off — the default — restore behaves exactly as it always has, except that an explicit non-default mode is refused with `backup.restore_protected_state_covenant_required` rather than silently doing nothing. See [DESIGN §10.19.10](Arcanum.DESIGN.md#101910-enforcing-the-restore-protected-state-mode), [§10.19.12](Arcanum.DESIGN.md#101912-the-restore-command-line-for-the-choices-the-contract-already-modelled), and [§10.19.13](Arcanum.DESIGN.md#101913-what-is-deliberately-absent) for the full boundary.

**With the gate on, a replace-installation restore is one exclusive Covenant operation.** The staged reconciliation is wired: `arcanum backup restore` closes admission under a single `BackupRestore` owner before it stages anything, strips the archive's managed-file authority, reissues this dataset's Covenant identities, joins the destination's host-tools taint and disclosure evidence without ever laundering either, commits its Campaign marker cleanup children before the first displacement, and reopens admission exactly once. Two operator-visible consequences: every restored Campaign comes back **unresolved** and needs a fresh `arcanum campaign` registration before workspace tools will act on it, and a restore that cannot prove its marker children after the swap exits `ReconciliationRequired` with Covenant admission still closed — the next host or CLI start resumes that same operation rather than restarting it. A `--new-profile-root` restore is outside this arm entirely — and therefore outside the protected-state enforcement above — exactly as its existing "installs data only" warning says. See [DESIGN §10.19.9](Arcanum.DESIGN.md#10199-reconciling-protected-state-inside-restore-staging).

Restored attachment snapshots stay readable even when their originating workspace does not exist here, but their live provenance is demoted to `WorkspaceUnavailable` until the workspace is explicitly rebound and revalidated. Trusted MCP workspace metadata is withheld rather than installed, and `Host:ListenAny` is reset to `false`: neither is authorization that transfers between machines. `new-profile-root` installs data only and never writes secret protection for another root, so adopt that generation with a `replace-installation` restore before using it. When the configuration component has a committed preset generation, its authenticated entries also contain the paired `arcanum.preset.json` and `arcanum.preset.rollback.json`; the transient preset journal is never included. Restore the pair only beside its matching `arcanum.json` during a coordinated recovery. An incomplete or mismatched pair fails the configuration component. A pending journal prevents capture of a possibly mid-transaction configuration until preset recovery runs.

### `arcanum data`

Inspect and maintain persisted Arcanum data.

Read-only lifecycle inspection and every destructive retention command use the authenticated host API; the CLI never opens or mutates the Grimoire directly. `data prune` requires exactly one of `--dry-run` and `--apply`. Every mutation below prompts in an interactive terminal and requires the global `--yes` switch when confirmation cannot be obtained; cancellation sends no mutation request. Human mode prints concise status, settings, plan, and apply summaries. Global `--json` preserves the exact API payload; `--json --yes data prune --apply` emits one final apply result rather than a preview/result sequence. The separate encryption migration, verification, and key-rotation workers are resumable local operator operations with bounded worker settings. Inference and guardrail audit writers never delete historical JSONL files on a write; dated-log age removal is available only through the bounded server-owned `data prune` plan/apply path.

When a reset or healthy online global factory plan carries a Covenant inventory, one shared renderer writes the destructive-operation statement, the receipt-backed possible-attempt count with exact or lower-bound wording, and every resolved provider help target to diagnostics before the owning prompt. `--yes` suppresses the prompt, not the disclosure; `--json` therefore keeps stdout to its one structured document. Declining after disclosure sends no mutating request. Global/all first obtains the authenticated host plan and rebinds its exact data-plan identity into the local installation inventory; this happens before dry-run output, disclosure, or confirmation. Missing host/key/current Covenant inventory or a binding mismatch fails immediately. Workspace retains its offline path.

Retention-class matching is case-insensitive and ignores hyphens, underscores, and spaces. Grouped names such as `attachments`, `workspace-indexes`, `accounting`, and `daemon-history` are accepted; a typed attachment, batch-file, workspace, or accounting subclass updates the rule that governs its dependency group. Setting a rule to `disabled` preserves its current day value.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum data status` | Show rows, files, estimated bytes, effective policy, store, and provenance for every typed retention class, plus aggregate totals and categories preserved outside the selected root. | None beyond global or inherited family options. |
| `arcanum data retention show` | Show the effective unified retention settings and per-class rules. | None beyond global or inherited family options. |
| `arcanum data retention set <class> <days\|disabled>` | Set one named typed rule to clamped retention days or disable policy selection for that class. Numeric enum spellings are rejected before confirmation; disabling preserves the prior day value and does not hide status or authorize/deauthorize an explicit item-scoped deletion. | Mutation confirmation; use global `--yes` for automation. |
| `arcanum data prune --dry-run` | Build the current bounded deletion plan without mutating data. The plan reports class rows/files/estimated bytes/derived records, candidates, blockers, active-operation conflicts, and its content-derived `planId`. | `--dry-run` — Required mutually exclusive preview mode. |
| `arcanum data prune --apply` | Fetch and display the exact current plan, confirm its id and totals, then send that `planId` as `expectedPlanId` to a durable, checkpointed apply. The server rechecks blockers/conflicts and verifies each selected candidate's owned rows, derived records, and files after deletion; repeated runs converge. JSON mode keeps the preview silent and emits exactly one final result. | `--apply` — Required mutually exclusive apply mode.<br>Mutation confirmation; use global `--yes` for automation without another step. |
| `arcanum data delete-session <id>` | Delete one session and its Entries, session-scoped attachment metadata/bytes, and derived Entry/attachment indexes. Pinned Entries/context, operator holds, active work, and outstanding accounting state block the plan. | Mutation confirmation; use global `--yes` for automation. |
| `arcanum data delete-attachment <id>` | Delete one attachment version, its encrypted bytes, chunks, embeddings, and index state. A pinned attachment/context blocks deletion; independently retained Saga/Lexicon facts keep typed provenance and report the source unavailable. | Mutation confirmation; use global `--yes` for automation. |
| `arcanum data reset-memory --scope <scope>` | Reset exactly one named store: `entry`, `attachments`, `workspace`, `saga`, `lexicon`, or `covenant`. There is no ambiguous generic memory delete, and numeric enum spellings are rejected before any API request. Every scope first calls the dedicated read-only reset-plan route and later sends that preview id as `expectedPlanId`. For `covenant`, the CLI additionally writes the nonrevocable external boundary, the receipt-backed possible-attempt count, and resolved provider help targets before confirmation, then enters the durable ten-phase protected-erasure coordinator. Plan drift is refused before effects. Success keeps the confirmed plan id, reports reconciliation, and does not invent row, file, byte, or derived-record deletion totals from retained disclosure evidence. | `--scope <scope>` — Required explicit memory scope.<br>Mutation confirmation; use global `--yes` for automation. Covenant scope writes its disclosure to diagnostics before either path. |
| `arcanum data factory-reset (--workspace\|--global\|--all) (--dry-run\|--apply)` | Preview or apply the closed installation-reset plan. `--workspace` targets the most-specific registered Campaign containing the current directory, its exact-root derived Grimoire rows, and its `.arcanum` tree. `--global` targets installation-wide state, configured credential identities, and daemon registration. `--all` captures the current Campaign before running the global phase. Global/all rebind the authenticated online plan before any output or confirmation; missing host/key/inventory fails before shutdown. Apply sends a typed in-memory handoff to the running host; under its exact installation lock the host publishes owner-only authenticated V2 `Prepared + HostFactoryErasure`, uses its requested-operation identity only as the replay key for a distinct server operation, and durably records the content-free completion proof before responding. The CLI then shuts down the host, acquires the exact maintenance lock, and passes it through offline continuation. Exact order remains `prepare -> host apply/replay -> proof -> shutdown -> lock -> offline continuation`. The data phase requires a measurable current inventory and exclusive lifecycle, then runs protected erasure first and ordinary factory cleanup between `ManagedArtifactsProcessed` and `HandlesClosed`; missing proof fails closed rather than running ordinary cleanup alone, and recovery repeats that cleanup only before `HandlesClosed` is durable. Cancellation or an uncertain host outcome preserves authenticated evidence for replay; only a proven pre-effect `Data.PlanChanged` may close and retire it. Recognized `.arcbackup` files and nested registered Campaign roots are preserved. Ordinary global/all apply rereads and requires a clean host-tools marker pair before client coordination, host-handoff creation, the online host factory call, shutdown, lock acquisition, or any reset effect; missing pair-reader composition fails closed. The sole external-remediation spelling is `arcanum data factory-reset --all --apply --external-remediation-attestation <file>`: before configuration bootstrap the CLI requires that exact mode, then securely reads one owner-controlled file of at most 64 KiB and strictly decodes its source-generated version-1 JSON contract. The P-256/SHA-256 signature, fixed issuer, 24-hour lifetime, signed operation, installation identity, exact live `TaintedMatched` marker evidence, action, and nonce must all verify through the code-pinned public root. The signed operation ID becomes the authenticated active-record identity; this slice creates no host handoff, online replay, or reset effect. Fresh acceptance must occur inside the statement's signed issue/expiry window. The exact authenticated pre-effect claim may resume after expiry only while the supplied statement and a final authoritative live installation/marker snapshot remain exact. Startup projects no ordinary host handoff from that claim; a missing retry attestation fails before the apply boundary, and the ordinary exact-lock seam rejects the claim even if the pair later reads clean. Acceptance records only the minimal encrypted, authenticated remediation claim fields and keeps the installation fail-closed; marker/Campaign compare-deletion is #122 and remaining credential, file, and identity terminalization is #123. No statement plaintext, signature, nonce, issuer text, file path, trust-root material, or private-key material is written to command output, confirmation, diagnostics, logs, or an HTTP request; the signed operation ID remains intentionally visible as the reset operation identity. | Exactly one scope and mode are required. Interactive apply requires the case-sensitive text `RESET`; global and all scopes write any Covenant disclosure first. Noninteractive apply requires both global `--yes` and command-local `--force`; `--force` is invalid with dry-run, and global/all automation still emits any disclosure diagnostics.<br>`--external-remediation-attestation <file>` — Valid only with exact `--all --apply`; supplies the independently signed remediation statement. It is never an alternative to `--yes`, `--force`, or interactive confirmation, and no other command or mode accepts it. |
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
| `arcanum use clear [<scope>]` | Clear all saved context when scope is omitted, or clear campaign, workspace, model, or session context. Only those four names are accepted; numeric enum spellings and comma-separated flag lists are rejected with exit `2` and the expected names, so no unnamed scope is ever cleared. | None beyond global or inherited family options. |

### `arcanum context`

Inspect effective CLI context and its sources.

Explains the effective values and previews model context without running main inference. Content remains hidden unless `--show-content` is supplied.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum context current` | Show effective campaign, workspace, model, and session context. | None beyond global or inherited family options. |
| `arcanum context inspect [<prompt>...]` | Inspect the complete effective turn context without running main inference. | `--show-content` — Include model-visible content for explicit operator inspection.<br>`--no-retrieval` — Skip embedding and RAG retrieval work.<br>`-C, --campaign <campaign>` — Campaign GUID or name; defaults to saved/detected context.<br>`-w, --workspace <workspace>` — Workspace ID or path; defaults to saved/detected context.<br>`-m, --model <model>` — Model name; defaults to saved/server context.<br>`-s, --session <session>` — Session GUID, title, or prefix; defaults to saved context. |
| `arcanum context tools [<prompt>...]` | Inspect effective turn tools without main inference. | `--show-content` — Include model-visible content for explicit operator inspection.<br>`--no-retrieval` — Skip embedding and RAG retrieval work.<br>`-C, --campaign <campaign>` — Campaign GUID or name; defaults to saved/detected context.<br>`-w, --workspace <workspace>` — Workspace ID or path; defaults to saved/detected context.<br>`-m, --model <model>` — Model name; defaults to saved/server context.<br>`-s, --session <session>` — Session GUID, title, or prefix; defaults to saved context. |
| `arcanum context sources [<prompt>...]` | Inspect effective turn sources without main inference. | `--show-content` — Include model-visible content for explicit operator inspection.<br>`--no-retrieval` — Skip embedding and RAG retrieval work.<br>`-C, --campaign <campaign>` — Campaign GUID or name; defaults to saved/detected context.<br>`-w, --workspace <workspace>` — Workspace ID or path; defaults to saved/detected context.<br>`-m, --model <model>` — Model name; defaults to saved/server context.<br>`-s, --session <session>` — Session GUID, title, or prefix; defaults to saved context. |
| `arcanum context cost [<prompt>...]` | Estimate the effective turn token allocation without main inference. This absorbs the former top-level `mana` command and matches the `/cost` slash command. | `--show-content` — Include model-visible content for explicit operator inspection.<br>`--no-retrieval` — Skip embedding and RAG retrieval work.<br>`-C, --campaign <campaign>` — Campaign GUID or name; defaults to saved/detected context.<br>`-w, --workspace <workspace>` — Workspace ID or path; defaults to saved/detected context.<br>`-m, --model <model>` — Model name; defaults to saved/server context.<br>`-s, --session <session>` — Session GUID, title, or prefix; defaults to saved context. |

### `arcanum preset`

Inspect, preview, apply, and reset transparent onboarding presets.

The six built-in version-1 presets are `general-assistant` (**General Assistant**), `coding-workspace` (**Coding Workspace**), `research` (**Research**), `private-offline` (**Private/Offline**), `automation` (**Automation**), and `advanced-custom` (**Advanced/Custom**). `<name>` accepts an exact ID or exact display name; quote display names that contain spaces or shell punctuation. Definitions are partial overlays: only their declared owned paths can change, and Advanced/Custom owns none.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum preset list` | List each ID, version, display name, purpose, and effective state. The active item is `Active` or `Drifted`; other entries are `Available`. | None beyond global or inherited family options. |
| `arcanum preset show <name>` | Show the shared purpose, exact owned values, restart flags, enables/disables, security and provider requirements, resource/cost behavior, prerequisite setup commands, first essential choice, deferred advanced features, recommendations, and Ward/Sanctum/Weave/Saga/Lexicon glossary. | None beyond global or inherited family options. |
| `arcanum preset diff <name>` | Read-only plan showing applicability, idempotency, prerequisite status, completion summary, and every owned persisted/effective/proposed value with its source, environment-override name/effectiveness, ownership, and restart/change flags. | None beyond global or inherited family options. |
| `arcanum preset apply <name>` | Build and canonically validate the complete candidate, then atomically write only the preset-owned overlay with provenance and rollback state. Reapplying the same version and owned values is a successful no-op. | None beyond global or inherited family options. |
| `arcanum preset reset` | Restore unchanged preset-owned values to their pre-apply baseline, preserve user drift and all unrelated settings, clear active provenance, and report restored/preserved counts plus rollback status. No active preset is a successful no-op. | None beyond global or inherited family options. |

Plain and `--json` modes are projections of the same shared service. Secret-shaped canonical values are `***`; environment-variable names may be shown, but their values never are. Persisted value means the value in `arcanum.json`; effective value includes recognized environment layering; proposed persisted value is what apply would write. When an environment override is effective, the persisted value can change without an effective-value change, and `diff` reports both flags instead of misrepresenting runtime truth. Only an effective override that contradicts an owned safety/privacy boundary blocks Apply. Benign feature masks remain authoritative and are reported as drift without making the plan inapplicable. The secure research-credential store is consulted only for Research `diff` and `apply`; listing, showing, state inspection, reset, and other presets do not probe it.

Preset state is separate owner-only provenance, not a setting: no provenance is `Custom`, an exact owned-value match is `Active`, and a later persisted or effective difference is `Drifted`. Apply uses an expected-settings hash, the current-user cross-process coordinator shared by all canonical configuration writers, an owner-only rollback baseline, a prepared transaction journal, atomic replacement, and post-write verification. The journal stores only owned before/after values and hashes plus previous/next provenance. Bounded no-follow sidecar reads and exact catalog ownership, value, hash, and state/rollback validation reject forged or stale provenance. Reset and recovery restore a baseline path only while its persisted value still matches the transaction's applied value; manual drift and unrelated edits win. Apply/reset are already explicit mutation commands and do not prompt or require `--yes`.

Required provider/model, workspace, research-credential, loopback-provider, or positive-budget prerequisites are reported with exact setup commands. A plan applies only when required prerequisites and complete canonical validation succeed. Presets never supply provider secrets, invent budgets, bypass Ward or Sanctum, silently enable network exposure/unsandboxed children/ untrusted MCP/destructive memory, or add retry, timeout, loop-count, or other arbitrary tuning knobs. Every plan concludes with active preset, provider/model, workspace/campaign, memory sources, tool policy, privacy state, and next recommended command. Recommendations are directly executable; Coding Workspace uses `arcanum run --workspace . "Inspect this workspace and summarize it."`, including the required prompt.

These commands expose the reusable preset service directly. [`arcanum setup`](#arcanum-setup) composes the same service as its preset step and applies exactly the same overlay, so the two surfaces can never disagree about what a preset owns.

### `arcanum config`

Safely inspect, validate, edit, and open Arcanum configuration.

Inspects and changes `arcanum.json` through descriptor-backed parsing and validation. `get` and `set` use dotted descriptor paths such as `host.port` or `providers.0.endpoint`. Secrets stay redacted; sensitive provider endpoint values come from stdin or a hidden prompt.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum config path` | Print the exact arcanum.json path. | None beyond global or inherited family options. |
| `arcanum config show` | Show the effective configuration with secrets redacted. | None beyond global or inherited family options. |
| `arcanum config get <key>` | Show one value selected by its dotted descriptor path. | None beyond global or inherited family options. |
| `arcanum config set <key> [<value>]` | Parse, validate, and atomically set one dotted descriptor path; sensitive values use redirected stdin or a hidden prompt. Under `-p/--print` or `--output-format json` the hidden prompt is skipped and a sensitive value must arrive on redirected stdin, otherwise the command returns `2`. | None beyond global or inherited family options. |
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
| `--tool <tool>` | Show events for matching tool names; repeat for multiple free-form, case-insensitive values. |

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum watch session [<session>]` | Follow replayed and live Session entries. | `--since <since>` — Begin after this Session Entry GUID. |
| `arcanum watch apprentice [<apprentice>]` | Follow an Apprentice Chronicle. | None beyond global or inherited family options. |
| `arcanum watch logs` | Follow live host log entries. | `--level <level>` — Minimum server log level (free-form; validated by the API).<br>`--category <category>` — Match a log category, case-insensitively.<br>`--search <search>` — Search log messages and categories, case-insensitively. |
| `arcanum watch mcp` | Follow live MCP server lifecycle events. | None beyond global or inherited family options. |
| `arcanum watch daemons` | Follow live Unseen Servant daemon events. | None beyond global or inherited family options. |
| `arcanum watch health` | Poll authenticated host health snapshots. | `--interval <interval>` — Seconds between health observations (default: 5; any positive integer). |

### `arcanum completion`

Generate shell completion from the canonical command tree.

Generation is pure: it reads the command tree and writes a script, touching no network and no state. Output is deterministic and free of host, account, and endpoint values, so the same tree produces identical bytes on any machine and a generated script is safe to commit or share.

Completion is a projection of the live parser, so it offers exactly the commands and options the binary accepts. Removed spellings cannot reappear through it.

**Syntax:** `arcanum completion <shell>`

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum completion <shell>` | Write the completion script for `bash`, `zsh`, `fish`, or `powershell` to stdout. | None beyond global or inherited family options. |
| `arcanum completion install <shell>` | Write the script to this shell's conventional per-user location after confirmation. | `--target <target>` — Explicit destination path; defaults to the shell's conventional per-user completion location. |

Installation names the exact target on stderr before asking, reports when an existing file will be replaced, writes through a temp file and atomic replace, and prints the sourcing step for that shell. It is a mutation, so a redirected invocation without `--yes` fails closed rather than writing to a shell configuration unattended. A completion script is not a secret and `--target` is an operator-owned path, so an existing target directory keeps whatever permissions its owner chose; only a directory the install itself has to create is made owner-only.

Default targets, all under the operator's own home directory:

| Shell | Target |
|---|---|
| bash | `~/.local/share/bash-completion/completions/arcanum` |
| zsh | `~/.zfunc/_arcanum` |
| fish | `~/.config/fish/completions/arcanum.fish` |
| powershell | `~/.config/powershell/arcanum.completion.ps1` |

The fish script gates each path on `test "$(__arcanum_path)" = "<path>"`, so it needs fish 3.4 or newer — that is the release where `$(…)` substitutes inside double quotes.

**Dynamic completion.** Where a symbol names a live resource — models, providers, Campaigns, Workspaces, Sessions, Spells, Prompts, Apprentices, and visible MCP servers — the generated script calls a hidden `completion resolve` helper. Both halves of the surface are covered: an option's values are offered after the option under every spelling the parser accepts, short flag included, so `arcanum run -m <TAB>` resolves models exactly as `--model` does; and a command's resource positional is offered at that command's path until it has been supplied, so `arcanum campaign show <TAB>` names Campaigns while `arcanum campaign show Arcanum <TAB>` moves on to the options. A positional named generically resolves from the family it sits in — the `id` of `campaign delete` is a Campaign — and a generic positional outside a resource family, such as `batch cancel <id>` or the free prompt text of `run`, is deliberately left with no source rather than offered names the parser would reject. That path is bounded by design because it runs inside a keystroke: it never starts the host, gives up on its own short budget rather than making the shell wait, prints nothing at all on failure so static completion simply continues, and caches only names briefly. Prompt text, transcripts, endpoints, credentials, MCP commands/arguments/environment, attachment contents, and tool arguments are never read or cached by it.

### `arcanum help`

Explain a task-oriented topic in plain language, with the commands that do it.

`--help` answers "what are this command's options"; `arcanum help <topic>` answers "how do I do X". Each topic glosses the thematic vocabulary in plain terms before naming commands, so an operator who has not read the metaphor table can still navigate. Omit the topic to list them all.

**Syntax:** `arcanum help [<topic>]`

| Topic | Covers |
|---|---|
| `sessions` | Starting, continuing, branching, and finding conversations. |
| `memory` | What Arcanum remembers between turns, and how to compress or inspect it. |
| `attachments` | Getting files, images, and pinned context into a turn. |
| `security` | Approval gates, credentials, and filesystem boundaries. |
| `automation` | Running Arcanum from scripts, CI, and background jobs. |
| `context` | What Arcanum sends to the model, and how much it costs. |
| `output` | Controlling what reaches stdout, stderr, and the terminal. |

## Removed spellings

Arcanum maintains no backward-compatibility or data-migration path, so there is no alias layer: exactly one spelling resolves each action. The spellings below were removed. Each fails to parse with exit `2` and a diagnostic naming its replacement, which is the entire migration path.

| Removed | Use instead |
|---|---|
| `arcanum ask` | `arcanum run` |
| `arcanum chat` | Bare `arcanum` for interactive work, or `arcanum run` for one-shot work |
| `arcanum mana` | `arcanum context cost` |
| `arcanum session chat` | `arcanum run -c`, `arcanum run -r <id>`, or `arcanum run --session <id>` |
| `arcanum session get` | `arcanum session show` |
| `arcanum session watch` | `arcanum watch session` |
| `arcanum workspace get` | `arcanum workspace show` |
| `arcanum mcp get` | `arcanum mcp show` |
| `arcanum campaign get` | `arcanum campaign show` |
| `arcanum campaign use` | `arcanum use campaign` |
| `arcanum spell get` | `arcanum spell show` |
| `arcanum prompt get` | `arcanum prompt show` |
| `arcanum model get` | `arcanum model show` |
| `arcanum provider get` | `arcanum provider show` |
| `arcanum ward get` | `arcanum ward show` |
| `arcanum apprentice get` | `arcanum apprentice show` |
| `arcanum apprentice chronicle` | `arcanum watch apprentice` |
| `arcanum batch watch` | `arcanum batch wait` |
| `--tool-name` on watch commands | `--tool` |
| `--campaignId`, `--sessionId`, `--agentUrl` | Their kebab-case forms |
| `--content-type` on `attachment add` | `--mime` |
| `-c` for `--campaign` | `-C`; `-c` is now `--continue` |
| `/mana` | `/context` |
| `/new`, `/session new` | `/clear` |
| `/summary`, `/log` | `/memory` |
| `/rest` | `/compact` |
| `/history` | `/session list` |
| `/delete` | `/session archive` |
| `/session resume` | `/resume` |
| `/context list`, `/context pin`, `/context unpin` | `/pins`, `/pin`, `/unpin` |

`arcanum batch wait` is deliberately not spelled `watch`: it polls a REST resource until it reaches a terminal state, while `watch <source>` is the live SSE-stream family with its own `--reconnect`/`--event-type` contract. Sharing the verb implied a shared mechanism that does not exist.

## Watch stream details

Watch terminal output uses UTC timestamps and source-specific colors. The shared SSE parser joins multi-line `data:` fields, treats comment frames as stderr liveness diagnostics, and treats `[DONE]` as successful completion. `--json` writes only compact source event objects to stdout.

`--reconnect` retries network failures, unexpected EOF, and transient HTTP 408/425/429/5xx responses with exponential delays capped at 30 seconds. Authentication, validation, not-found, and connection-cap denials are terminal. Every reconnect warns that events may have been missed; only session watch carries the last valid Entry ID forward, and that cursor is not a replay guarantee.

## Related documentation

- [`README.md`](../README.md) — installation and quick-start workflows.
- [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md) — architecture, ownership, security, and implementation rationale.
- [`Arcanum.API.md`](Arcanum.API.md) — HTTP routes, wire shapes, status mapping, and public error codes.
- [`Arcanum.DEBUGGING.Human.md`](Arcanum.DEBUGGING.Human.md) — operator troubleshooting.
- [`Arcanum.CommandMap.json`](Arcanum.CommandMap.json) — the machine-readable command map: every command path, argument, option, alias, closed value set, dynamic-completion source, and example, projected from the live parser. It is regenerated and diffed by test, so an unintended entry in its diff is an unintended change to the public CLI surface.
