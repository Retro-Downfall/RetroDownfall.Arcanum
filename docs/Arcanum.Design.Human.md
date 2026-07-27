# Arcanum Design Reading Guide

This is the non-authoritative, human-readable companion to Arcanum's technical design.

The repository's complete documentation contract is:

- [`Arcanum.DESIGN.md`](Arcanum.DESIGN.md) — the single source of truth for architecture, APIs,
  persistence, runtime behavior, The Forge, packaging, and testing;
- [`Compendium.README.md`](Compendium.README.md#complete-configuration-reference) — the only complete
  `arcanum.json` key/default/bounds and credential-reference listing;
- [`Arcanum.README.md`](Arcanum.README.md) — concise agent/operator orientation and runnable
  commands; and
- [`Arcanum.Design.Human.md`](Arcanum.Design.Human.md) — this reading guide.

Do not copy technical contracts or configuration tables into this guide. Update the owning
canonical document and keep this navigation in sync.

## Suggested reading paths

### Extending the host or API

1. DESIGN §4 for project ownership and the endpoint inventory.
2. DESIGN §8 for JSON, NDJSON, SSE, and OpenAI wire rules.
3. DESIGN §9 for Native AOT and source-generation constraints.
4. DESIGN §11 for authentication, path/network policy, Wards, Sanctum, Sessions, and `/v1`.

### Changing inference behavior

1. DESIGN §10.1–§10.6 for model resolution, tools, routing, context, attachments, and the Lexicon.
2. DESIGN §10.7 for the exact shared Master turn lifecycle, buffered/streaming projections,
   fallback, correction, cost/context admission, cancellation, and terminal events.
3. DESIGN §22 for structured output, accounting, budgets, and prompt caching.

### Changing persistence

1. DESIGN §5.4 for the Grimoire inventory, raw-SQL/compiled-model boundary, install/reinstall policy,
   serialization, and crash consistency.
2. DESIGN §5.5.5 for Unseen Servant watermarks.
3. DESIGN §10.2.5 and §11.16 for attachment and Session lifecycle.
4. DESIGN §22.2 for inference accounting.

Incompatible local Grimoire schemas are recreated rather than data-migrated. Stop Arcanum, back up
anything needed, delete the database plus its WAL/SHM sidecars, and restart as directed by
DESIGN §5.4.5.

### Working on autonomous agents

Use DESIGN §5.7 for the canonical **Master/Apprentice** hierarchy, recovery loops, Chronicles,
Simulacrum, The Conclave, and A2A.

### Working on desktop applications

- DESIGN §19 owns The Forge Inference IDE architecture, wire quirks, authentication/settings, UI
  vocabulary, implemented surfaces, limitations, and packaging.
- `Compendium.README.md` owns the configuration editor and complete configuration contract.
- The Arcanum CLI/host is Native AOT on Windows/Linux and folder-based self-contained on macOS.
  The Forge and Compendium are self-contained Avalonia applications on .NET 10 and are not Native
  AOT.

### Testing

DESIGN §13 owns commands, coverage thresholds, CI behavior, fixtures, parallel collections,
SQLCipher/API-host safety, reasoning coverage, and reliable editing-loop test matrices.
