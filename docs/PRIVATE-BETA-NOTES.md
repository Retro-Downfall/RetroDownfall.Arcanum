# Private beta notes (Windows / Linux)

These notes ship with private-beta archives produced by:

- `scripts/packaging/linux/package-linux.sh`
- `scripts/packaging/windows/package-windows.ps1`
- `.github/workflows/private-beta-release.yml`

macOS Apple Silicon release remains on the separate signed/notarized path (`docs/RELEASE-MACOS.md`).

## What’s in the archive

| Artifact | Kind |
|----------|------|
| `arcanum` / `arcanum.exe` | Native AOT CLI/host |
| `the-forge-*` folder | Self-contained Avalonia desktop app (**not** Native AOT) |
| `compendium-*` folder | Self-contained Avalonia config editor (**not** Native AOT) |
| `SHA256SUMS` | Checksums for the compressed archives |

Windows/Linux private-beta builds are **unsigned by default**. Windows SmartScreen may warn on first launch of unsigned binaries; that is expected for this beta channel. Optional Authenticode signing is available only when packaging with `-Sign` and `WINDOWS_CERT_*` credentials.

## Linux quick start

1. Extract: `tar -xzf arcanum-linux-x64.tar.gz`
2. Ensure the binary is executable: `chmod +x arcanum-linux-x64/arcanum`
3. First run: `./arcanum serve` (generates the master API key; use `arcanum key show` to recover it)
4. Config / Grimoire live under the Arcanum data directory (typically `~/.local/share/arcanum` / `~/.config/arcanum` depending on platform helpers — see `arcanum doctor`)
5. Launch The Forge / Compendium from their extracted folders (run the published app host binary)

If Secret Service / libsecret is unavailable, The Forge may prompt for an API key or accept the process-only env override `THEFORGE_ARCANUM_KEY` (never written to `the-forge.json`).

## Windows quick start

1. Extract the `.zip` archives
2. Run `arcanum.exe serve` from an elevated-optional normal user shell
3. If SmartScreen blocks the unsigned binary, use “More info” → “Run anyway” for private-beta testing only
4. Recover the API key with `arcanum key show` or paste into The Forge when prompted
5. Launch The Forge / Compendium `.exe` from their extracted folders

## Tool-child sandbox (beta honesty)

- **macOS:** filesystem jail active via deprecated `/usr/bin/sandbox-exec`
- **Linux:** Landlock helper in-tree but **inactive** — command tools fail closed unless `Arcanum:Security:AllowUnsandboxedToolChildren=true`
- **Windows:** no filesystem jail (Job Objects only); Sanctum path-boundary may deny command tools
- Network isolation for tool children is **not provided**

Run `arcanum doctor` and inspect `GET /api/health` component `ToolChildSandbox`.

## RAG / The Weave

sqlite-vec is **not** shipped in this beta. When embeddings are enabled, search uses managed SIMD fallback (preview/performance-limited; 50,000 row scan budget). The Forge shows a non-blocking banner; `GET /api/meta` exposes typed `embeddingsVectorMode` (`disabled` | `managed` | `vec0` | `unavailable`).

## Local packaging

```bash
# On Linux host:
./scripts/packaging/linux/package-linux.sh --version 0.1.0-beta.1 --output-dir ./dist

# On Windows host (PowerShell):
.\scripts\packaging\windows\package-windows.ps1 -Version 0.1.0-beta.1 -OutputDir .\dist
```

Cross-OS artifacts: run `.github/workflows/private-beta-release.yml` (`workflow_dispatch`).
