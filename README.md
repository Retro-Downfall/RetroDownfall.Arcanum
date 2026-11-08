# Arcanum

**A local-first AI assistant with durable memory, practical tools, and one API for your models.**

[![CI](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/actions/workflows/ci.yml/badge.svg)](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Retro-Downfall/RetroDownfall.Arcanum?include_prereleases&sort=semver&label=release)](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/releases)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-yes-2ea44f)](#one-executable-no-runtime)
[![Platforms](https://img.shields.io/badge/platforms-macOS%20arm64%20%C2%B7%20Windows%20x64%2Farm64-blue)](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/releases)

Connect a local model server or hosted provider, work from the terminal or desktop, and keep your conversations and memory in an encrypted store on your own machine. Arcanum combines a progress-driven tool loop with memory you can inspect, correct, scope, and erase.

The Native AOT `arcanum` executable is both the HTTP host and its command-line client. It runs on Apple Silicon macOS and Windows x64/arm64 without a separately installed .NET runtime. Models run through your configured providers; Arcanum does not bundle a model runtime.

## What you can do

- **Work across models.** Use OpenAI-compatible hosted APIs, Ollama, or another local model server behind the same commands and API.
- **Carry work across conversations.** Organize Sessions into Campaigns, maintain cross-session summaries, and retrieve relevant files and memories.
- **Act on your workspace.** Read, search, and edit files; use MCP tools; run permitted commands; and repeat workflows with named Spells and stored Prompts.
- **Manage what the assistant remembers.** Inspect provenance and eligibility, correct facts, review new memories, pin useful material, retire outdated claims, or erase an exact item.
- **Evaluate and observe.** Inspect planned context, track usage and budgets, run Trials, and compare answers with and without selected memory.

Capabilities have explicit feature, provider, and platform requirements. The [configuration reference](docs/Compendium.README.md#feature-opt-ins) explains what each switch enables.

## Install

Download the appropriate archive from [Releases](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/releases) and verify it against `SHA256SUMS.txt` from the same release.

**macOS — Apple Silicon.** The release is signed with Developer ID and notarized.

```bash
unzip arcanum-osx-arm64.zip
cd arcanum-osx-arm64
./arcanum setup
```

**Windows — x64 or arm64.** Windows releases are unsigned, so SmartScreen may warn. This example uses x64; choose the arm64 archive on an arm64 machine.

```powershell
Expand-Archive .\arcanum-win-x64.zip -DestinationPath .
.\arcanum-win-x64\arcanum.exe setup
```

Windows needs the **Microsoft Visual C++ Redistributable** for its architecture: [x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) · [arm64](https://aka.ms/vs/17/release/vc_redist.arm64.exe). Without it, the encrypted store's native library cannot load.

Run Arcanum as a normal user; elevation is not required. Compendium and The Forge are separate desktop downloads in the same release.

## Quickstart

Run `arcanum setup` to select a provider, model, credentials, workspace, and preset. The wizard validates the provider and shows the complete configuration plan before you accept it. Cancelling before acceptance leaves your existing configuration and credentials unchanged.

With the executable on your `PATH`:

```bash
arcanum setup
arcanum run "Hello"
arcanum center
```

Interactive `run` and Command Center can start the local host. To keep it running explicitly, start `arcanum serve` in one terminal, then use the clients in another:

```bash
arcanum run "Explain this code" --with @src/example.cs
arcanum memory status
arcanum memory search "project conventions"
arcanum context inspect --no-retrieval
arcanum doctor
```

Replace the example file with one of your own. For automation, commands support `--json`; the [Command Reference](docs/Arcanum.Command.Reference.md) covers output, context selection, and exit codes.

## One engine, many ways to work

| Interface | Use it for |
|---|---|
| **Command Center** — `arcanum center` | Interactive terminal conversations, Session navigation, attachments, and live progress |
| **CLI** — `arcanum run`, `session`, `memory`, and more | One-shot prompts, scripts, inspection, and administration |
| **The Forge** — `arcanum open theforge` | A desktop inference IDE for Sessions, Campaigns, Spells, and Apprentices |
| **Compendium** — `arcanum open compendium` | Editing local configuration, providers, feature opt-ins, and presets |
| **HTTP API** — `arcanum serve` | Native Arcanum clients and existing OpenAI-compatible Chat Completions clients |

Server-backed operations share the same API. Compendium also edits local configuration directly; setup and maintenance commands can operate on the installation without a running host.

### One executable, no runtime

The CLI and HTTP host ship as one self-contained Native AOT executable. The application is built with .NET 10, ASP.NET Core, and an encrypted SQLCipher store. The desktop clients use Avalonia. See the [engineering guide](docs/Arcanum.Engineering.md) for the architecture, platform prerequisites, and build instructions.

### Your provider, your choice

Configure OpenAI-compatible endpoints for hosted services or local inference, including Ollama's `/v1` API. Arcanum exposes a Chat Completions compatibility subset for existing clients; moderation, image-generation, and audio APIs are not implemented.

Optional **Familiar** transports use your separately installed and authenticated Claude Code or Codex CLI for text completions. Arcanum does not install or sign in to those tools, and its own tool loop runs through OpenAI-compatible providers rather than the CLIs' agent runtimes.

## Memory you can inspect and govern

Arcanum's memory is more than conversation history. Each part has a specific job:

| Capability | What it gives you |
|---|---|
| **Saga and Lexicon** | Extracted observations and structured facts that persist across conversations |
| **Campaign-scoped memory** | An opt-in retrieval policy that keeps another project's memories out of the current Campaign |
| **Campaign rollups** | Opt-in summaries that carry continuity between Sessions without replacing each Session's own history |
| **The Annals** | Claim history recording who asserted something, when it applied, and when Arcanum came to hold it |
| **The Long Rest** | Opt-in consolidation, supersession, and evidence-based ranking over Saga memory |
| **The Covenant** | Opt-in standing context with separate operator-confirmed and assistant-proposed lanes |

Annals capture defaults on for ordinary Saga and Lexicon writes. Saga extraction, Campaign-scoped retrieval, Campaign rollups, Long Rest, and Covenant turn participation are separate opt-ins. Enabling a memory feature does not grant permission to bypass a source's authority or sensitivity rules.

### Correct, review, retire, or erase

The `arcanum memory` commands show what is stored, where it came from, and whether it is eligible for retrieval. You can correct an exact memory, review new claims, pin material against automatic pruning, or retire it while keeping it inspectable. Selective erasure removes the targeted local memory and its covered derivatives, with content-free suppression evidence to prevent ordinary extraction or restore from resurrecting it.

Long Rest separates discovering similar memories from deciding they are equivalent. Consolidation retains source history, and repeated retrieval alone earns no positive usefulness credit. The [Proving Grounds API](docs/Arcanum.API.md#821-the-proving-grounds) can run paired evaluations with and without the same selected memory to report benefit, harm, no measurable effect, or insufficient evidence.

### OATH — Origin-Bound, Authority-Conserving Transactional History

**Memory cannot outrank its origin.** An operator-confirmed instruction, a model inference, and a retrieved quotation may contain the same words; they carry different authority.

OATH binds memory to its source, scope, sensitivity, revisions, and evidence. Summarizing or repeatedly retrieving a claim cannot turn it into an instruction or grant it tool rights. The Covenant keeps confirmed context separate from proposed data, and a protected turn's assistant result and staged memory mutations publish together or neither does. Disclosure receipts record protected content sent outside Arcanum; they do not promise that it can be recalled.

Read the [plain-language OATH guide](docs/ArcanumOATH.Human.md) or the [architecture companion](docs/Arcanum.OATH.md) for the full model.

## Find the right context

**Workspace, Session, and attachment retrieval** combine full-text search with semantic search over locally stored embeddings. Managed cosine search preserves exact results; compact quantized representations can reduce full-vector reads without replacing the authoritative embeddings or turning retrieval into approximate search. Performance depends on the corpus and query.

**The Tapestry** adds opt-in recursive RAPTOR summaries over eligible workspace, text-attachment, and Session material. It builds multiple levels of context for questions that span a collection. Those summaries retain source lineage and remain derived evidence.

**Attachments** are encrypted, versioned Session snapshots. You can add, reference, refresh, pin, inspect, and export them; retrieval follows the owning Session and exact attachment version. Text and source files can also be supplied directly with `run --with`.

**Comprehension is experimental and opt-in.** It extracts bounded text from modern Office, OpenDocument, Visio, RTF, and text-layer PDF attachments. PDF extraction runs in a sandboxed worker and can include text a reader would not see; these parsers are not document renderers. OCR, scanned PDFs without text, and legacy binary Office formats are unsupported. Both Comprehension and attachment retrieval must be enabled. Upload documents with `arcanum attachment add`, then pass their ID to `arcanum run --attachment`; `--with` does not extract them. The [configuration reference](docs/Compendium.README.md#feature-opt-ins) owns the exact format and eligibility limits.

## Tools, workflows, and follow-through

Arcanum's agent loop can make repeated model and tool calls, correct errors, and continue while it makes progress. It stops for completion, cancellation, explicit budgets, provider/context boundaries, safety or integrity refusals, or repeated lack of progress.

- **Spells and Prompts** make instructions and workflows reusable.
- **MCP tools** connect inference to permitted workspace and service actions.
- **Web research** combines search, browsing, and a server-owned research workflow when enabled.
- **Apprentices and delegated tasks** support bounded work; native child tasks are model-only, with no ambient parent history or child tools.
- **Daemons and long-running operations** support scheduled work and handler-specific durable recovery.
- **Trials** evaluate model behavior using regex, JSON-schema, or semantic checks.

Tool execution follows workspace containment, explicit authority, Sanctum policy, and the available platform sandbox. Ward records provide an informational audit trail. Process tools have platform requirements; `workspace_check` is currently available only on eligible macOS hosts.

## Local storage and data control

Sessions, attachments, and uploads are stored encrypted on your machine. API keys and provider credentials use operating-system secure storage with owner-only encrypted fallback files; provider credentials can also reference environment variables. The database key is held in an owner-only file. Protect the machine and its backups with full-disk encryption: local storage encryption does not protect against someone who can read both the data and its keys.

Authenticated encrypted backups, selective restore, retention policies, encrypted-blob key rotation, and installation reset give you control over the data lifecycle. Local erasure cannot revoke copies already disclosed to a provider or another external destination. Inference, embedding, and enabled background features may send admitted content to the providers you configure.

Ordinary background work stays on the queue-free fast path. A destructive Grimoire transition closes host-wide admission and drains work already admitted before it takes exclusive control. If the process stops, startup resumes only from authenticated durable evidence; evidence it cannot verify keeps the Grimoire closed instead of guessing.

## Coming soon

The [Arcanum Feature Tracker](https://github.com/orgs/Retro-Downfall/projects/1) describes the planned capabilities:

- **The Guild:** coordinated local and remote agents with durable Quests, scoped authority, shared work, budgets, and cancellation.
- **An organized installation:** one central Arcanum directory, configuration split by domain, and clear homes for data, authored assets, logs, caches, backups, and recovery files.
- **Stop and steer active work:** send an ordinary message while Arcanum is working and have it steer the same task at the earliest safe boundary, with visible stop controls.
- **Desktop memory management:** inspect, explain, correct, and review Saga, Lexicon, and Covenant memories directly in The Forge.
- **More document understanding:** additional document formats, OCR for scanned pages and images, and document-aware Tapestry summaries.
- **The Crucible:** independent evidence for verified task completion and admission of learned Spells, so a model cannot certify its own success.

## Documentation

| Document | What it covers |
|---|---|
| [Human architecture guide](docs/Arcanum.Design.Human.md) | A guided tour and navigation through the system |
| [Engineering guide](docs/Arcanum.Engineering.md) | Contributor orientation, platform details, build, and verification |
| [Architecture reference](docs/Arcanum.DESIGN.md) | Authoritative architecture, persistence, runtime behavior, and testing |
| [HTTP API](docs/Arcanum.API.md) | Exact native and compatibility contracts |
| [Command Reference](docs/Arcanum.Command.Reference.md) | Complete CLI syntax, options, output, and exit behavior |
| [Chat loop](docs/Arcanum.CHAT-LOOP.md) | Model/tool rounds, attachments, and context handling |
| [OATH architecture](docs/Arcanum.OATH.md) · [OATH guide](docs/ArcanumOATH.Human.md) | Governed, origin-bound memory |
| [Configuration](docs/Compendium.README.md) | Providers, features, presets, and every public setting |
| [Debugging guide](docs/Arcanum.DEBUGGING.Human.md) | Tracing and diagnosing the running system |

Arcanum is in beta; API and CLI surfaces can change. The [current operator limitations](docs/Arcanum.Engineering.md#current-operator-limitations) describe the practical boundaries of the implemented features.

## Contributing

Contributions are welcome. Start with the [engineering guide](docs/Arcanum.Engineering.md) for repository conventions and verification requirements.

## License

Arcanum is released under the [MIT License](LICENSE).

Copyright (c) 2026 Retro Downfall

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
