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
JSON. Redirected stdin is used where explicitly documented, notably secret and tool-argument input.

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

Resource-taking commands resolve an explicit exact ID first, then an exact case-insensitive name, then a unique case-insensitive name prefix. Omitted selectors may open a searchable picker only when stdin and stdout are interactive and output is not JSON. Redirected, ambiguous, or cancelled selection never guesses.

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

Command-specific refinements:

- `ask` returns `0` on success, `1` for empty prompt, inference-option, stream, or API failure, and
  `130` when the in-flight turn is cancelled.
- `chat` returns `0` after a clean REPL exit and `1` if any turn failed. Ctrl+C during a turn
  cancels that turn and returns to the prompt rather than exiting `130`.
- Bare Command Center returns `0` after `/exit` or `/quit`, for non-interactive usage, and when
  `ARCANUM_NO_COMMAND_CENTER=1`; terminal-size or UI-bootstrap failure returns `1`.
- Watch commands and their compatibility aliases return `0` on normal completion, `2` on parse
  failure or a non-positive health interval, `1` on validation/API/unexpected-disconnect failure,
  and `130` on cancellation.
- `trial run` returns `1` when the completed Trial result is not passing, independently of HTTP
  or validation failure.
- `operation reconcile` returns `2` when reconciliation succeeds but one or more operations still
  require operator repair; otherwise it returns `0` for a successful pass.

## Handler-validated required values

Some options are nullable in the generated parser so handlers can resolve saved context, read a secure value, or produce a better error. The following requirements are therefore enforced after parsing even when short help displays square brackets:

| Command | Runtime requirement |
|---|---|
| `ask` | A non-empty prompt is required. |
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
| `/campaign list` | List campaigns. |
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
| `/spell list` | List spells. |
| `/ward list` | List open Wards. |
| `/ward allow [<id>]` | Allow the supplied or currently prompted Ward. |
| `/ward deny [<id>]` | Deny the supplied or currently prompted Ward. |
| `/exit`, `/quit` | Leave Command Center. |

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
| `--response-format <response-format>` | Response format: text, json_object, or json_schema. |
| `--presence-penalty <presence-penalty>` | Presence penalty from -2 through 2. |
| `--frequency-penalty <frequency-penalty>` | Frequency penalty from -2 through 2. |
| `--image <image>` | Local image path to stage as a Scrying focus; repeatable, constrained by configured size and allowed MIME types, and requires a vision-capable model. |
| `--attachment <attachment>` | Bound attachment GUID to include; repeatable. |

### `arcanum chat`

Interactive multi-turn REPL with the Mage.

Starts the multi-turn Mage REPL. Inference controls apply to every turn, while `--attachment` values are staged for the next successful turn. See the REPL slash-command table above.

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
| `--response-format <response-format>` | Response format: text \| json_object \| json_schema. |
| `--presence-penalty <presence-penalty>` | Presence penalty -2..2. |
| `--frequency-penalty <frequency-penalty>` | Frequency penalty -2..2. |
| `--attachment <attachment>` | Bound attachment GUID to use on the next successful turn; repeatable. |

### `arcanum look`

Eye of the World: situational snapshot of the current directory (domain + TOC).

Builds an Eye of the World snapshot for the current directory locally, without requiring the HTTP host.

**Syntax:** `arcanum look`

### `arcanum doctor`

Run environment diagnostics (version, paths, API health).

Runs System, Paths, Configuration, MCP, Tokenizer, File Encryption, durable-operation, and authenticated API-health diagnostics. The health probe has a code-owned two-second timeout: an unreachable API is a warning, while hard local checks return a nonzero exit. `--json` emits the typed doctor report rather than decorated panels.

**Syntax:** `arcanum doctor`

| Option | Meaning |
|---|---|
| `--fix-permissions` | Apply owner-only permissions to the Grimoire database, arcanum.json, and secret store. |

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

Maintains the legacy operator-owned key/value MageSettings store. Lore is distinct from Lexicon entities, Saga memories, session entries, and attachments.

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
| `arcanum session delete-entry [<entry>]` | Delete an entry after confirmation. | `--session <session>` — Session GUID, exact title, or unique title prefix; omit for an interactive picker. |
| `arcanum session pin-entry [<entry>]` | Pin an entry when memory management is enabled. | `--session <session>` — Session GUID, exact title, or unique title prefix; omit for an interactive picker. |
| `arcanum session unpin-entry [<entry>]` | Unpin an entry when memory management is enabled. | `--session <session>` — Session GUID, exact title, or unique title prefix; omit for an interactive picker. |
| `arcanum session compact [<session>]` | Compact session context when memory management is enabled. | None beyond global or inherited family options. |
| `arcanum session divine <query>` | Semantic search over Grimoire entries. | `--limit <limit>` — Maximum number of matching entries to return.<br>`--campaign <campaign>` — Campaign GUID filter; names and prefixes are not accepted here.<br>`--status <status>` — Filter semantic session search by status; defaults to active. |

### `arcanum saga`

Saga long-term associative memory (requires arcanum serve).

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
| `arcanum memory status [<session>]` | Show feature gates and counts by memory store. | None beyond global or inherited family options. |
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

Manages built-in and workspace spells, named version files, validation, dry-run casting, and execution. Workspace selectors resolve server-host resources.

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
| `arcanum apprentice start [<id>]` | Start plan generation and execution. | None beyond global or inherited family options. |
| `arcanum apprentice pause [<id>]` | Pause at the next step boundary. | None beyond global or inherited family options. |
| `arcanum apprentice resume [<id>]` | Resume from checkpoint. | None beyond global or inherited family options. |
| `arcanum apprentice cancel [<id>]` | Cancel execution. | None beyond global or inherited family options. |
| `arcanum apprentice reweave [<id>]` | Replace the remaining plan steps. | `--plan <plan>` — JSON array of plan steps: inline text, or @filename to read from a file. |
| `arcanum apprentice intervene [<id>]` | Provide Divine Intervention guidance to an escalated Apprentice. | `--guidance <guidance>` — Guidance text for the escalated Apprentice. |
| `arcanum apprentice cast [<id>]` | Delegate a child Apprentice via The Conclave. | `--goal <goal>` — Child Apprentice goal text.<br>`--name <name>` — Display name for the child Apprentice. |
| `arcanum apprentice chronicle [<id>]` | Stream live Apprentice events (SSE). | None beyond global or inherited family options. |

### `arcanum model`

Native model listing across configured providers (requires arcanum serve).

Lists or selects configured models without exposing provider endpoints or credentials.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum model list` | List configured models across all providers (GET /api/models). | None beyond global or inherited family options. |
| `arcanum model get [<model>]` | Show a configured model without exposing its endpoint. | None beyond global or inherited family options. |

### `arcanum provider`

Native provider listing and configuration summary (requires arcanum serve).

Lists or selects configured providers while keeping endpoints and credential details redacted.

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
| `arcanum workspace tree [<workspace>]` | List the server-side workspace tree recursively. | `--path <path>` — Optional relative path inside the selected workspace. |
| `arcanum workspace info <path>` | Inspect a path through the server workspace API. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum workspace read <path>` | Read a file through the bounded server workspace API. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection. |
| `arcanum workspace search <query>` | Semantically search the selected workspace's server-side index. | `--workspace <workspace>` — Workspace ID, name, or server path; defaults to saved context or current-path detection.<br>`--limit <limit>` — Optional bounded result count. |
| `arcanum workspace index [<workspace>]` | Request a server-side workspace re-index. | None beyond global or inherited family options. |
| `arcanum workspace index-status [<workspace>]` | Show server-side workspace indexing status. | None beyond global or inherited family options. |
| `arcanum workspace chunks [<workspace>]` | Inspect bounded previews of server-side indexed chunks. | `--path <path>` — Optional relative-path filter.<br>`--limit <limit>` — Maximum indexed chunks to return.<br>`--offset <offset>` — Number of indexed chunks to skip. |
| `arcanum workspace unregister [<workspace>]` | Remove a workspace registration without deleting files. | None beyond global or inherited family options. |

### `arcanum mcp`

Operate MCP lifecycle, trust, discovery, and external-only diagnostics without exposing server secrets.

Administers MCP server lifecycle, trust, tool discovery, and external diagnostics. Safe projections omit URLs, commands, arguments, environment variables, working directories, and credentials. `invoke [arguments]` accepts inline JSON, `@file`, or redirected stdin and uses `{}` when omitted interactively; input is bounded to 1 MiB of UTF-8 JSON and depth 64.

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

Run bounded server-side multi-hop research with progress and citations.

Runs server-owned bounded multi-hop research. Progress is written to stderr; the selected final format is written to stdout.

**Syntax:** `arcanum research <question>`

| Option | Meaning |
|---|---|
| `--max-sources <max-sources>` | Maximum unique sources (1-20; default 5). |
| `--max-hops <max-hops>` | Maximum search hops (1-5; default 2). |
| `--model <model>` | Server-configured model for final synthesis. |
| `--token-budget <token-budget>` | Maximum synthesis output tokens (64-32768; default 2000). |
| `--cost-budget <cost-budget>` | Maximum reported search-provider cost in USD. |
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
| `arcanum file delete <id>` | Delete uploaded file metadata and content after confirmation. | None beyond global or inherited family options. |

### `arcanum batch`

Create and operate asynchronous OpenAI-compatible batches.

Uses the OpenAI-compatible `/v1/batches` surface. Local JSONL preflight catches basic shape errors; the server remains authoritative. Output/error downloads use safe filenames and overwrite confirmation. `batch watch --json` emits the final terminal batch object, unlike live `watch ... --json` NDJSON.

| Command | Explanation | Additional command options |
|---|---|---|
| `arcanum batch create <input-file>` | Create a batch from a local JSONL file or an existing uploaded file ID. | None beyond global or inherited family options. |
| `arcanum batch list` | List batches with request counts and status. | `--status <status>` — Filter by exact batch status. |
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
| `arcanum operation reconcile` | Run one bounded recovery pass; exit 2 means the pass completed but operator repair is still required. | None beyond global or inherited family options. |

### `arcanum data`

Inspect and maintain persisted Arcanum data.

Inspects and maintains encrypted persisted blobs. Migration, verification, and key rotation are resumable server-owned operations with bounded worker settings.

| Command | Explanation | Additional command options |
|---|---|---|
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
