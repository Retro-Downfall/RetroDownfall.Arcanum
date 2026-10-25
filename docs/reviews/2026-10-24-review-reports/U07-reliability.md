# U07 · Arcanum.Cli · CommandCenter (2) · Reliability review

Tree: `claude/codebase-review-2026-10` at `ac0d2d8e` (read-only worktree `/Users/mat/Source/apps/RetroDownfall.Arcanum/.claude/worktrees/codebase-review`).

Unit membership: `units.json` was not present in the scratchpad, so the unit was reconstructed from `cs-lines.txt`: the `Cli/CommandCenter` directory is 44 files / 16,743 lines; the contiguous tail from `OverlayLayout.cs` to `ThinkingSpinner.cs` is exactly 4,773 lines (the stated unit size), and the first 30 files (11,970 lines) form CommandCenter (1).

## Coverage

Read fully (all 14 unit files, 4,773 lines):

- `src/RetroDownfall.Arcanum.Cli/CommandCenter/OverlayLayout.cs` (92)
- `.../PersistedToolInteraction.cs` (94)
- `.../SessionActionWindowState.cs` (54)
- `.../SessionAttachmentReveal.cs` (126)
- `.../SessionIdLineParser.cs` (53)
- `.../SessionLogBuffer.cs` (639)
- `.../SessionWorkspaceService.cs` (828)
- `.../ShellCommandDispatcher.cs` (1,634)
- `.../ShellCommandParser.cs` (356)
- `.../SlashCommandRegistry.cs` (311)
- `.../StreamingUiCoalescer.cs` (232)
- `.../TelemetryPane.cs` (74)
- `.../TerminalCellMetrics.cs` (211)
- `.../ThinkingSpinner.cs` (69)

Read for context (callers / contracts), not reviewed as the unit: `CommandCenterState.cs` (full), `CommandCenterSessionMutationGuard.cs` (full), `CommandCenterSubmitText.cs` (full), `CommandCenterHost.cs` (RunTerminalLoop, RunGatedAsync, DispatchSlashAsync, HandleSubmitAsync, shutdown/cleanup, submit wiring), `CommandCenterChatRunner.cs` (RunTurnAsync 40-460), `CommandCenterTurnAttachmentBuilder.cs` (TryStagePathForNextTurn, TryResolvePath), `CommandPaletteCatalog.cs` (slash-menu decision code), `CommandCenterWindow.GetSelectedTranscriptEntryId`, `CliSessionManager.SaveSessionIdAsync` + `SessionMutationRevalidator`; host side `SessionRepository` fork cutoff (505-575), `SessionAttachmentRequestValidator`, `SessionAttachmentTurnService` reference loading (115-170), `SessionAttachmentStore.ValidateReferencesAsync`, `TurnContextGuards.IsToolCallEntry/IsToolResultEntry`, `SessionEntryPersistence` tool entry builders, `WizardIntelligenceProvider` SessionBound emission. Docs: DESIGN §11.16.1 (fork), Command Reference "Command Center" (keys + slash commands), Engineering line 1188 (branching).

Existing tests for the area (`SessionWorkspaceServiceTests`, `CommandCenterTests`, `SessionLogBuffer*`, `SessionCrossGenerationWriterTests`, coalescer, cell metrics, parser, registry): 148 passed on the shared build.

## Probes run

One scratch worktree (`wt/U07-reliability`, removed afterwards), one probe file `tests/RetroDownfall.Arcanum.Tests/Cli/CommandCenter/U07ReliabilityProbeTests.cs` (source saved as `reports/A/U07-reliability-probes/U07ReliabilityProbeTests.cs.txt`; raw outputs `probe-run-1.txt`, `probe-run-2.txt` in the same folder). The probes drive the real `ShellCommandDispatcher` + `SessionWorkspaceService` + `ArcanumApiClient` over a fake `HttpMessageHandler` host, the real `SessionLogBuffer`, `StreamingUiCoalescer`, `ShellCommandParser`, and the host's real `SessionAttachmentRequestValidator`. 27 probe cases, all ran.

| Probe | What it shows |
|---|---|
| P1 | With a turn generating, `/resume` and `/clear` are denied, but `/branch parent`, `/fork` and `/session archive <active>` switch the binding and replace the log; the streaming assistant entry is orphaned and the chat runner's first reasoning insert throws `ArgumentException: The anchor entry does not belong to this log.` |
| P2 | `/fork alternative` sends `UpToEntryId` = the user prompt (inclusive cutoff), and queues that same prompt as `PendingAlternativePrompt`. |
| P3 | `/fork at <id>` and `/fork alternative` on a session with 10 attachments ask for `/fork confirm`; `/fork confirm` then POSTs `UpToEntryId=null` (whole session) and no alternative prompt. |
| P4 | A reference staged by `/attachments add` survives `/clear` and `/resume <other>`; the next PingRequest is refused by the host validator (`AttachmentReferences require a SessionId.` / not bound to this session). |
| P5, P5b | `WrapLine` boundary behaviour; frequency on realistic prose (0 affected rows at 80/100 columns: the defect is limited to rows that are one unbroken token). |
| P6 | Fork POST succeeds, reload of the branch fails: reported as `host busy`, no branch id; a retry creates a second branch (2 POSTs). |
| P7 | `/branch child` with no bound session resumes an unrelated root session. |
| P8 | `PersistedToolInteraction.IsToolInteraction` is true for `user` entries that start with `[ToolResult:` / `[toolcall:`. |
| P9 | `StreamingUiCoalescer.FlushCancelledAsync` after the UI channel is completed throws `ChannelClosedException`. |
| P10 | `/attach My  Notes.txt` parses to `My Notes.txt`; `/pin file docs/a  b.md` to `docs/a b.md`. |
| P11 | `/branch`, `/session`, `/ward` answer "Unknown command `/x`. Did you mean `/x`?" |

## Findings

### F1 · Important · A staged attachment reference outlives its session and then blocks every later turn
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs:593` (`StartNewSession`), also `:701` (`ApplyTranscriptPage`, used by resume and fork) and `ShellCommandDispatcher.cs:83` (`/clear`).
- Claim: `/attachments add <name>` stages a host attachment id that is valid only for the current session (`ShellCommandDispatcher.cs:450` → `CommandCenterState.StageAttachmentReference`). Nothing clears staged references when the binding changes: `ClearSessionBinding`, `StartNewSession`, `ApplySessionMeta`/`ApplyTranscriptPage` (resume, fork, branch, archive-current, Ctrl+N) leave `_stagedAttachmentReferences` intact. The next turn sends the stale id; the host refuses the turn (`SessionAttachmentRequestValidator.cs:41-44` "AttachmentReferences require a SessionId." for a new session; `SessionAttachmentStore.cs:1543-1546` / `SessionAttachmentTurnService.cs:144-152` "not a bound attachment for this session" otherwise). The chat runner only clears the staged snapshot after a successful result (`CommandCenterChatRunner.cs:435-437`), and no command unstages, so the stale id is resent on every subsequent message.
- Failure scenario: in session A run `/attachments add notes`, then `/clear` (or `/resume B`, `/fork`, Ctrl+N). Every message from then on fails with the host error until Command Center is restarted. A fork copies attachments under new ids, so even the fork of A refuses A's id.
- Evidence: P4 output: `after /clear: SessionId=null staged=66666666-…` and `host validator verdict for next turn: AttachmentReferences require a SessionId.`; `after /resume 1111…: staged=66666666-…`, validator proceeds to `ValidateReferencesAsync`, which throws "does not belong to session" (code above). Grep shows the only mutations of staged references are `StageAttachmentReference`, `SnapshotStaged`, `ClearStaged`.
- Fix: add `CommandCenterState.ClearStagedReferences()` and call it wherever the bound session changes (`ClearSessionBinding`, `ApplySessionMeta` when the id differs); report "Dropped N staged attachment reference(s) from the previous session." Optionally also clear the snapshot when the host rejects a reference so a single bad id cannot wedge the composer. Test: stage, `/clear`, assert staged references empty; stage, `/resume other`, assert empty. Effort S.

### F2 · Important · `/fork`, `/branch parent|child` and `/session archive <active>` switch sessions while a turn is generating
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:236` (SessionFork), `:313` (BranchParent), `:323` (BranchChild), `:1282` (archive of the active session); related `SessionLogBuffer.cs:146-150`, `CommandCenterChatRunner.cs:164`, `CommandCenterHost.cs:2246`.
- Claim: the documented rule ("A turn is still generating. Cancel it with Ctrl+C before switching sessions.") is applied to `/resume` and `/clear` (`ShellCommandDispatcher.cs:73`, `:221`) and to the keyboard actions in the host, but not to the other session-switching slash commands. Slash commands run under the action gate, which a chat turn does not hold (`HandleSubmitAsync` dispatches slash input while `TryBeginTurn` is held), so these commands run mid-turn. They replace the log and rebind the session under the running turn: the streaming answer goes into an entry no longer in the log (invisible), the turn's tool calls land in the new session's Incantations pane, and if the turn has not yet produced its first reasoning frame, `state.Log.InsertBefore(assistantLine, …)` throws `ArgumentException`, which the chat runner catches as a turn failure and abandons the stream (host marks the run Abandoned). `/session archive <active>` additionally archives the session the turn is writing to.
- Failure scenario: send a prompt to a reasoning model, then while it is still "Thinking" type `/branch parent` (or `/fork`). The view switches; the first reasoning event throws, the turn ends with "Error: The anchor entry does not belong to this log. (Parameter 'before')", and the answer is lost.
- Evidence: P1 output: `/resume … | SessionId=2222… anchorInLog=True footer='A turn is still generating…'`; `/branch parent | SessionId=1111… anchorInLog=False … firstReasoningInsert=> ArgumentException: The anchor entry does not belong to this log.`; same for `/fork` (forkPosts=1, SessionId=fork) and `/session archive 2222…` (SessionId=null, archiveCalls=1). Existing tests (`CommandCenterTests.cs:635-661`) cover only `/resume` and `/clear`.
- Fix: apply `TryDenySessionMutationWhileGenerating` to SessionFork, BranchParent, BranchChild and to SessionArchive when the target is `state.SessionId` (simplest: deny every session-mutating kind in one place before the switch). Make `InsertBefore` tolerate a missing anchor (append instead of throw) so a future gap cannot kill a turn. Add the three commands to the generating-deny tests. Effort S.

### F3 · Important · `/fork alternative` duplicates the user prompt in the new branch
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:268-269`; related `CommandCenterHost.cs:2276-2280`, `SessionRepository.cs:520-533`, DESIGN §11.16.1 line 3894.
- Claim: the cutoff is set to the preceding user prompt's entry id, and the host's `upToEntryId` is inclusive ("copies only entries up to and including that entry"; `maximumSourceEntrySequence = cutoffEntry.Sequence`). The branch therefore already ends with the prompt. The dispatcher then queues the same prompt text, and the host submits it as a new turn in the branch, which appends a second identical user entry before the regenerated answer. The model receives the question twice and the persisted branch shows it twice. Attachments bound to the original prompt entry are copied with it, but the resent prompt carries none.
- Failure scenario: select an assistant answer, `/fork alternative`. The branch transcript reads `Dungeon Master: Q` / `Dungeon Master: Q` / `Mage: <new answer>`; every further turn in that branch carries the duplicate.
- Evidence: P2 output: `fork request UpToEntryId=4444… (prompt entry=4444…)`, branch last entry `user/What is 2+2?`, `PendingAlternativePrompt='What is 2+2?'`. No test exercises `/fork alternative` (grep: only the dispatcher, parser, state and host reference it).
- Fix: cut off at the entry before the prompt (`promptIndex - 1`), loading the previous transcript page when the prompt is the first loaded entry and forking with no entries when the prompt is the session's first entry (needs a host contract for an empty or exclusive-cutoff fork); or keep the inclusive cutoff and ask the host to regenerate from the trailing prompt instead of resending it. Add a dispatcher test asserting the cutoff and a host test asserting one user entry before the regenerated answer. Effort M.

### F4 · Important · `/fork confirm` forks the whole session, discarding the cutoff or alternative that asked for confirmation
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandParser.cs:243-244`, `ShellCommandDispatcher.cs:290-299`, `SessionWorkspaceService.cs:486-494`.
- Claim: `/fork confirm` is parsed with no argument and no memory of the pending request, so it always sends `ForkSessionRequest(UpToEntryId: null)`. When `/fork at <id>`, `/fork at` (selected) or `/fork alternative` trips the "10 attachments or 10 MiB" confirmation, confirming creates a complete branch instead: no cutoff, every attachment copied, and no regenerate. The confirmation threshold is also measured over all source attachments, so a cutoff fork that would copy few or none of them is still made to confirm.
- Failure scenario: a session with 10 attachments; select an early entry, `/fork at`; the CLI asks for `/fork confirm`; the operator confirms and gets a full copy of the session (and all attachment bytes), not the branch at the selected entry.
- Evidence: P3 output for both first commands: `'This fork will copy 10 attachment(s) (0.0 MiB). Run `/fork confirm` to continue.'` then `/fork confirm -> POSTs=1 UpToEntryId=null (whole session) PendingAlternativePrompt=null`.
- Fix: keep the pending fork request (cutoff + alternative prompt + source id) in state when confirmation is required and replay it on `/fork confirm` (clear it on any session change); measure the threshold over the attachments the cutoff will actually copy (entry-bound rows at or before the cutoff; entry-less rows only for a full fork, matching `SessionRepository`). Effort S-M.

### F5 · Minor · A fork that the host created but the CLI could not reload is reported as a plain failure; retrying creates another branch
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs:505-524` (also `:543-554`).
- Claim: after `POST /fork` succeeds, a failure of any of the three reloads (detail, entries, attachments) or of the last-session save returns `Failed` with only the reload's error text. The branch exists on the host, the session list is not refreshed, and the message neither names the branch nor says it was created, so the natural retry forks again.
- Failure scenario: host briefly busy after the fork; operator sees "host busy", runs `/fork` again, and the session list later shows two identical "Fork of …" branches.
- Evidence: P6 output: `first /fork -> 'host busy'`, `second /fork -> 'host busy'`, `fork POSTs (branches created on host) = 2; message names fork id: False`.
- Fix: once the fork id is known, any later failure should say "Branch {forkId:D} was created but could not be opened: … Use `/resume {forkId:D}`." and refresh the session list. Effort S.

### F6 · Minor · `/branch child` on a New Session opens an unrelated session
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:324`.
- Claim: `state.Sessions.FirstOrDefault(s => s.ForkedFromSessionId == state.SessionId)` compares two `Guid?`; with no bound session both sides are null, so the newest session that is not a branch is treated as "the child".
- Failure scenario: after `/clear` or at start-up with no last session, `/branch child` resumes the most recently updated root session as if it were a branch of nothing.
- Evidence: P7 output: `/branch child with SessionId=null -> SessionId=1111… title=Parent`.
- Fix: return "Open a session first." when `state.SessionId` is null (same as `/fork`). Effort S.

### F7 · Minor · Tool-markup detection ignores the entry role, hiding user prompts on resume
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/PersistedToolInteraction.cs:12-30`; caller `SessionWorkspaceService.cs:721-727`.
- Claim: any entry whose content starts with `[ToolCall:` or `[ToolResult:` (case-insensitive, after trimming), or that carries a ToolName, is routed to Incantations and removed from the transcript, whatever its role. The host's own definition is role-bound and ordinal (`TurnContextGuards.cs:32-39`: ToolCall = assistant, ToolResult = system). A user prompt that starts with that text disappears from the resumed transcript and appears as a fake Incantation; model output starting with `[ToolCall: …]` is shown as a tool call that never ran.
- Failure scenario: the operator pastes a log line `[ToolResult: …] why did this fail?` as a prompt; after `/resume` the prompt is missing from the transcript (the answer appears with no question) and an "unparseable" row appears in Incantations instead.
- Evidence: P8 output: `role=user content='[ToolResult: this is my question]' IsToolInteraction=True`; `role=user content='[toolcall: pretend(1)]' IsToolInteraction=True`.
- Fix: mirror `TurnContextGuards`: ToolCall only for assistant entries (ToolName or ordinal prefix), ToolResult only for system entries with the ordinal prefix, `tool`/`function` roles as today. Effort S.

### F8 · Minor · The coalescer's cancellation and dispose flushes throw after the UI channel completes
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/StreamingUiCoalescer.cs:225-231` (doc claim at `:99-106`).
- Claim: `FlushCancelledAsync`/`FlushFinalAsync`/`DisposeAsync` write with `WriteAsync`, relying on "the UI channel is unbounded, so it completes at once". On quit the host cancels the run token and then immediately completes the channel (`CommandCenterHost.cs:120-123`, `:653`). A turn that is streaming when the operator confirms Ctrl+Q reaches `FlushCancelledAsync` after `TryComplete`, which throws `ChannelClosedException` from the chat runner's cancel handler; `HandleSubmitAsync`'s catch then logs "Command Center submit failed." at Error and its own `ui.WriteAsync` throws again out of the fire-and-forget task (unobserved).
- Failure scenario: confirmed quit during a streaming answer writes an Error entry with a ChannelClosedException stack to the log on an ordinary exit.
- Evidence: P9 output: `FlushCancelledAsync after TryComplete: System.Threading.Channels.ChannelClosedException: The channel has been closed.` Ordering traced in `RunTerminalLoop` (cancel then cleanup → TryComplete).
- Fix: use `_ui.TryWrite(...)` in `FlushCoreAsync` (unbounded channel: TryWrite is the non-blocking write, and false simply means the UI is gone). Effort S.

### F9 · Minor · `/attach` and `/pin` collapse runs of whitespace inside the path
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandParser.cs:81`, `:92-94`, `:114`.
- Claim: the command line is split with `RemoveEmptyEntries | TrimEntries` and the remainder is rejoined with single spaces, so a path containing two consecutive spaces (or a tab) is changed before it is resolved.
- Failure scenario: `/attach My  Notes.txt` reports "not found at …/My Notes.txt", or, if a file of that single-spaced name exists, stages the wrong file for the model; `/pin file docs/a  b.md` likewise.
- Evidence: P10 output: `/attach My  Notes.txt -> Kind=Attach Argument='My Notes.txt'`; `/pin file docs/a  b.md -> … Secondary='docs/a b.md'`.
- Fix: take the raw substring after the head token (and after the kind token for `/pin`), trimming only the ends. Effort S.

### F10 · Minor · Transcript paging and entry selection go stale after live turns
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs:359-392` (paging), `:644-699` (bound session), `:701-740`.
- Claim: `SessionEntryCount`, `LoadedTranscriptEntries` and `TranscriptEntryOffset` are set only by resume/fork. Turns sent in Command Center add entries at the head of the newest-first offset space without updating them, so the next Ctrl+PgDn page is offset by the new entries and `HasOlderTranscriptEntries` is computed against the stale total, hiding the oldest entries; a session created in Command Center has `SessionEntryCount = null`, so it can never page back once the 1,000-entry log trims. Live entries have no `SourceEntryId`, so `/fork at` and `/fork alternative` on an answer produced in this sitting report "Select a transcript entry/answer" until the session is resumed again.
- Failure scenario: resume a 350-entry session (page 0..199), send 30 turns (+60 entries), Ctrl+PgDn: offset 200 returns new-numbered 200..399 and `200+200 < 350` is false, so the last 10 entries are unreachable; `/fork alternative` on the latest answer fails.
- Evidence: trace — the only writers are `SessionWorkspaceService.cs:336/556` (ApplySessionMeta), `:732-734`, and `CommandCenterState.ClearSessionBinding`; `CommandCenterWindow.GetSelectedTranscriptEntryId` returns `SourceEntryId`, which `SessionLogBuffer.Append` never sets. Not executed.
- Fix: after a turn completes, re-read the session detail (entry count) and the newest transcript page (or bump `SessionEntryCount`/offset by the entries the turn appended and record the persisted entry ids from the stream), so paging and selection stay aligned. Effort M.

### F11 · Minor · `WrapLine` never breaks at the column just past the window
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionLogBuffer.cs:533-557`.
- Claim: the break search looks for whitespace strictly inside the window (`i > index`, `i <= sliceEnd-1`) and never considers `line[sliceEnd]`. A word that ends exactly at the width is pushed to the next row; when the row is a single unbroken token of exactly the width (a hash, URL or path), the next row starts with the space and the following word is hard-split.
- Failure scenario: `WrapLine("hello world again", 5)` gives `hello | ␠worl | d | again`. At 80/100 columns on ordinary prose this did not occur (P5b: 0 of 1,838 rows), so the visible cases are long tokens exactly as wide as the pane; ordinary rows only wrap one word early.
- Evidence: P5 outputs `['aaaa' | ' bbb' | 'b']`, `['hello' | ' worl' | 'd' | 'again']`, `['The' | 'quick' | 'brown' | 'fox jumps']`; P5b frequency.
- Fix: if `sliceEnd < line.Length && char.IsWhiteSpace(line[sliceEnd])`, emit `line[index..sliceEnd]` and skip the whitespace. Effort S.

### F12 · Minor · A bare `/branch`, `/session` or `/ward` suggests itself
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandParser.cs:342-352`, `SlashCommandRegistry.cs:115-128`.
- Claim: a registered head with missing or wrong sub-arguments falls to `Unknown`, and the prefix match returns the head itself.
- Evidence: P11 output: "Unknown command `/branch`. Did you mean `/branch`?" (also `/session`, `/ward`).
- Fix: when the head resolves in the registry, return `Denied` with the descriptor's `Usage` instead of the did-you-mean path. Effort S.

### F13 · Minor · `/pin file` containment is case- and alias-sensitive on case-insensitive volumes
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:822-829`.
- Claim: the candidate is accepted only if it starts with the working directory using `StringComparison.Ordinal`; `Path.GetFullPath` does not normalise case or resolve symlinks. On Windows (a shipping RID) and default case-insensitive APFS, an absolute path typed with different case, or through `/tmp` rather than `/private/tmp`, is refused although `File.Exists` is true.
- Failure scenario: Windows, cwd `C:\Repo`, `/pin file c:\repo\src\a.cs` → "File pins must name an existing file inside the workspace."
- Evidence: trace (not executed on Windows).
- Fix: compare with the platform's path comparison (OrdinalIgnoreCase on Windows/macOS) or reuse `WorkspacePathPolicy`-style containment on resolved paths. Effort S.

### F14 · Minor · `/config` help and Command Reference promise the configuration file path; the handler never prints it
- File: `src/RetroDownfall.Arcanum.Cli/CommandCenter/ShellCommandDispatcher.cs:1156-1175` (registry text `SlashCommandRegistry.cs:37`, `docs/Arcanum.Command.Reference.md` `/config` row).
- Claim: the output lists models, providers, workspace and campaign and says "run `arcanum config list`", but no file path.
- Fix: print the resolved `arcanum.json` path (safe value), or change the help/registry/doc text. Effort S.

## Checked and found sound

- `SessionLogBuffer` locking: every read and write of `_entries` and `_wrapCache` is under `_gate`; `CopyLinesTo` builds lines under the lock and edits the UI-owned collection outside it; entry text only enters through `SanitizeAndClamp`.
- Wrap-cache reuse: the settled prefix is reused only after an exact span comparison of the formatted prefix, so the prefix-label change at stream start/end and late surrogate pairing force a rewrap; `newSettledLength >= settledLength` holds whenever the prefix matched; the cache is pruned on trim/remove/clear and so stays bounded by the entry count.
- Truncation keeps surrogate pairs whole (`Utf8Truncation.SafeCharSliceLength`) and caps after sanitising.
- `TerminalCellMetrics.TakeCells` always advances with `forceProgress`, so `WrapLine` and `OverlayLayout.WrapLines` terminate on wide and zero-width graphemes; `OverlayLayout.MeasureHeight`'s clamp bounds are both ≥ `MinHeight`, so it never throws.
- `StreamingUiCoalescer.WithTrailingFlushAsync`: an abandoned wait cancels the linked enumeration token and drains the outstanding `MoveNextAsync` before the enumerator is disposed; trailing flushes run only while the consumer awaits the next item; timer tasks are linked to the caller's token.
- `SessionWorkspaceService` cancellation: every catch excludes `OperationCanceledException`; `TransientStatus` is reset in `finally`; resume and fork change the binding only after the last-session save completes (pinned by the existing cancellation/refusal tests); `PersistBoundSessionAsync` refusal keeping the prior binding is deliberate and tested.
- `/resume` and `/clear` are denied while generating (P1).
- `SessionAttachmentReveal` builds `open -R` / `explorer /select,` / `xdg-open` with `ArgumentList` and `UseShellExecute=false`; `TryBuildAbsoluteAttachmentPath` rejects rooted paths and `..`, and checks the resolved path stays under the attachments root.
- `PinContextAsync` refuses non-regular files before opening, streams the hash, and catches IO/permission failures.
- `SlashCommandRegistry.Distance`: the row-minimum early exit is valid for Damerau-Levenshtein (a transposition value two rows down is bounded by the intermediate row's minimum + 1).
- Parser numeric arguments: list offsets must be non-negative ints, versions > 0; `ParseListOffset` and `AppendDisplayContinuation` cannot overflow because `Skip` past the end returns an empty page first.
- `ThinkingSpinner.Frame` handles negative ticks; `CommandCenterFocusCycle.Next` handles regions off screen.
- `SessionActionWindowState.Capture` reads the views synchronously before any await, as its remarks require.

## Noted, not reported

- `RefreshMcpAsync` (`ShellCommandDispatcher.cs:936-953`) catches `OperationCanceledException` along with everything else; the following call in `/doctor` rethrows it, and `/mcp` merely appends a stale list on a cancelled run. No operator-visible effect.
- `/look` and the registry description ("Show an Eye of the World snapshot") differ from the handler, which prints only the working directory; the Command Reference row matches the handler. Usability lens.
