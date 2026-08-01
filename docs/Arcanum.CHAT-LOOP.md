# Arcanum chat loop and attachment continuation ordering

This document is the focused companion to `Arcanum.DESIGN.md` §10.7. It describes the one shared
buffered/streaming model-tool loop and the ordering contract for attachment content.

## 6. One logical turn, multiple provider requests

A tool response cannot change the provider request that produced the tool call. “Refresh in the
current message” therefore means refresh during the same logical Arcanum turn and include the
content in that turn's next provider request. Every call still passes through `IModelCallExecutor`;
there is no second inference path.

```mermaid
sequenceDiagram
    participant Model
    participant Loop as Shared tool loop
    participant Tool as refresh_session_file
    participant Store as Attachment store
    Model->>Loop: tool call (attachmentId or logicalKey)
    Loop->>Tool: Ward then Sanctum then invoke
    Tool-->>Loop: selector acknowledgement
    Loop->>Store: secure source read and persist/reuse
    Store-->>Loop: version plus current bytes metadata
    Loop->>Loop: append assistant call and tool result
    Loop->>Loop: after all round results, append queued content
    Loop->>Model: next admitted request in same logical turn
```

## 7. Refresh security and persistence

`refresh_session_file` accepts exactly one attachment selector and never a filesystem path. The host
supplies session, turn-visible IDs, model, campaign, and assistant Entry. Logical keys match
case-insensitively but fail if that would select more than one case-distinct key.

The latest Bound version must carry verified workspace provenance. The resolver checks workspace
identity, lexical and canonical containment, unchanged symlink target, path/open-handle identity,
and Sanctum against the actual canonical path. Bytes come from the verified handle under the text
or image size cap. Two complete handle reads must have identical SHA-256 hashes. MIME, strict UTF-8
for text, Scrying allowlists, and model vision capability are reapplied.

An unchanged hash reuses the latest row and encrypted blob. Changed bytes create the next version
under the existing per-session/per-logical-key locks and `MaxBytesPerSession` /
`MaxVersionsPerLogicalKey` limits. The original user Entry is never changed; a new version may bind
to the current assistant Entry.

When semantic attachment retrieval is enabled, a newly created Bound refresh version is also
offered to the bounded background indexing queue. This happens after durable persistence and does
not change the tool result or injection ordering. Queue overflow or embedding failure is recovery
work only: it records/delays indexing and never fails the logical turn. Default retrieval on later
turns uses only the newest Bound version for each logical key in the same session; older indexed
versions remain available only through explicit historical search.

## 8. Transcript and injection order

For a model response containing several calls, Arcanum appends every assistant-call/tool-result pair
first. Successful `attach_session_file` and `refresh_session_file` materializations accumulate in a
pending list. Only after the loop has appended the last result does it add one User message carrying
the queued `TextContent` / `DataContent`.

```mermaid
flowchart TD
    A["Provider returns one or more tool calls"] --> B["Execute call 1"]
    B --> C["Append assistant call 1 and tool result 1"]
    C --> D["Execute remaining calls in order"]
    D --> E["Append every remaining call and result"]
    E --> F{"Any validated attachment content queued?"}
    F -- Yes --> G["Append one User message with all queued content"]
    F -- No --> H["Continue without attachment extras"]
    G --> I["Fresh context and cost admission"]
    H --> I
    I --> J["Next IModelCallExecutor request"]
```

The shared `MaxReferencesPerTurn` budget includes explicit references and both model tools. A logical
key/version is injected once per turn. Refreshed text is framed as untrusted DATA with an adaptive
fence and a hardened label containing filename, logical key, version, and source freshness. Images
pair an untrusted notice with `DataContent` and are never truncated.

Expected unsafe/unavailable cases return a structured tool result and no content. Unexpected
post-processing exceptions follow the shared mode policy: streaming invocation failures are
tolerated with `toolError` plus a synthetic result, while buffered behavior follows
`TolerateToolFailures`. A successful refresh emits native `attachmentRefreshed` observability after
its `toolResult`; OpenAI projections ignore that native-only event.
