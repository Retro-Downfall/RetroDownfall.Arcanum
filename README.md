# Arcanum

**A local-first AI assistant and inference hub for .NET.** One Native AOT executable, an
OpenAI-compatible API, and an encrypted local store — no Python runtime, no cloud account
required, nothing leaving the machine that you did not send.

[![CI](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/actions/workflows/ci.yml/badge.svg)](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Retro-Downfall/RetroDownfall.Arcanum?include_prereleases&sort=semver&label=release)](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/releases)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-yes-2ea44f)](#why-arcanum)
[![Platforms](https://img.shields.io/badge/platforms-macOS%20arm64%20%C2%B7%20Windows%20x64%2Farm64-blue)](https://github.com/Retro-Downfall/RetroDownfall.Arcanum/releases)

Local AI tooling is almost entirely Python and TypeScript. Arcanum is the .NET one. The `arcanum`
executable runs as a long-lived HTTP host (`arcanum serve`) or as thin terminal clients against
that same API, exposes an **OpenAI Chat Completions compatibility subset**, routes inference across
any OpenAI-compatible provider — including Ollama and other local model servers through their `/v1`
endpoint — and can also call the **Claude Code and Codex CLIs you already have installed** as
inference providers. Everything it remembers lives in a SQLCipher-encrypted store on your disk.

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

**Linux** — no prebuilt binary in this release yet; build from source:

```bash
git clone https://github.com/Retro-Downfall/RetroDownfall.Arcanum.git
cd RetroDownfall.Arcanum
dotnet build RetroDownfall.Arcanum.slnx
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

From there, `arcanum open compendium` gives you every remaining setting in a real UI — see
[Configure it with Compendium](#configure-it-with-compendium).

For a fully local setup, give the wizard `http://localhost:11434/v1` as the endpoint and whichever
model Ollama has pulled. Credentials go to the OS credential manager — Keychain, Windows Credential
Manager, or Secret Service — and secrets are only ever accepted on stdin or as an environment
reference, never as a command-line argument.

## Configure it with Compendium

**You never have to write `arcanum.json` by hand.** Compendium is Arcanum's desktop configuration
editor — a .NET 10 Avalonia app that ships alongside the CLI and edits the same file with typed
controls instead of a text editor.

```bash
arcanum open compendium        # or: arcanum config open
```

It also opens from The Forge under **View → Open Compendium**, from the setup wizard, and from the
**Settings…** item in the macOS application menu. Extract the `compendium-*` archive beside the
`arcanum` one and the CLI will find it; if it can't, it prints every location it tried.

**Presets do the first pass for you.** Rather than starting from an empty file, pick one of five
workflow presets and Compendium applies a versioned overlay:

| Preset | For |
|---|---|
| **General Assistant** | Everyday use — attachments on, conservative memory settings |
| **Coding Workspace** | Working in a repository — workspace checks and file-write permission |
| **Research** | Web browsing on; needs a research credential |
| **Private/Offline** | Loopback-only binding, browsing off, telemetry off |
| **Automation** | Unattended runs; needs an operator-authored daily budget |

Selecting a card shows you what it would do before it does anything: exactly which settings the
preset owns, which prerequisites are unmet, what it deliberately leaves alone, and whether your
current configuration is **Active**, **Custom**, or **Drifted** from it. **Advanced/Custom** owns
nothing and changes nothing, for when you want to drive every value yourself.

**It will not let you save a broken configuration.** Validation runs as you type, errors render
inline under the control that caused them, and Save stays unavailable while any field is invalid —
so a bad value is caught in the editor rather than at the next startup. Saves are atomic: a
temporary write, a durable flush, then a replacement of `arcanum.json`, all inside the same
cross-process transaction the CLI and preset writers use.

Compendium edits configuration and nothing else — it does not run inference, open the Grimoire,
execute tools, or touch your encryption keys. Those stay with `arcanum data encryption ...` on
purpose.

Prefer the terminal? `arcanum config path`, `show`, `get <key>`, `set <key>`, `validate`, and `edit`
reach the same file, with endpoint values redacted on read and secrets accepted only on stdin.
[`Compendium.README.md`](docs/Compendium.README.md#complete-configuration-reference) is the complete
key-by-key reference.

## Why Arcanum

- **One binary, no runtime to install.** Native AOT is a hard constraint on every line of code in
  this repository, not an aspiration. The CLI and host ship as a single self-contained executable.
- **Local-first, and encrypted at rest.** State lives in a SQLCipher-encrypted store you hold the
  key to. There is a real backup, key-rotation, and recovery story, and a real deletion story.
- **Your API, not a bespoke one.** An OpenAI Chat Completions compatibility subset means existing
  clients and SDKs work against `arcanum serve` unchanged.
- **Bring the providers you already pay for.** Any OpenAI-compatible HTTP endpoint, plus opt-in
  routing through the Claude Code and Codex CLIs already installed on your machine.
- **Built for agents that finish.** The default posture is an unrestricted coding harness: work
  continues until the task completes or you cancel it, rather than stopping because a turn count or
  duration estimate was exceeded. Authentication, containment, SSRF defenses, and explicit operator
  budgets stay authoritative.

## What's in the box

| | |
|---|---|
| **`arcanum`** | Native AOT CLI and HTTP host — `serve`, `run`, `watch`, `session`, `memory`, `spell`, `model`, and more |
| **Compendium** | Avalonia desktop editor for the complete configuration surface — [see above](#configure-it-with-compendium) |
| **The Forge** | Avalonia desktop companion app |

Arcanum names its domain concepts after a D&D metaphor — a Campaign is a persistent workspace, a
Spell is a versioned skill, the Grimoire is the encrypted store. Every one of them is mapped to its
plain meaning and its API route in
[Naming metaphor](docs/Arcanum.Engineering.md#naming-metaphor).

## Documentation

| Document | What it is |
|---|---|
| [`Arcanum.Design.Human.md`](docs/Arcanum.Design.Human.md) | **Start here** — the human-readable navigation companion |
| [`Arcanum.Engineering.md`](docs/Arcanum.Engineering.md) | Contributor and agent orientation: standards, repository map, configuration, distribution, build and verification |
| [`Arcanum.DESIGN.md`](docs/Arcanum.DESIGN.md) | Authoritative: architecture, persistence, runtime behavior, packaging, testing |
| [`Arcanum.API.md`](docs/Arcanum.API.md) | Exact HTTP contracts |
| [`Arcanum.Command.Reference.md`](docs/Arcanum.Command.Reference.md) | Complete CLI syntax, options, aliases, exit behavior |
| [`Compendium.README.md`](docs/Compendium.README.md#complete-configuration-reference) | The only complete configuration reference |
| [`Arcanum.DEBUGGING.Human.md`](docs/Arcanum.DEBUGGING.Human.md) | Debugging guide |

## Status

`0.1.0-beta` — see [`Directory.Build.props`](Directory.Build.props). The API and CLI surfaces are
still moving.
[Current operator limitations](docs/Arcanum.Engineering.md#current-operator-limitations) is an
honest list of what does not work yet, and it is worth reading before you rely on anything here.

**Stack:** .NET 10 · ASP.NET Core Minimal API · Native AOT on Windows/Linux · `Microsoft.Extensions.AI`
· EF Core 10 + hermetic SQLCipher 4.17.0 · Avalonia

## Contributing

Issues and pull requests are welcome.
[`Arcanum.Engineering.md`](docs/Arcanum.Engineering.md) is the orientation document for working on
the code, and [Build, test & verify](docs/Arcanum.Engineering.md#build-test--verify) has the
commands a change has to pass before it can land.

[Retro Downfall](https://retrodownfall.com) will pay for your subscription to this repository if you
apply for collaborator privileges — and trustworthy contributors earn more privileges and benefits
over time.

## License

**No license has been chosen yet**, which means default copyright applies and you do not currently
have permission to use, copy, modify, or redistribute this code. If you want to build on Arcanum,
open an issue and say so — settling this is on the near-term list.
