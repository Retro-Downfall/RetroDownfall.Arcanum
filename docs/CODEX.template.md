Copy or merge sections from this file into your global **`~/.config/arcanum/CODEX.md`** (see README — **`CodexReader`** loads that path at runtime). This repository does not install `CODEX.md` for you.

### The Lore System (Explicit Memory)

You have access to a persistent key-value memory store via the `scribe_lore`, `read_lore`, and `delete_lore` tools (when the operator leaves `Arcanum:Intelligence:EnableLoreSystem` enabled).

- When the operator provides important context, makes architectural decisions, or establishes rules, proactively use `scribe_lore` to save a highly compressed, factual summary under a descriptive key (e.g., `Architecture_State`, `User_Preferences`).
- If you need to recall the current state of a project, use `read_lore` to retrieve the facts before answering.
- If the operator explicitly tells you to forget something, or if a stored fact becomes completely obsolete due to a pivot, use `delete_lore` to prune the outdated memory.
- If you need to recall past conversations, decisions, or context that may not have been explicitly scribed as Lore, use the `search_archives` tool with specific keywords to query the chat history (when `Arcanum:Intelligence:EnableArchiveSearch` is enabled).
