# Task 4 report — effect-level Ward-removal acceptance coverage

## Initial status

- Started at `12deedf7` with substantial pre-existing uncommitted issue-219 changes across docs,
  production, CLI, and tests. Those changes were preserved.
- `McpConnectionManagerBootstrapIdempotencyTests.cs` and
  `TurnProjectionSemanticTests.cs` were clean before this task.
- `WardAutoApprovalPolicyTests.cs` already had issue-219 edits, including reflection/member-absence
  assertions. Task 4 removes the file rather than retaining or replacing those implementation-shape
  tests.

## Test-first evidence

- Added the two acceptance tests before any production edit. They invoke the real
  `McpConnectionManager` internal registration path, the in-process `ArcanumInternalToolServer`, the
  SDK bridge, and `ToolExecutionPipeline`.
- The first focused invocation exposed only test compilation omissions (the Ward contract namespace,
  the existing literal `write_file` tool name, and test-only fixture imports). Those were corrected
  without changing production code.
- Once compiled, the focused behavioral filter was GREEN immediately: the current issue-219 working
  tree already allowed both effects. There was therefore no genuine behavioral RED and no production
  fix to make.

## Focused verification

1. `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~McpConnectionManagerBootstrapIdempotencyTests.Unattended_forbidden"`
   - Passed: 2; failed: 0; skipped: 0.
2. `dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj --no-build --disable-build-servers -m:1 --filter "FullyQualifiedName~McpConnectionManagerBootstrapIdempotencyTests|FullyQualifiedName~TurnProjectionSemanticTests|FullyQualifiedName~WardRecordPipelineTests"`
   - Passed: 36; failed: 0; skipped: 0.

Both commands required the normal local test-host IPC socket outside the filesystem sandbox. The
initial build-based invocation also printed NuGet vulnerability-audit `NU1900` warnings because the
environment could not load `https://api.nuget.org/v3/index.json`; the final no-build verification was
warning-free.

## Files and commit

- `tests/RetroDownfall.Arcanum.Tests/Mcp/McpConnectionManagerBootstrapIdempotencyTests.cs`
  - Adds effect-level `write_file` and `scribe_lexicon` cases with real effects, restrictive
    `ForbiddenArts`, unattended requests, persisted assistant-turn context for the write, and exact
    Ungated Ward audit assertions.
- `tests/RetroDownfall.Arcanum.Tests/Intelligence/WardAutoApprovalPolicyTests.cs`
  - Deleted as obsolete reflection/absence coverage.
- `tests/RetroDownfall.Arcanum.Tests/Intelligence/TurnProjectionSemanticTests.cs`
  - Corrects only stale operator-approval wording; semantic assertions are unchanged.
- Scoped implementation commit: `79ae543c` (`test: cover ungated registered tool effects`). It contains
  this report's original version and only the files listed above; the following report-only update
  records that immutable implementation commit ID.

## Self-review and concerns

- Each test owns a temporary workspace and fixture-local stateful Lexicon. It has no arbitrary
  sleeps, global-state coupling, reflection, prebuilt Ward frames, or delegate substitute for the
  tool invocation.
- The real workspace containment, persisted assistant-turn binding, SDK registration/dispatch,
  Sanctum call path, Lexicon scope resolver, and tool capability registration remain active.
- This is intentionally only Task 4 acceptance coverage and cleanup. It does not claim issue-wide
  completion or run the controller-owned full suite.
