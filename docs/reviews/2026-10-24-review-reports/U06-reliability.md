# U06 · Arcanum.Cli · CommandCenter (1) · Reliability lens

Tree: `/Users/mat/Source/apps/RetroDownfall.Arcanum/.claude/worktrees/codebase-review` at `ac0d2d8e`.

`units.json` was not present in the scratchpad (`scratchpad/review/` did not exist), so the unit was reconstructed from its description: `src/RetroDownfall.Arcanum.Cli/CommandCenter/`, sorted by path, with the cumulative line count reaching exactly 11,970 at `IncantationStore.cs`. That gives 30 files, `AbandonableBlockingWork.cs` through `IncantationStore.cs`.

## Coverage

I read every one of the 30 files in full:

AbandonableBlockingWork, CommandCenterApp, CommandCenterAttachmentDriftMonitor, CommandCenterBrandBanner, CommandCenterChatRunner, CommandCenterGuidance, CommandCenterHardModalArbiter, CommandCenterHintBar, CommandCenterHost (all 2,619 lines), CommandCenterHumanPromptCoordinator, CommandCenterKeyChords, CommandCenterKeymap, CommandCenterLineSync, CommandCenterModelChoice, CommandCenterModelPicker, CommandCenterSessionMutationGuard, CommandCenterStartUpInterrupt, CommandCenterState, CommandCenterSubmitText, CommandCenterTermination, CommandCenterTheme, CommandCenterTurnAttachmentBuilder, CommandCenterUiUpdatePump, CommandCenterWindow (all 2,488 lines), CommandPaletteCatalog, ComposerLayout, HumanPromptOverlayContent, ICommandCenterHost, IncantationFormatter, IncantationStore.

To trace callers and effects I also read parts of files outside the unit:
- `SessionWorkspaceService.cs`: resume, paging, bound session and history ingest.
- `OverlayLayout.cs`, `StreamingUiCoalescer.cs`, and parts of `SessionLogBuffer`.
- `ArcanumApiClient` (`SendRequestAsync`, timeouts, endpoints).
- `CliApplicationFactory`: DI registrations and the launch path.
- The API endpoints `/api/models`, `/api/sessions/{id}/attachments` and `/entries`.
- `SessionAttachmentStore.RevalidateBoundSourcesAsync`.
- `WizardIntelligenceProvider`: the order in which ToolCall events are emitted.
- `ProviderResolver.TryResolveProviderForModel`.

Docs read:
- DESIGN §16.5 and §16.6: the Command Center reliability bullets, hard-modal arbitration, launch token and cleanup, transcript refresh, cell metrics.
- DESIGN attachment-state paragraph (around line 1396).
- Command Reference "The model drop-down".
- API.md row for `/api/sessions/{id}/entries`.

Existing tests run on the shared tree:
- 51 tests in the lifecycle classes passed: `CommandCenterHardModalArbiterTests`, `CommandCenterHumanPromptCoordinatorTests`, `CommandCenterTerminationTests`, `CommandCenterUiUpdatePumpRunTests`, `CommandCenterStartUpInterruptTests`, `CommandCenterAppTests`, `CommandCenterHostTests`.
- All 783 tests under `RetroDownfall.Arcanum.Tests.Cli.CommandCenter` passed, with 4 skips, which are Windows-only. None of them catches the findings below.

## Probes

The probe source is `/private/tmp/claude-501/-Users-mat-Source-apps-RetroDownfall-Arcanum/d111c03e-eda5-4c82-a12d-f02f9cd1cdb8/scratchpad/review/reports/A/U06-reliability-probes.cs.txt`. It was built and run in the scratch worktree `wt/U06-reliability`, which has since been removed. Each probe asserts the current, defective behaviour, and all 11 pass. The log is `results/A/U06-probes-final.log`.

The host probes build a real `CommandCenterHost` with fake-handler `ArcanumApiClient`s and reach its private `DispatchActionAsync`, `HandleSubmitAsync` and `TryHandleModalOverlayKey` through reflection. They use a real Terminal.Gui `IApplication` on the ANSI driver. `DispatchProxy` cannot implement `IApplication`, because its `MainThreadId` setter is internal.

| Probe | Output |
|---|---|
| 1 Ctrl+Q over ask_human | `after Ctrl+Q: overlay=QuitConfirm answer='' answerVisible=False promptActive=True` · `after Esc: overlay=QuitConfirm footer='Enter submit · Ctrl+J newline · Ctrl+C cancel turn'` · `after Enter: requestExit=True` |
| 2 CJK overlay width | `question cells=80 overlay inner width=58` |
| 3 turn end with palette open | `after turn: overlay=CommandPalette focusRegion=Composer inputFocus=True filterFocus=False` · `key 'a': handled=True/True filter=''` |
| 4 drift monitor churn | `idle baseline GETs=1; GETs during 4.7s of unrelated obj/ writes (+1s tail)=22; session attachments=0` |
| 5 overlapping older-page loads | deterministic order: `history=[null,null]`, `after one newer: cursor=null hasNewer=True`. The first run with simultaneous answers gave `history=[null]`, a lost `List.Add` from two pool threads. |
| 6 duplicate model id | `confirmation='Model set to llama3 (Remote) for this session.' routed=Local rows=[● llama3  (Local) \| ● llama3  (Remote)]` |
| 7 resume and attachments | `resume=Success session=bbbb… attachments=1 footer='Indexing attachments: 1 pending'` |
| 8 pump while UI busy | `30 RefreshLog updates while the UI thread was busy -> 30 applies queued, 30 run` |
| 9 long ask_human question | `80x24` and `120x40`: `question 578 chars; shown rows: 10; ends with ellipsis row: True`. The closing question sentence is absent. |
| 10 older transcript pages after live turns | `first page 250..449; offset=200 range=250..449 overlapWithFirst=200; offset=400 range=50..249; unreachable original entries: 50 (0..49)` |

## Findings

Ordered by severity, most severe first.

### 1. Important · Ctrl+Q during an ask_human prompt replaces the hard modal, wipes the typed answer, and makes "Enter submit" quit Command Center

- `CommandCenterHost.cs:1584` (`RequestQuit`).
- Related: `CommandCenterHost.cs:598-637` (global chords), `:1012-1020` (`CloseOverlayOrFocusComposer`), `CommandCenterWindow.cs:927-944` (`ShowOverlay`).

What happens:
- Ctrl+Q is a global chord in `app.Keyboard.KeyDown`. It reaches `RequestQuit`, which is not gated by `BlockAuxiliaryWhileHardModal`. While a turn is generating (always the case while an ask_human prompt is up), `RequestQuit` shows `QuitConfirm`. `ShowOverlay` sets `OverlayAnswer.Visible=false` and `OverlayAnswer.Text=""`.
- The overlay says "Esc = cancel". Esc is consumed by `app.Keyboard.KeyDown` as `CloseOverlayOrFocusComposer`, and that branch first checks `humanPromptCoordinator.IsActive`. It therefore sets the footer to `HumanPromptFooter` ("Enter submit · Ctrl+J newline · Ctrl+C cancel turn") and leaves `QuitConfirm` up.
- Enter then maps to `ConfirmPending`, which quits Command Center. That ends the turn and the prompt, and stops the auto-launched host.
- The only other way out is Ctrl+C, which cancels the turn.

Scenario: a model asks "Which port…?" and the operator types "8080". They press Ctrl+Q, then Esc. The overlay stays and the footer says "Enter submit". They press Enter and Command Center exits. The answer is lost.

Evidence: probe 1, output in the table above.

Suggested fix:
- Treat Quit like the other auxiliary overlays while a hard modal is active: show the footer "answer or Ctrl+C" and do not open `QuitConfirm`.
- If quitting must stay possible, check `state.Overlay is QuitConfirm or DiscardConfirm` before the HITL check in `CloseOverlayOrFocusComposer`, and on cancel re-show the pending prompt with its preserved answer.
- `ShowOverlay` should never clear `OverlayAnswer` while a prompt is pending.
- Add a host test.

Effort: S.

### 2. Important · A long ask_human question is cut to four rows (and a tall one is clipped), and the full question is shown nowhere

- `CommandCenterWindow.cs:1077` (`ShowHumanPromptOverlay` → `OverlayLayout.WrapLines`).
- Related: `OverlayLayout.cs:51-88`, `CommandCenterWindow.cs:2020-2040`, `IncantationFormatter.cs:385-421`.

What happens:
- `OverlayLayout.WrapLines` cuts every logical line to `MaxWrappedPreviewRows = 4` rows and ends the last one with "…". It was written for argument previews, but it is applied to the question.
- The body is a fixed-height Label. `OverlayLayout.MeasureHeight` clamps it to the body pane, so a question with many lines is clipped with no way to scroll.
- The question does not appear in the transcript. The Incantations row for `ask_human` shows only the name and state, because `question` is not a summary key. The operator therefore answers a question they cannot fully read.

Scenario: a 578-character one-paragraph question whose real ask is in the last sentence. At 80x24 and at 120x40 the overlay shows 4 rows plus "…", and "Which option should I take: A, B or C?" is never shown.

Evidence: probe 9, at both sizes.

Suggested fix:
- Render the question through a scrollable ListView, or wrap it without the preview cap and let the overlay grow up to the body height and scroll.
- Also append the full question to the transcript as a Status entry when the prompt opens.

Effort: M.

### 3. Important · Older transcript pages are offset-based against a total captured at resume, so live turns shift every older page and the oldest entries become unreachable

- `CommandCenterState.cs:296-298` (`HasOlderTranscriptEntries`).
- Related: `SessionWorkspaceService.cs:356-371` (`LoadOlderTranscriptPageAsync`), `SessionWorkspaceService.cs:732-734`, `CommandCenterHost.cs:1235-1297`.

What happens:
- `SessionEntryCount` is set only in `ApplySessionMeta` at resume or fork. `TranscriptEntryOffset` and `LoadedTranscriptEntries` change only when a page is loaded.
- Live turns append entries on the host, which serves pages newest-first by offset. Every turn therefore shifts every older-page offset by the number of entries it added.
- The next Ctrl+PgUp returns the page already on screen, and `HasOlderTranscriptEntries`, still computed from the stale total, turns false while K of the oldest entries were never shown.
- The host already supports a keyset cursor (`beforeCreatedAt`/`beforeId`) that would not shift.

Scenario: resume a 450-entry session (shows 449..250), then run 100 turns (200 entries).
- First Ctrl+PgUp: 449..250 again, exactly the first page.
- Second Ctrl+PgUp: 249..50.
- Then "No older transcript page is available". Entries 0..49 cannot be reached.

Evidence: probe 10, output in the table above.

Suggested fix: page older entries with the keyset cursor from the oldest loaded entry (`beforeCreatedAt` + `beforeId`). Page newer by re-resuming or by a forward cursor. Derive "has older" from a short read, `limit + 1`, instead of a total captured at resume.

Effort: M.

### 4. Minor · When a turn ends, focus moves to the composer even if the operator has since opened an overlay or moved to another pane; their next keys are swallowed or typed into the composer

- `CommandCenterHost.cs:2332-2337` (the `HandleSubmitAsync` `finally`).
- Related: `CommandCenterHost.cs:2447-2452`, `:313-366`.

What happens:
- The `finally` always runs `window.FocusInput(); state.FocusRegion = Composer`, whatever `state.Overlay` is and wherever the operator has moved.
- With the palette, the slash menu or the model picker open, the overlay stays visible but the keyboard is now on the composer. `Input.KeyDown` → `TryHandleModalOverlayKey` swallows every printable key, so filter typing silently stops.
- With the Sessions pane focused, `j`/`k` now type letters into the composer, and the next Enter sends them as a chat message.

Scenario: during a long turn the operator presses Ctrl+K and starts typing "ref…". The turn ends, the next letters vanish, and the filter stays as it was.

Evidence: probe 3. Output: `after turn: overlay=CommandPalette focusRegion=Composer inputFocus=True filterFocus=False`, and the key `a` was handled with the filter left empty.

Suggested fix: in the `finally`, return focus to the composer only if the turn's submit left focus there and no overlay is open (`state.Overlay == None && state.FocusRegion == Composer`). Otherwise only refresh.

Effort: S.

### 5. Minor · Chat turns are not gated against gated session mutations in flight; a message sent while a resume runs goes to the old session and the stream re-binds Command Center to it

- `CommandCenterHost.cs:2286-2304` (chat path, no `_actionGate`). The slash path at `:2246` takes the gate.
- Related: `CommandCenterChatRunner.cs:134` (`SessionId: state.SessionId`), `CommandCenterChatRunner.cs:322-341`, `SessionWorkspaceService.cs:644-690` (`PersistBoundSessionAsync` sets `state.SessionId`), `CommandCenterHost.cs:1431-1480`.

What happens:
- Session mutations deny while `Generating`, but a turn can start while a mutation holds the gate. `/resume <id>` clears the composer and awaits three host calls plus a write to `cli-context.json`.
- An Enter in that window starts a turn with the old `state.SessionId`. The resume then replaces the transcript. The turn's `sessionBound` frame then calls `PersistBoundSessionAsync(old)`, which sets `state.SessionId` back to the old session and saves it as the last session.
- From then on the header and later turns use session A while the transcript shows session B.

Scenario: the operator types `/resume <id>`, presses Enter, pastes a message and presses Enter again before the resume finishes.

Evidence: trace only. Timing-dependent, no deterministic seam.

Suggested fix: admit chat through the same gate (`_actionGate.Wait(0)`, with a "busy" footer if it is contended), or have gated mutations claim a "mutation in flight" flag that `TryBeginTurn` refuses. In `ChatRunner`, ignore a `sessionBound` for a session that is no longer `state.SessionId`.

Effort: S.

### 6. Minor · Ctrl+PgUp/PgDn page loads bypass the action gate; overlapping loads corrupt the session page history

- `CommandCenterHost.cs:1085-1105` and `:1179-1199` (not gated).
- Related: `SessionWorkspaceService.cs:123-167`, `CommandCenterState.cs:256`.

What happens:
- `LoadOlder`/`LoadNewer` session and transcript pages run without the gate. They read the cursor, await HTTP, then mutate shared state, including the plain `List` `SessionPageHistory`, from pool threads.
- Two overlapping Ctrl+PgDn presses (key auto-repeat or a double press during one round trip) both start from the same cursor. Both push to the history, so `HasNewerSessionPage` stays true on the first page.
- When the two continuations run at the same time, one `List.Add` is lost. The page loads also race the post-turn `RefreshSessionsAsync`, which clears that history.

Evidence: probe 5.
- With answers delivered in order: `history=[null,null]`, then after one Ctrl+PgUp `cursor=null hasNewer=True`.
- With simultaneous answers (first run): `history=[null]`, a lost update.

Suggested fix: run the page loads through `RunGatedAsync`, or a dedicated non-reentrant gate that drops a request while one is in flight.

Effort: S.

### 7. Minor · `EndTurn` runs before `TurnCts` is cleared, so a turn admitted in that window gets its source disposed and cannot be cancelled

- `CommandCenterHost.cs:2321`.

What happens: the `finally` runs:

```csharp
state.EndTurn();
CancellationTokenSource? captured = state.TurnCts;
state.TurnCts = null;
captured?.Dispose();
```

If the UI thread admits the next submit between `EndTurn` and the read, the next turn's `TryBeginTurn` succeeds and it publishes `state.TurnCts = turnCts2`. The old `finally` then reads `turnCts2`, nulls the slot and disposes it.
- Ctrl+C then finds `TurnCts == null`, so `TryCancelTurn` returns false.
- The linked source is unregistered, so shutdown cancellation does not reach the turn either.

Evidence: trace only. The window is a few instructions wide.

Suggested fix: clear the slot only if it is still this turn's (`Interlocked.CompareExchange(ref _turnCts, null, turnCts)`), dispose it, and only then call `EndTurn()`.

Effort: S.

### 8. Minor · The attachment drift monitor revalidates on every file event anywhere under the working directory; each revalidation double-SHA-256s every tracked source

- `CommandCenterAttachmentDriftMonitor.cs:79-117` and `:304-306`.
- Related: `SessionEndpoints.cs:243-288`, `SessionAttachmentStore.Lifecycle.cs:494-560`.

What happens:
- A recursive `FileSystemWatcher` over the whole working directory signals on any create, change, delete or rename. After a 150 ms debounce the pump calls `GET /api/sessions/{id}/attachments`.
- That endpoint runs `RevalidateBoundSourcesAsync`: an open, a containment check and a double SHA-256 per distinct tracked file.
- It does this even when the session has no tracked attachments, and it never filters events to the tracked paths.
- A build, `git` operation or `node_modules` install in the workspace, or launching from `~` on macOS where `~/Library` churns, keeps this running at about 4–6 requests per second.

Evidence: probe 4. Unrelated `obj/` writes at 50 Hz for 4.7 s, with zero attachments, produced 22 revalidation GETs (baseline 1).

Suggested fix:
- Skip the fetch when no row is a `WorkspaceFile`.
- Ignore events whose path is not a tracked `SourceRelativePath`. Keep the overflow and error events as a full revalidate.
- Add a floor of about 1 s between fetches.

Effort: S.

### 9. Minor · Resuming from the Sessions pane keeps the previous session's attachment rows, so the footer shows another session's indexing status

- `CommandCenterHost.cs:1431-1480` (`DoResumeCoreAsync` never calls `attachmentDriftMonitor.RequestRefresh()`; the slash and turn paths do, at `:2256` and `:2306`).
- Related: `CommandCenterState.cs:507-523`, `SessionWorkspaceService.cs:242-350`.

What happens: `ApplySessionMeta` and `ApplyTranscriptPage` do not reset `SessionAttachments`, and the Sessions-pane resume does not request a refresh. Session B's footer, its badges, and the notice comparison baseline keep session A's rows until a file event or a slash command happens. In a quiet workspace that can be indefinitely.

Evidence: probe 7. After a successful resume to B, `attachments=1` and the footer still reads `Indexing attachments: 1 pending`.

Suggested fix: reset `SessionAttachments` to `[]` whenever `ApplySessionMeta` changes `SessionId`, and call `RequestRefresh()` after a successful `DoResumeCoreAsync`. The same applies to the `ResumeWithConfirm` path.

Effort: S.

### 10. Minor · The overlay frame is sized by UTF-16 length, so wide text is clipped

- `CommandCenterWindow.cs:2004-2013`.
- Contradicts DESIGN §16.6, "overlay wrap and width all measure terminal display cells".

What happens: `ShowOverlay` and `ShowHumanPromptOverlay` wrap to a width measured in cells. `ApplyAbsoluteLayoutCore` then re-sizes the pane from `_overlayLines` using `line.Length`. A CJK row of 40 characters is 80 cells wide but has a length of 40, so the pane gets the 60-cell default and its right part is cut off.

Evidence: probe 2. The question is 80 cells wide and the overlay's inner width is 58.

Suggested fix: use `TerminalCellMetrics.MeasureWidth(line)` in that loop.

Effort: S.

### 11. Minor · The model drop-down offers the same model id once per provider, but a selection stores only the id; the confirmation names a provider the host will not use

- `CommandCenterHost.cs:1721-1743` (`ApplySelectedModel`: `state.Model = selected.Model`).
- Related: `CommandCenterModelPicker.cs:33-58` and `:101-105`, `ProviderResolver.cs:146-199`.

What happens:
- `Build` deliberately keeps "llama3 (Local)" and "llama3 (Remote)" as separate rows, calling them "genuinely different routes". The session slot and `PingRequest` carry only the model id.
- The host resolves the id in configured order, so picking the Remote row still routes to Local. The transcript nevertheless says "Model set to llama3 (Remote)".
- Both rows render with the active `●`.

Evidence: probe 6, output in the table above.

Suggested fix: either collapse duplicate ids into one row that lists their providers, or name the provider the host will actually route to (the first one) in the confirmation. A real per-provider choice would need a provider-qualified model in the session slot and in `PingRequest`.

Effort: S.

### 12. Minor · The UI update pump coalesces on the producer side of a non-blocking hand-off, so updates that arrive while the UI thread is busy are not folded

- `CommandCenterUiUpdatePump.cs:44-66`.
- The doc comment at lines 7-13 claims "dozens of refreshes while the UI thread is still applying the first one" are folded.

What happens: `marshal` is Terminal.Gui's `Invoke`, which queues and returns immediately, so the pump never waits for an apply to finish. It drains whatever is in the channel at that moment, usually a single item, and queues it. Every refresh that arrives while the UI thread is busy (a resize, a large paste, a re-wrap) becomes its own apply on the UI queue, each a full `ApplyState` of its kind. The existing test only covers a burst written before the pump starts.

Evidence: probe 8. 30 `RefreshLog` updates written 5 ms apart while the UI thread was not draining resulted in 30 applies queued.

Suggested fix: keep at most one outstanding apply per kind. For example, set a flag when one is queued, clear it inside the queued work, and fold anything that arrives in between. Alternatively, wait on a completion before the next drain.

Effort: S.

### 13. Minor · A FileSystemWatcher failure at start-up ends the whole Command Center as CrashedAfterStart

- `CommandCenterHost.cs:254`.
- Related: `CommandCenterAttachmentDriftMonitor.cs:79-109`, `CommandCenterApp.cs:175-188`.

What happens: `attachmentDriftMonitor.Start` runs inside the `buildAndWire` lambda with no guard. `new FileSystemWatcher(...)` or `EnableRaisingEvents = true` can throw: macOS `FSEventStreamStart` failure, a Windows directory handle failure, a network or FUSE mount. The exception escapes to `RunAfterStart`, the operator sees "Command Center stopped unexpectedly", and the exit code is 1. Live badges are an optional feature.

Evidence: trace only.

Suggested fix: wrap `Start` in a try/catch, log at Warning, and continue without the watcher. The post-turn `RequestRefresh` still refreshes the badges.

Effort: S.

### 14. Minor · If `Application.Init` fails after the driver has touched the terminal, the terminal modes are not restored

- `CommandCenterApp.cs:113-114` and `:387-410`.

What happens: `CreateAndInit` creates the app and calls `Init()` inside a local. If `Init` throws, the outer `app` stays null and `terminalTouched` stays false. The `finally` therefore neither disposes the app nor calls `RestoreTerminalModes`, and the shell can be left on the alternate screen or with mouse reporting on. The start-failure message is written into that state.

Evidence: trace only. `Init` cannot be made to fail deterministically in a test.

Suggested fix: create the app first, assign it to the outer variable, set `terminalTouched = true`, and only then call `Init()`.

Effort: S.

### 15. Minor · Quitting with an auto-launched host that has stopped answering waits up to five minutes before the process exits

- `CommandCenterHost.cs:793-795`.

What happens: `StopHostIfWeStartedItAsync` calls `QuitServerAsync(CancellationToken.None)` on the short-call client. Its only bound is the 5-minute response-headers deadline (`ArcanumApiClient.DefaultRequestResponseHeadersTimeout`). After Ctrl+Q the UI is gone but the process lingers. The backstop applies only to SIGTERM and SIGHUP.

Evidence: trace only.

Suggested fix: run the quit on a linked source with a short timeout, for example 5 s, and log if it expires.

Effort: S.

## Checked and sound

- **`AbandonableBlockingWork`** releases the awaiting turn on cancel through `WaitAsync`. A fault from the abandoned delegate is observed through a continuation registered with `OnlyOnFaulted`. `ChatRunner` reports "Cancelled before the message was sent" (`AbandonableBlockingWork.cs:17-36`, `CommandCenterChatRunner.cs:97-106` and `:418-423`).
- **Launch lifecycle.**
  - `RunTerminalLoop` skips the loop when the token is already cancelled.
  - Its `finally` cancels the run token and runs cleanup even when the loop throws.
  - `CommandCenterStartUpInterrupt` absorbs the first interrupt only.
  - `CommandCenterTermination` arms the backstop before cancelling. It restores the terminal on a background thread within its budget and contains ODE and AggregateException. Dispose is idempotent.
  - The 51 lifecycle tests pass.
- **`CommandCenterUiUpdatePump`** guards each apply inside the marshalled work, and OCE ends the pump quietly. Only the coalescing claim fails (finding 12).
- **Hard-modal arbiter and coordinator.**
  - Lock order is always coordinator → arbiter.
  - Show callbacks run outside the arbiter's lock.
  - A declined show releases the slot.
  - Stale closes are matched by id, and the submit in-flight flag is reset only for its own prompt.
  - The host emits each `ask_human` ToolCall right before executing it (`WizardIntelligenceProvider.cs:3940-3975`), so the gap that "queued prompt outcomes are ignored" leaves is unreachable within one turn.
- **`SendRequestAsync`** converts every fault other than caller cancellation into a `Result` failure. `OpenModelPickerAsync`, the drift pump and the human-prompt submit therefore only throw on cancellation.
- **Model-picker race.** `/api/models` is built from configuration and answers fast, so the race in which the picker's await lands after a HITL prompt opens is a sub-millisecond window. I did not report it.
- **`CommandCenterState`.** The staged sets live under one lock, `SnapshotStaged`/`ClearStaged` remove only what a turn sent, `TryBeginTurn` uses CAS, and `TryCancelTurn`/`TurnTokenOr` tolerate a disposed source.
- **`IncantationStore`** formats under the store lock, and the per-record memo is safe under that lock. `AddFromHistory`'s unparseable branch is reached only with a null call id from `IngestHistoryTool`, so `_byCallId` and `_order` cannot diverge.
- **`CommandCenterTurnAttachmentBuilder`.** A token is removed only after its file has staged. Reads are bounded and refuse FIFOs and devices on the opened handle. The regex has a timeout. A Comprehension document is refused.
- **`CommandCenterApp.TryReadWithDeadline`** drains stdout and stderr asynchronously before `WaitForExit(timeout)` and kills the child at the deadline.
- **`ChatRunner` `finally`** completes the streaming entries, clears `ThinkingActive`, closes any active HITL prompt, and clears the staged set only on a clean Result with no error and no cancel.
- **Composer and palette helpers.** `ComposerLayout`, `CommandCenterLineSync`, `CommandCenterHintBar`, `CommandPaletteCatalog` and `CommandCenterKeyChords` are pure. I checked their boundaries by reading the code (CRLF handling, trailing breaks, exact-fill rows, wide graphemes, the tail edit), and their test suites pass.
