# Arcanum

**A local-first AI assistant and inference hub for .NET.** One self-contained executable, an OpenAI-compatible API, and an encrypted local store — no Python runtime, no cloud account, nothing leaving the machine that you didn't send.

[![CI](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/actions/workflows/ci.yml/badge.svg)](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Retro-Downfall/RetroDownfall.Arcanum?include_prereleases&sort=semver&label=release)](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/releases)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-yes-2ea44f)](#functions)
[![Platforms](https://img.shields.io/badge/platforms-macOS%20arm64%20%C2%B7%20Windows%20x64%2Farm64-blue)](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/releases)

Arcanum runs a long-lived HTTP host (`arcanum serve`) with a set of thin CLI clients over that same API. It exposes an **OpenAI-compatible API** — so existing OpenAI clients and scripts talk to Arcanum unchanged — and routes inference across **any OpenAI-compatible provider**: hosted services, your own local Ollama or other model server, or the `Claude Code` and `Codex` CLIs you already have installed and signed in. Everything it learns lives in a **SQLCipher-encrypted** store on your disk, and the executable runs on **macOS and Windows**.

This page is a tour of what Arcanum does and how it differs. The authoritative architecture, HTTP contracts, CLI reference, and configuration reference are linked below; this one assumes you're comfortable with the usual terms.

## Functions and how Arcanum differs

### One executable, no runtime
Arcanum is a single self-contained executable built for **Native AOT** — so there's no .NET runtime, no Python interpreter, and no Node.js to install. It runs on macOS and Windows, starts fast, and behaves the same on every machine it's installed on.

### OpenAI-compatible, not OpenAI-dependent
Arcanum speaks the same interface as most popular AI products. That cuts both ways: it can present itself to existing OpenAI clients, and it can route inference to any OpenAI-compatible provider. Point it at a hosted API, a model server you run yourself, or the `Claude Code` / `Codex` CLIs you already have authenticated — Arcanum installs, updates, or signs in to nothing, and your subscription identity and credentials stay yours. Only text completions are routed through those CLIs; Arcanum's tool loop runs over OpenAI-compatible providers, not through the CLIs' own runtime.

### A progress-driven agent loop
Unlike a chatbot that stops after a single exchange, Arcanum keeps working until the task is done or you cancel it. The loop can make repeated model calls and tool rounds, correct its own errors, and follow through on sub-tasks. It stops only when it reaches a final answer, when the target is met, when you cancel, or when you've hit a spending or usage budget you set yourself. Per-call, per-frame, and concurrency limits are local safety rails, not a hard ceiling on a completed task.

Within that loop, an instruction becomes a **Spell** — a named, repeatable workflow — which can read from stored **Prompts**, stand on a **Campaign** (a persistent authority scope for one body of work), and delegate **Apprentices**, the sub-agents Arcanum spins up for parallel or deep work. Work that can't finish in a single reply becomes a **Daemon** or a **long-running operation**, which Arcanum can schedule, resume, and inspect while it runs. Standing tool authority and guardrails are declared as **Wards**; the local model playground for side-by-side comparisons is a **Trial**.

### A governed memory system
Arcanum remembers things about you and your projects across conversations. Its design follows one rule: **a memory may become shorter, simpler, or more useful — but it can never quietly become more authoritative.** What you told it is not treated the same as what the model inferred, which is not the same as a search result. Each subsystem has a role:

| What it is | What it does |
|------------|--------------|
| **Grimoire** | The encrypted local store that holds conversations, files, history, and notes |
| **Session history** | An ordered record of each conversation, including which files or links were shared |
| **Workspace search (the Eye of the World)** | Indexes a folder on your computer, so Arcanum can retrieve anything — even hundreds of files at once |
| **Saga** | Auto-extracts recurring topics and lessons from past conversations |
| **Lexicon** | Durable, structured facts — yours or the assistant's |
| **Covenant** | A standing, user-authored "rulebook" (below) |

These subsystems are stitched together by **OATH**, Arcanum's governing memory architecture. See [OATH: a memory that cannot outrank its origin](#oath--origin-bound-authority-conserving-transactional-history) below.

#### The Tapestry memory tree — built on **RAPTOR**
Arcanum uses a **RAPTOR**-style hierarchical structure (The Tapestry) to reason about whole bodies of material at once. Documents, notes, and attachments are grouped into clusters by meaning, summarized by a model, then those summaries are themselves clustered and summarized again, layer by layer. That lets Arcanum answer big-picture, multi-part questions from an entire corpus instead of from scattered fragments. As with everything else here, the summaries are treated as derived data, never authoritative — an original document can always be verified.

#### Locating context — RAG and full-text search
Two search mechanisms back the memory system:

- **Full-text search (FTS5):** a fast, exact-text index over everything Arcanum knows — best when you want a specific term or passage.
- **Retrieval-Augmented Generation (RAG):** Arcanum imprints text as vectors and searches your files, saved notes, and past conversations before answering, so replies are grounded in your information rather than a general guess.

#### The Covenant: a rulebook you control
Beyond ordinary memory, Arcanum can hold a **Covenant** — personal or project rules the assistant follows. Anything the assistant suggests is surfaced to you before it's trusted, and suggestions can never become rules on their own. Enabling the Covenant may mean those memories travel to where the assistant talks to the model — and Arcanum can't un-send them — so its protection ships by default and turning it on is a deliberate, protected choice.

### OATH — Origin-Bound, Authority-Conserving Transactional History
OATH is the model that ties the memory subsystems together. Its one hard rule: **memory cannot outrank its origin.**

Consider three identical sentences:

- You said, "Production billing uses PostgreSQL."
- The assistant inferred, "Production billing probably uses PostgreSQL."
- A search result surfaces text containing, "Production billing uses PostgreSQL."

The words are the same, but their authority is not. An ordinary assistant can remember a great deal without knowing what any one memory is allowed to mean. OATH does.

**What OATH is.** A durable memory travels with its papers: where it came from, who authorized it, where it may apply, which version is current, how sensitive it is, and what later summaries or indexes derive from it. A memory may become shorter, simpler, or more useful — but it can never quietly become more powerful. What you told it is not the same as what the model inferred, which is not the same as a search hit. The four load-bearing properties spell the acronym:

| Property | What it means |
|----------|---------------|
| **Origin-Bound** | Every retained claim or derivative binds its immutable source — identity, revision, hash, scope, and the receipt that produced it. Deleting a source changes its availability; it does not make a surviving derivative look self-authored. |
| **Authority-Conserving** | Summarizing, extracting, ranking, or repeating can narrow a memory's authority but never widen it: a proposed guess cannot become an operator instruction, a campaign fact cannot leak to everyone, sensitivity cannot drop, and lineage cannot be dropped. Only a new authenticated action creates a more powerful claim. |
| **Transactional** | Local bookkeeping uses append-only revisions, guarded current pointers, and idempotency receipts. The rule that matters for a turn: **the assistant's answer and its staged memory publish together, or neither does.** |
| **History** | Corrections are new revisions, retirements are tombstones, receipts distinguish a replay from a different request. Current state is a view over immutable history, not one mutable truth slot. |

**How OATH is implemented.** OATH is a cross-cutting rule set, not a single feature or table. It governs the boundary *between* the memory subsystems, each of which keeps its own role:

- **The Grimoire** is the encrypted, transactional substrate that stores canonical and derived records — it does not itself decide a record's authority.
- **The Covenant** supplies OATH's governing claim and authority substrate: immutable versions, operator-authorized **Confirmed** claims and agent-suggested **Proposed** claims, policy, evidence, and the publication rules.
- **The Lexicon**, **Saga**, **Tapestry**, **Weave**, and **Divination**, plus **Session history**, each serve a different memory role.

OATH's central test is simple: **retrieval can discover a candidate, but it cannot promote its authority.** To keep that true, OATH separates five decisions that simpler systems merge — *retention* (does it exist?), *discovery* (can it be found?), *eligibility* (does policy permit it here?), *admission* (does it fit this exact provider request?), and *authority* (what is it allowed to mean?). Retrieval only answers "what might be relevant."

In practice OATH shows up in a few places:

- **Two lanes.** The Covenant keeps an operator lane (**Confirmed**, rendered as structured `CONTEXT`) and a separate agent lane (**Proposed**, rendered as fenced `DATA`). Proposed material can never become context, and it cannot grant tool permission.
- **Atomic publication.** A turn's assistant result and its staged memory mutations commit in one transaction, so a failed or cancelled answer never leaves a plausible-looking memory behind.
- **Fenced, not promoted.** Under context pressure, Proposed material is the first thing removed — admitted through a deterministic prefix so the same inputs always reach the same decision.
- **Acknowledged disclosure.** Before protected material leaves Arcanum for a provider or external tool, the system durably records the disclosure — a receipt, not a promise that it can be recalled.
- **Failure closed.** Missing or malformed origin evidence yields refusal, quarantine, or erasure — never a guess.

OATH is bitemporal-*ready*: immutable history, revisions, timestamps, generations, and source versions already exist. Full valid-time reasoning (exactly-when-something-was-true semantics) is planned work, not a claim about today's implementation.

### Encrypted by default
Everything is stored **encrypted at rest on your own disk**. API keys and other secrets use your operating system's secure storage (Keychain, Windows Credential Manager, Secret Service) rather than a plain-text config file. Tools that touch the filesystem run inside a sandbox. Anything you read or write stays local — nothing goes to disk or the network beyond what you ask.

### Full control over your data
- **Backups** — a single, mobile, full-encrypted copy; no need to disrupt your work to take it.
- **Retention** — decide how long to keep data before it's eligible for cleanup.
- **Key rotation** — change your encryption passphrase while keeping everything intact.
- **Full-erasure** — wipe your data when you want it gone.

### Background work
Beyond a single reply, Arcanum can keep going on its own: schedule **daemons**, resume **long-running operations**, watch files, and execute multi-step work that isn't one-shot conversation.

### See what's happening
- **`arcanum context inspect`** — predict where the attention budget goes and which tools would run, before anything runs.
- **`arcanum doctor`** — health checks with suggested fixes, applied only with your confirmation.
- **`arcanum look`** — an instant snapshot of the directory currently in view.

### One engine, many clients
The same engine powers the terminal program (`arcanum run`, `serve`, `session`, `memory`, …), the interactive terminal workspace (`arcanum center`, the Command Center), the configuration editor (**Compendium**), and the inference IDE (**The Forge**). Every client just asks the engine to work — nothing reaches in and changes how the engine works internally. Arcanum's tool loop also runs over **MCP tools** (read, edit, and search files; run commands; extract and curate memory) that follow the same workspace sandbox and Sanctum security policy.

### Setup presets
Rather than start from nothing, pick one of six pre-made setups per workflow. Each shows what it changes, what might be needed to enable it, and what it leaves alone — so what happens is always clear.

---

## Install

Grab a build from the [latest release](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/releases).

**macOS (Apple Silicon)** — signed with a Developer ID certificate and notarized.

```bash
unzip arcanum-osx-arm64.zip && cd arcanum-osx-arm64
./arcanum setup
```

**Windows (x64 or arm64)** — unsigned, so SmartScreen may warn.

```powershell
Expand-Archive .\arcanum-win-x64.zip -DestinationPath .
.\arcanum-win-x64\arcanum.exe setup
```

Verify any download against `SHA256SUMS.txt` from the same release. Run as a normal user —
elevation is never required.

## Quickstart

`arcanum setup` is a guided wizard that walks eight steps and **writes nothing until you accept the
plan**, so Ctrl+C at any point leaves your machine exactly as it was. Point it at a local model
server and you never touch a hosted provider:

```bash
arcanum setup                  # endpoint, model, credential, workspace, preset — then a diff to accept
arcanum run "Hello"            # one-shot prompt
arcanum serve                  # long-lived host; thin clients talk to it over the same API
arcanum key list               # what credentials Arcanum holds (presence and status only, never values)
```

## Documentation

| Document | What it is |
|---|---|
| [`Arcanum.Design.Human.md`](docs/Arcanum.Design.Human.md) | **Start here** — the human-friendly navigation companion |
| [`Arcanum.Engineering.md`](docs/Arcanum.Engineering.md) | Contributor and agent orientation: standards, repository map, configuration, distribution, build and verification |
| [`Arcanum.DESIGN.md`](docs/Arcanum.DESIGN.md) | Authoritative: architecture, persistence, runtime behavior, packaging, testing |
| [`Arcanum.API.md`](docs/Arcanum.API.md) | Exact HTTP contracts |
| [`Arcanum.Command.Reference.md`](docs/Arcanum.Command.Reference.md) | Complete CLI syntax, options, aliases, exit behavior |
| [`Arcanum.CHAT-LOOP.md`](docs/Arcanum.CHAT-LOOP.md) | The chat loop, attachments, context ledger, and memory promotion |
| [`Arcanum.OATH.md`](docs/Arcanum.OATH.md) | The formal architecture for governed, origin-bound memory |
| [`Arcanum.OATH.Human.md`](docs/Arcanum.OATH.Human.md) | The human-friendly guide to the same architecture |
| [`Compendium.README.md`](docs/Compendium.README.md) | The complete configuration reference |
| [`Arcanum.DEBUGGING.Human.md`](docs/Arcanum.DEBUGGING.Human.md) | Debugging guide |

## Status

`0.1.0-beta` — see [`Directory.Build.props`](Directory.Build.props). The API and CLI surfaces are still moving.

**Current operator limitations** — an honest list of what does not work yet, in [`Arcanum.Engineering.md`](docs/Arcanum.Engineering.md#current-operator-limitations). Worth reading before you rely on anything here.

**Stack:** .NET 10 · ASP.NET Core Minimal API · Native AOT on macOS and Windows · `Microsoft.Extensions.AI` · EF Core 10 + hermetic SQLCipher 4.17.0 · Avalonia · MCP client (stdio + HTTP)

## Contributing

Issues and pull requests are welcome. [`Arcanum.Engineering.md`](docs/Arcanum.Engineering.md) is the orientation document for working on the code.

## License

Arcanum is released under the MIT License. See the [`LICENSE`](LICENSE) file for the full text. In short, you have permission to use, copy, modify, and redistribute the software, provided you keep the original copyright and license notice with any copies.

Copyright (c) 2026 Retro Downfall

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
