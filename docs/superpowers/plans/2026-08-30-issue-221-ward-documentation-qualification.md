# Issue #221 Ward Documentation Sweep and AOT Qualification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish and enforce the finished record-only Ward contract across Arcanum's governed documentation, including an exact 31-tool inventory, then qualify and deliver issue #221 without changing runtime behavior or absorbing issue #230.

**Architecture:** Treat the already-shipped #216-#220 runtime as authoritative. Add test-owned contracts at the three durable seams that can drift—tracker-reference syntax, the machine-readable DESIGN inventory, and the live CLI surface—then make the smallest canonical-document and code-owned help corrections that satisfy them. Regenerate the command map from the live CLI tree, perform a bounded semantic sweep, review once, run the complete locally applicable qualification matrix once, and merge the byte-identical verified tree into `remove-wards`.

**Tech Stack:** .NET 10, C# 13, xUnit, System.CommandLine, Markdown, JSON, Bash, Git, GitHub CLI/GraphQL, Native AOT, SQLCipher verification.

**Spec:** `docs/superpowers/specs/2026-08-30-issue-221-ward-documentation-qualification-design.md`

## Global Constraints

- Work only on `codex/issue-221-ward-documentation-sweep`, whose approved base is tracked `remove-wards` commit `165e23656b48c618ca67b9cdd76edb3e3256f1a3`; do not modify or merge into `main`.
- Issue #221 is a documentation-and-qualification slice. Do not change tool admission, execution order, persistence, configuration semantics, Ward event shapes, Covenant behavior, or any safety/capability boundary.
- Issue #230 remains a separate open follow-on for Annals behavior. Leave #197 open until #230 is delivered.
- Canonical scope is the `DocumentationIssueReferenceTests` governed inventory plus root `README.md`; dated reviews and `docs/superpowers` plans/specifications remain historical records.
- Every server-executed tool has Ward decision `None`: it records exactly one immediate informational `warded` / `wardResolved` pair with shared id and `origin: "ungated"`, then continues through independent boundaries.
- `ForbiddenArts` is only the request-selected `ToolPolicy.NoForbiddenArts` advertisement filter. `UnattendedMode` controls genuine human-input availability. Neither is an execution decision for ordinary tools.
- Preserve the active compatibility Ward engine and `/api/wards`, `arcanum ward`, Command Center `/ward`, and Forge Gatehouse compatibility surfaces.
- Preserve Sanctum, `WorkspacePathPolicy`, Artifact Attunement, edition/host-process policy, `workspace_check` trust and platform eligibility, tool-specific validation/capabilities, and Covenant preflight/disclosure/publication boundaries.
- `retire_covenant` is record-only and needs no Ward consent receipt; `propose_covenant` remains attended-only because that is capability admission, not a Ward decision.
- Keep the 31-name documentation inventory test-owned. Do not add a production catalog or alter MCP registration/advertisement.
- Generated `docs/Arcanum.CommandMap.json` must be updated only through `ARCANUM_UPDATE_COMMAND_MAP=1`; never edit it by hand.
- Use `RIPGREP_CONFIG_PATH=/dev/null` or `rg --no-config`, `--disable-build-servers -m:1` for large .NET commands, and preserve zero errors and zero warnings.
- Run focused RED/GREEN commands as needed, but run the complete locally applicable qualification matrix only once. Do not repeat a green full suite on a tree-identical merge.

## File and responsibility map

- `tests/RetroDownfall.Arcanum.Tests/Build/DocumentationIssueReferenceTests.cs` — recognizes all accepted tracker-reference spellings in governed documents.
- `tests/RetroDownfall.Arcanum.Tests/Intelligence/ToolRiskClassifierTests.cs` — binds the marked DESIGN table to the established 31-name no-Ward enumeration.
- `tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs` — asserts the live `mcp invoke` and retained Ward-resolution help descriptions.
- `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Core.cs` — source of the `mcp invoke` description.
- `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Wards.cs` — source of retained compatibility Ward option descriptions.
- `src/RetroDownfall.Arcanum.Cli/Commands/Wards/WardCommands.cs` — XML contract for the corresponding handler parameters.
- `docs/Arcanum.DESIGN.md` — authoritative architecture, execution sequence, Covenant reversal, and marked 31-tool inventory.
- `docs/Arcanum.Command.Reference.md` — canonical CLI wording and historical-preset terminology.
- `docs/Compendium.README.md` — canonical historical-preset terminology.
- `docs/Arcanum.CommandMap.json` — generated projection of the live CLI tree.
- `README.md` — current Ward-removal child accounting, explicitly separating #230.
- `docs/Arcanum.OATH.md`, `docs/Arcanum.API.md`, `docs/Arcanum.Design.Human.md`, `docs/Arcanum.DEBUGGING.Human.md`, `docs/Arcanum.CHAT-LOOP.md`, `docs/ArcanumOATH.Human.md`, and `docs/Arcanum.ConstraintInventory.json` — inspection-only unless the canonical semantic sweep finds a real stale execution-gate claim; `Arcanum.OATH.md` remains exempt only from the tracker-reference ban.

---

### Task 1: Close the tracker-reference spelling gap

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Build/DocumentationIssueReferenceTests.cs`
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `docs/Arcanum.Command.Reference.md`
- Modify: `docs/Compendium.README.md`

**Interfaces:**
- Consumes: `GovernedDocuments : string[]` and repository-root resolution from `NativeSqlCipherTestPaths.RepositoryRoot()`.
- Produces: `TrackerIssueReference : Regex` matching `issue #55`, `issue-219`, and `issue 219` without treating ordinary Markdown anchors as tracker references.

- [ ] **Step 1: Expand the contract before changing prose**

Replace the regex and its explanatory remark with this accepted-spelling contract:

```csharp
/// <para>The pattern is anchored on the word <c>issue</c> and accepts the tracker spellings
/// <c>issue #55</c>, <c>issue-55</c>, and <c>issue 55</c>. A bare <c>#</c> and digits is how every
/// Markdown section anchor in these documents is spelled, so matching that instead would flag the
/// cross-references the documents are supposed to carry.</para>
```

```csharp
private static readonly Regex TrackerIssueReference = new(
    @"\bissues?(?:\s*#\s*|[- ])\d+",
    RegexOptions.IgnoreCase,
    TimeSpan.FromSeconds(5));
```

- [ ] **Step 2: Run the focused test and verify the intended RED result**

Run:

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~DocumentationIssueReferenceTests"
```

Expected: FAIL with exactly these three governed offenders, proving the old regex missed hyphenated references:

```text
docs/Arcanum.DESIGN.md ... pre-issue-219
docs/Arcanum.Command.Reference.md ... pre-issue-219
docs/Compendium.README.md ... pre-issue-219
```

If any additional document appears, inspect and classify it before editing; do not weaken the regex.

- [ ] **Step 3: Replace tracker-dependent historical-preset wording**

Make these exact prose substitutions while preserving the documented v1-to-v2 behavior:

```text
docs/Arcanum.DESIGN.md
"The pre-issue-219 v1 workflow definitions remain frozen as historical validation definitions"
→ "The retired-key-era v1 workflow definitions remain frozen as historical validation definitions"
```

```text
docs/Arcanum.Command.Reference.md
"The pre-issue-219 v1 workflow definitions remain frozen for exact historical sidecar validation."
→ "The retired-key-era v1 workflow definitions remain frozen for exact historical sidecar validation."
```

```text
docs/Compendium.README.md
"The five pre-issue-219 v1 workflow definitions remain frozen for historical sidecar validation"
→ "The five retired-key-era v1 workflow definitions remain frozen for historical sidecar validation"
```

- [ ] **Step 4: Re-run the focused contract**

Run the Step 2 command again.

Expected: PASS, with no governed tracker reference under any accepted spelling.

- [ ] **Step 5: Commit the independently reviewable contract**

```bash
git add tests/RetroDownfall.Arcanum.Tests/Build/DocumentationIssueReferenceTests.cs docs/Arcanum.DESIGN.md docs/Arcanum.Command.Reference.md docs/Compendium.README.md
git commit -m "test: close governed issue reference gap"
```

---

### Task 2: Bind the DESIGN inventory to the 31 no-Ward names

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Intelligence/ToolRiskClassifierTests.cs`
- Modify: `docs/Arcanum.DESIGN.md`

**Interfaces:**
- Consumes: existing `KnownToolNames : string[]`, `ToolRiskClassifier.RequiresWard(string, bool, WardSettings)`, and `NativeSqlCipherTestPaths.RepositoryRoot()`.
- Produces: `WardToolInventoryRow(string Group, string Name, string CatalogStatus, string WardDecision, string IndependentBoundary)` and `ReadWardToolInventory(string design) : IReadOnlyList<WardToolInventoryRow>` over the stable DESIGN markers.
- Marker contract: `<!-- ward-tool-inventory:start -->` through `<!-- ward-tool-inventory:end -->`, located only inside DESIGN §11.14.

- [ ] **Step 1: Add repository-path access and the marked-table parser**

Add this using:

```csharp
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
```

Add these members to `ToolRiskClassifierTests`:

```csharp
private const string WardToolInventoryStart = "<!-- ward-tool-inventory:start -->";

private const string WardToolInventoryEnd = "<!-- ward-tool-inventory:end -->";

private sealed record WardToolInventoryRow(
    string Group,
    string Name,
    string CatalogStatus,
    string WardDecision,
    string IndependentBoundary);

private static IReadOnlyList<WardToolInventoryRow> ReadWardToolInventory(string design)
{

    int sectionStart = design.IndexOf(
        "### 11.14 Wards (record-only server tool calls and retained compatibility engine)",
        StringComparison.Ordinal);

    Assert.True(sectionStart >= 0, "DESIGN §11.14 is missing.");

    int nextSection = design.IndexOf("\n### 11.15 ", sectionStart, StringComparison.Ordinal);

    Assert.True(nextSection > sectionStart, "DESIGN §11.15 must bound the Ward section.");

    int inventoryStart = design.IndexOf(WardToolInventoryStart, StringComparison.Ordinal);

    int inventoryEnd = design.IndexOf(WardToolInventoryEnd, StringComparison.Ordinal);

    Assert.True(inventoryStart > sectionStart && inventoryEnd > inventoryStart && inventoryEnd < nextSection);

    Assert.Equal(inventoryStart, design.LastIndexOf(WardToolInventoryStart, StringComparison.Ordinal));

    Assert.Equal(inventoryEnd, design.LastIndexOf(WardToolInventoryEnd, StringComparison.Ordinal));

    string table = design[(inventoryStart + WardToolInventoryStart.Length)..inventoryEnd];

    string[] lines = table.Split(
        '\n',
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    Assert.True(lines.Length >= 2, "The Ward inventory table is empty.");

    Assert.Equal(
        "| Group | Tool | Catalog status | Ward decision | Independent boundary |",
        lines[0]);

    Assert.Equal("|---|---|---|---|---|", lines[1]);

    List<WardToolInventoryRow> rows = [];

    foreach (string line in lines.Skip(2))
    {

        string[] cells = line.Trim('|').Split('|', StringSplitOptions.TrimEntries);

        Assert.Equal(5, cells.Length);

        rows.Add(new WardToolInventoryRow(
            cells[0],
            cells[1].Trim('`'),
            cells[2],
            cells[3],
            cells[4]));

    }

    return rows;

}
```

- [ ] **Step 2: Add the failing inventory contract**

Add this test beside `Known_tool_inventory_contains_31_distinct_names`:

```csharp
[Fact]
public void Design_ward_inventory_matches_every_known_tool_and_names_no_ward_decision()
{

    string root = NativeSqlCipherTestPaths.RepositoryRoot();

    string design = File.ReadAllText(Path.Combine(root, "docs", "Arcanum.DESIGN.md"));

    IReadOnlyList<WardToolInventoryRow> rows = ReadWardToolInventory(design);

    Assert.Equal(31, rows.Count);

    Assert.Equal(31, rows.Select(static row => row.Name).Distinct(StringComparer.Ordinal).Count());

    Assert.Equal(
        KnownToolNames.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
        rows.Select(static row => row.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray());

    Assert.All(rows, static row => Assert.Equal("None", row.WardDecision));

    Assert.All(rows, static row => Assert.False(string.IsNullOrWhiteSpace(row.Group)));

    Assert.All(rows, static row => Assert.False(string.IsNullOrWhiteSpace(row.IndependentBoundary)));

    Assert.All(
        rows,
        static row => Assert.Contains(
            row.CatalogStatus,
            new[]
            {
                "normally advertised",
                "conditionally advertised",
                "recognized compatibility alias",
            }));

    WardToolInventoryRow browseWeb = Assert.Single(
        rows,
        static row => row.Name == ArcanumBuiltInToolNames.BrowseWeb);

    Assert.Equal("recognized compatibility alias", browseWeb.CatalogStatus);

}
```

The existing `RequiresWard_returns_false_for_every_known_tool_under_either_campaign_setting` theory already supplies both legacy Campaign values and a maximally restrictive `ForbiddenArts = [toolName]`; exact set equality makes that behavior test apply to every documented row without duplicating the algorithm in a documentation test.

- [ ] **Step 3: Run the focused test and verify the intended RED result**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~ToolRiskClassifierTests"
```

Expected: FAIL because the start/end markers do not yet exist in DESIGN §11.14. All pre-existing `ToolRiskClassifierTests` must remain green.

- [ ] **Step 4: Add the exact marked inventory after the §11.14 purpose paragraph**

```markdown
The standing server-tool vocabulary is below. Catalog availability may still depend on the named independent boundary; that changes whether a tool is offered or can complete, never its Ward decision.

<!-- ward-tool-inventory:start -->
| Group | Tool | Catalog status | Ward decision | Independent boundary |
|---|---|---|---|---|
| Workspace/editing | `apply_patch` | conditionally advertised | None | Workspace binding, `WorkspacePathPolicy`, Sanctum, and transactional patch validation. |
| Workspace/editing | `workspace_check` | conditionally advertised | None | Trusted workspace bytes plus eligible macOS containment and runtime chain. |
| Workspace/editing | `write_file` | conditionally advertised | None | Workspace binding, write enablement, `WorkspacePathPolicy`, and Sanctum. |
| Workspace/editing | `replace_text_block` | conditionally advertised | None | Workspace binding, write enablement, `WorkspacePathPolicy`, and Sanctum. |
| Workspace/editing | `search_workspace` | conditionally advertised | None | Workspace binding, `WorkspacePathPolicy`, and bounded traversal. |
| Workspace/editing | `read_file_chunk` | conditionally advertised | None | Workspace binding, `WorkspacePathPolicy`, Sanctum, and bounded reads. |
| Workspace/editing | `list_directory` | conditionally advertised | None | Workspace binding, `WorkspacePathPolicy`, Sanctum, and bounded listing. |
| Host process | `execute_command` | conditionally advertised | None | Development edition, explicit host-process opt-in, workspace binding, and Sanctum. |
| Host process | `run_spell_script` | conditionally advertised | None | Development edition, explicit host-process opt-in, process policy, and Sanctum. |
| Host process | `read_command_output` | conditionally advertised | None | An `execute_command` connection-lifetime output handle and bounded paging. |
| Durable memory | `delete_lexicon` | conditionally advertised | None | Lexicon availability; `ToolPolicy.NoForbiddenArts` may omit the name from advertisement. |
| Durable memory | `scribe_lexicon` | conditionally advertised | None | Lexicon availability, turn authority, and attachment provenance. |
| Durable memory | `read_saga` | conditionally advertised | None | Saga availability and resolved memory scope. |
| Durable memory | `search_archives` | conditionally advertised | None | Archive-search availability and bounded Grimoire search. |
| Covenant | `retire_covenant` | conditionally advertised | None | Exact target preflight, Campaign binding, disclosure, one-call capability, Sanctum, and publication. |
| Covenant | `propose_covenant` | conditionally advertised | None | Attended proposal eligibility, provenance, one-call capability, Sanctum, and publication. |
| Session state | `attach_session_file` | conditionally advertised | None | Attachment-tool availability, current Session binding, and materialization policy. |
| Session state | `refresh_session_file` | conditionally advertised | None | Attachment-tool availability, host-owned source identity, containment, and Session binding. |
| Orchestration | `delegate_task` | conditionally advertised | None | Registered subagent runner, orchestration coordinator limits, and delegated-task validation. |
| Orchestration | `petition_dungeon_master` | normally advertised | None | Active Apprentice context and escalation lifecycle. |
| Orchestration | `ask_human` | conditionally advertised | None | Attended human-interaction availability; unattended turns omit it. |
| Orchestration | `adjust_initiative` | normally advertised | None | Existing scheduled-job identity and bounded interval validation. |
| Messaging | `cast_sending` | conditionally advertised | None | Conclave availability and child-Apprentice orchestration limits. |
| Messaging | `continue_sending` | conditionally advertised | None | A2A client availability and an existing continuable remote task. |
| Messaging | `dispatch_sending` | conditionally advertised | None | A2A client availability, outbound URL policy, and remote protocol validation. |
| Messaging | `send_commlink_alert` | normally advertised | None | Configured Comm Link capability, outbound URL policy, and bounded payload. |
| Web | `web_search` | conditionally advertised | None | Web-browsing availability, credential readiness, and outbound URL/provider policy. |
| Web | `read_url` | conditionally advertised | None | Web-browsing availability, SSRF policy, redirect validation, and response bounds. |
| Web | `browse_web` | recognized compatibility alias | None | Canonicalized to `read_url`; never advertised by a new native catalog. |
| Host information | `get_local_system_time` | normally advertised | None | Read-only host-information implementation. |
| Host information | `get_arcanum_system_info` | normally advertised | None | Sanitized host-information projection. |
<!-- ward-tool-inventory:end -->
```

- [ ] **Step 5: Run the focused inventory and existing handler-parity contracts**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~ToolRiskClassifierTests|FullyQualifiedName~ToolsList_names_match_registered_handlers_when_all_features_enabled"
```

Expected: PASS. This keeps the 31-name cross-catalog vocabulary separate from the internal MCP server's 24 registered handlers and its conditional tools/list exclusions.

- [ ] **Step 6: Commit the inventory contract**

```bash
git add tests/RetroDownfall.Arcanum.Tests/Intelligence/ToolRiskClassifierTests.cs docs/Arcanum.DESIGN.md
git commit -m "docs: publish no-Ward tool inventory"
```

---

### Task 3: Correct live CLI help and regenerate its projection

**Files:**
- Modify: `tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Core.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Wards.cs`
- Modify: `src/RetroDownfall.Arcanum.Cli/Commands/Wards/WardCommands.cs`
- Modify: `docs/Arcanum.Command.Reference.md`
- Generate: `docs/Arcanum.CommandMap.json`

**Interfaces:**
- Consumes: `CliSurfaceTests.BuildMap() : CliSurfaceMap`, `CliSurfaceTests.Walk(CliSurfaceMap) : IEnumerable<CliSurfaceCommand>`, and live `CliSurfaceCommand.Options`.
- Produces: the exact `mcp invoke`, `ward resolve --allow`, and `ward resolve --deny` descriptions in both the live tree and generated command map.

- [ ] **Step 1: Add live-surface tests for the intended language**

Add these tests before the committed-map parity test:

```csharp
[Fact]
public void Diagnostic_mcp_invoke_help_names_master_pipeline_reservation_not_a_ward_gate()
{

    CliSurfaceCommand invoke = Walk(BuildMap()).Single(
        static command => command.Path == "mcp invoke");

    Assert.Equal(
        "Invoke one external MCP tool diagnostically; internal tool names are reserved for the Master execution pipeline.",
        invoke.Description);

    Assert.DoesNotContain("Forbidden Art", invoke.Description, StringComparison.OrdinalIgnoreCase);

    Assert.DoesNotContain("blocked server-side", invoke.Description, StringComparison.OrdinalIgnoreCase);

}

[Fact]
public void Ward_resolve_help_describes_retained_record_resolution_not_tool_admission()
{

    CliSurfaceCommand resolve = Walk(BuildMap()).Single(
        static command => command.Path == "ward resolve");

    CliSurfaceOption allow = resolve.Options.Single(
        static option => option.Name == "--allow");

    CliSurfaceOption deny = resolve.Options.Single(
        static option => option.Name == "--deny");

    Assert.Equal("Record an allowed resolution.", allow.Description);

    Assert.Equal("Record a denied resolution.", deny.Description);

    Assert.DoesNotContain("proceed", allow.Description, StringComparison.OrdinalIgnoreCase);

    Assert.DoesNotContain("tool call", deny.Description, StringComparison.OrdinalIgnoreCase);

}
```

- [ ] **Step 2: Run only the two new tests and verify RED**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Diagnostic_mcp_invoke_help_names_master_pipeline_reservation_not_a_ward_gate|FullyQualifiedName~Ward_resolve_help_describes_retained_record_resolution_not_tool_admission"
```

Expected: both tests FAIL on the current code-owned descriptions.

- [ ] **Step 3: Make the minimal code-owned description corrections**

In `CliCommandTree.Core.cs`, construct `mcp invoke` with:

```csharp
Command invoke = new(
    "invoke",
    "Invoke one external MCP tool diagnostically; internal tool names are reserved for the Master execution pipeline.");
```

In `CliCommandTree.Wards.cs`, construct the retained options with:

```csharp
Option<bool> resolveAllow = new("--allow") { Description = "Record an allowed resolution." };
Option<bool> resolveDeny = new("--deny") { Description = "Record a denied resolution." };
```

In `WardCommands.Resolve`, keep XML documentation aligned:

```csharp
/// <param name="allow">Record an allowed resolution.</param>
/// <param name="deny">Record a denied resolution.</param>
```

In `docs/Arcanum.Command.Reference.md`, change only the stale `mcp invoke` explanation to:

```markdown
Invoke one external MCP tool diagnostically; internal tool names are reserved for the Master execution pipeline.
```

The Ward table already says `Record an allowed resolution` and `Record a denied resolution`; retain it.

- [ ] **Step 4: Re-run the two behavior tests**

Run the Step 2 command again.

Expected: PASS.

- [ ] **Step 5: Prove the generated artifact is stale before regeneration**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Committed_command_map_matches_the_live_tree"
```

Expected: FAIL because the committed JSON still contains the three old descriptions.

- [ ] **Step 6: Regenerate from the live tree and prove byte parity**

```bash
ARCANUM_UPDATE_COMMAND_MAP=1 dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Committed_command_map_matches_the_live_tree"
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~Committed_command_map_matches_the_live_tree"
```

Expected: both regeneration and read-only parity PASS.

- [ ] **Step 7: Review the generated diff**

```bash
git diff -- docs/Arcanum.CommandMap.json
```

Expected: only three description values change—`mcp invoke`, `ward resolve --allow`, and `ward resolve --deny`. Any path, alias, option, argument, example, or ordering change is a blocker to investigate.

- [ ] **Step 8: Commit the live and generated contract together**

```bash
git add tests/RetroDownfall.Arcanum.Tests/Cli/CliSurfaceTests.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Core.cs src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Wards.cs src/RetroDownfall.Arcanum.Cli/Commands/Wards/WardCommands.cs docs/Arcanum.Command.Reference.md docs/Arcanum.CommandMap.json
git commit -m "docs: clarify retained Ward CLI surfaces"
```

---

### Task 4: Complete the canonical Ward semantic sweep

**Files:**
- Modify: `docs/Arcanum.DESIGN.md`
- Modify: `README.md`
- Inspect: all remaining governed documents and `src/RetroDownfall.Arcanum.Cli`

**Interfaces:**
- Consumes: the marked table contract from Task 2 and the CLI vocabulary from Task 3.
- Produces: one coherent canonical account in DESIGN §§10.14 and 11.14, with README child accounting that leaves #230 separate.

- [ ] **Step 1: Correct the Lexicon ownership statement**

Replace this stale sentence in DESIGN §10.6:

```text
`delete_lexicon` is a Forbidden Art; `scribe_lexicon` is ungated. Both follow `Arcanum:Features:Lexicon`.
```

with:

```text
Both tools follow `Arcanum:Features:Lexicon` and the record-only `ungated` Ward path. When a request selects `ToolPolicy.NoForbiddenArts`, an operator-configured `delete_lexicon` name may be omitted from advertisement; when advertised, Lexicon availability, scope, provenance, and validation remain the live boundaries.
```

- [ ] **Step 2: Make the event ordering exact**

Replace item 6 in DESIGN's typical native NDJSON sequence with:

```markdown
6. per server-executed tool: `toolCall`, required informational `warded` / `wardResolved`, optional `toolError`, then `toolResult`; client-forwarded tools run outside this server loop and produce no Ward frames;
```

This is documentation only; do not edit the execution pipeline.

- [ ] **Step 3: Clarify Covenant diagnostic routing and owner vocabulary**

In DESIGN §10.14, replace the final sentence of “Both tools are registered inert and advertised live” with:

```text
Neither name can be shadowed by an external MCP server, and both names are reserved from the external diagnostic invocation endpoint so that route cannot bypass the Master execution pipeline; their normal Master-path calls remain record-only rather than Ward-gated.
```

Change the ownership-table row to:

```markdown
| Tool schemas, availability, and staging handlers | `ArcanumInternalToolServer` Covenant partials |
```

Keep the surrounding retirement text explicit: new retirements use `CovenantAuthorizationMode.None` and a null Ward digest; proposal attendance remains proposal capability admission.

- [ ] **Step 4: Update current child accounting without declaring #221 complete early**

Replace the README heading:

```markdown
**Ward-removal status (issues #216–#219).**
```

with:

```markdown
**Ward-removal contract (issues #216–#221; issue #230 is a separate Annals follow-on).**
```

Keep the existing current-contract paragraph and its explicit supersession of historical issue snapshots. Do not edit those historical snapshots, close #197, or describe #230 as implemented.

- [ ] **Step 5: Run the governed semantic searches**

```bash
RIPGREP_CONFIG_PATH=/dev/null rg -n -i -e 'forbidden art|requires a ward|warded|auto-den' README.md docs/Arcanum.OATH.md docs/Arcanum.DESIGN.md docs/Arcanum.API.md docs/Arcanum.Command.Reference.md docs/Arcanum.Design.Human.md docs/Arcanum.DEBUGGING.Human.md docs/Arcanum.CHAT-LOOP.md docs/ArcanumOATH.Human.md docs/Compendium.README.md docs/Arcanum.ConstraintInventory.json
RIPGREP_CONFIG_PATH=/dev/null rg -n -i -e 'gated|requires approval|operator.s only chance to refuse|auto-denied|blocked Forbidden Art|blocked server-side' README.md docs/Arcanum.OATH.md docs/Arcanum.DESIGN.md docs/Arcanum.API.md docs/Arcanum.Command.Reference.md docs/Arcanum.Design.Human.md docs/Arcanum.DEBUGGING.Human.md docs/Arcanum.CHAT-LOOP.md docs/ArcanumOATH.Human.md docs/Compendium.README.md docs/Arcanum.ConstraintInventory.json src/RetroDownfall.Arcanum.Cli
```

Classify every survivor into exactly one of:

```text
record/event vocabulary
retained compatibility or historical-origin vocabulary
Forbidden Arts advertisement policy
named independent containment/capability boundary
```

Fix any survivor that cannot be classified without changing its meaning. Preserve legitimate API event names, retained resolution origins, debugging recipes, and owner-specific denials.

- [ ] **Step 6: Confirm the governed issue-reference boundary**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~DocumentationIssueReferenceTests"
```

Expected: PASS. README issue references are allowed; governed standalone documents contain none.

- [ ] **Step 7: Run the combined focused regression cluster once**

```bash
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj -c Release --no-restore --disable-build-servers -m:1 --filter "FullyQualifiedName~DocumentationIssueReferenceTests|FullyQualifiedName~ToolRiskClassifierTests|FullyQualifiedName~CliSurfaceTests|FullyQualifiedName~ToolsList_names_match_registered_handlers_when_all_features_enabled"
```

Expected: PASS. Do not substitute this focused cluster for final qualification.

- [ ] **Step 8: Review scope and commit the canonical sweep**

```bash
git diff --stat 165e23656b48c618ca67b9cdd76edb3e3256f1a3..HEAD
git diff --check
git status --short
```

Confirm no production execution, configuration, persistence, or tool-registry file changed beyond the three code-owned CLI description sources already named in Task 3.

```bash
git add README.md docs/Arcanum.OATH.md docs/Arcanum.DESIGN.md docs/Arcanum.API.md docs/Arcanum.Command.Reference.md docs/Arcanum.Design.Human.md docs/Arcanum.DEBUGGING.Human.md docs/Arcanum.CHAT-LOOP.md docs/ArcanumOATH.Human.md docs/Compendium.README.md docs/Arcanum.ConstraintInventory.json
git diff --cached --quiet || git commit -m "docs: complete Ward contract sweep"
```

Only existing files with real changes enter the commit. The guarded commit permits inspection-only files to remain untouched.

---

### Task 5: Perform one bounded independent review

**Files:**
- Review: `git diff 165e23656b48c618ca67b9cdd76edb3e3256f1a3..HEAD`
- Modify only files already in Tasks 1–4 if a verified Critical or Important finding requires it.

**Interfaces:**
- Consumes: complete feature-branch diff and approved spec.
- Produces: a read-only review report covering issue alignment, inventory accuracy, test honesty, preserved boundaries, generated provenance, and absence of runtime changes.

- [ ] **Step 1: Invoke the requesting-code-review skill**

Request one fresh reviewer with this exact scope:

```text
Review issue #221 only, comparing base 165e23656b48c618ca67b9cdd76edb3e3256f1a3 to HEAD. Check the approved design and implementation plan; exact 31-name inventory; every Ward decision None; conditional catalog and browse_web alias accuracy; Covenant retirement/proposal distinction; retained Sanctum, containment, capability, and compatibility wording; tracker-reference test coverage; CLI live/help/generated parity; historical-artifact exclusion; and whether any production behavior changed. Report only Critical, Important, or Minor findings with file and line evidence. Do not edit files and do not spawn another reviewer.
```

- [ ] **Step 2: Resolve findings with evidence**

For each Critical or Important finding:

1. Reproduce it with the narrowest applicable existing or new test/search.
2. If observable behavior is involved, add a focused failing test before the fix.
3. Apply the smallest in-scope correction.
4. Re-run only that focused gate.
5. Commit with a message naming the corrected contract.

Minor prose suggestions are accepted only when they reduce ambiguity without broadening scope. Do not start a second open-ended review loop.

- [ ] **Step 3: Record the review disposition**

Capture reviewer identity, base, HEAD, findings, and dispositions for the issue closeout. A Critical or Important finding without a verified disposition blocks Task 6.

---

### Task 6: Run the complete locally applicable qualification matrix once

**Files:**
- Inspect: `scripts/coverage.sh`
- Inspect: `scripts/verify-aot-il-warnings.sh`
- Inspect: `scripts/verify-native-sqlcipher.sh`
- Inspect: `scripts/packaging/macos/common_test.sh`
- Verify: complete reviewed feature tree

**Interfaces:**
- Consumes: reviewed HEAD from Task 5.
- Produces: one fresh, recorded set of Release, suite, coverage, static, AOT/IL, packaging, and native-provenance evidence.

- [ ] **Step 1: Reconfirm wrapper behavior before execution**

```bash
sed -n '1,220p' scripts/coverage.sh
sed -n '1,260p' scripts/verify-aot-il-warnings.sh
sed -n '1,220p' scripts/verify-native-sqlcipher.sh
sed -n '1,220p' scripts/packaging/macos/common_test.sh
```

Confirm `coverage.sh --threshold` supplies the one complete non-Perf Arcanum suite and that the AOT script publishes the current `osx-arm64` closure and runs regex smoke. Do not separately run another unfiltered Arcanum suite.

- [ ] **Step 2: Validate and clear only generated Native AOT object directories**

Validate the exact repository and generated targets:

```bash
git rev-parse --show-toplevel
test -d /Users/mat/Documents/Source/apps/RetroDownfall.Arcanum/src/RetroDownfall.Arcanum.Cli/obj/Release/net10.0/osx-arm64/native
test -d /Users/mat/Documents/Source/apps/RetroDownfall.Arcanum/tests/RetroDownfall.Arcanum.RegexAotSmoke/obj/Release/net10.0/osx-arm64/native
```

If either directory exists, request the required destructive-action approval and remove only these validated generated directories:

```bash
rm -rf -- /Users/mat/Documents/Source/apps/RetroDownfall.Arcanum/src/RetroDownfall.Arcanum.Cli/obj/Release/net10.0/osx-arm64/native
rm -rf -- /Users/mat/Documents/Source/apps/RetroDownfall.Arcanum/tests/RetroDownfall.Arcanum.RegexAotSmoke/obj/Release/net10.0/osx-arm64/native
```

If a directory is absent, record that it was already clean; never broaden the deletion target.

- [ ] **Step 3: Run the clean zero-warning Release build**

```bash
dotnet build RetroDownfall.Arcanum.slnx -c Release --no-incremental --no-restore --disable-build-servers -m:1 -warnaserror
```

Expected: exit 0, zero errors, zero warnings.

- [ ] **Step 4: Run coverage threshold and the one full Arcanum suite**

```bash
python3 -m unittest scripts/coverage_threshold_test.py
./scripts/coverage.sh --threshold
```

Expected: both PASS; retain total/pass/skip counts and reported tier thresholds from the wrapper output.

- [ ] **Step 5: Run the remaining first-party suites once**

```bash
dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj -c Release --no-restore --disable-build-servers -m:1
dotnet test tests/RetroDownfall.TheForge.Tests/RetroDownfall.TheForge.Tests.csproj -c Release --no-restore --disable-build-servers -m:1
```

Expected: both PASS. Record exact test counts.

- [ ] **Step 6: Run packaging, gate-unit, fresh AOT/IL, and native provenance checks**

```bash
./scripts/packaging/macos/common_test.sh
./scripts/verify_aot_il_warnings_test.sh
RIPGREP_CONFIG_PATH=/dev/null ./scripts/verify-aot-il-warnings.sh osx-arm64
RIPGREP_CONFIG_PATH=/dev/null ./scripts/verify-native-sqlcipher.sh --rid osx-arm64
```

Expected: all PASS. Record whether the host used full macOS Native AOT or the documented folder-publish fallback based on `lld`, plus the fresh regex-AOT smoke result and SQLCipher provenance result.

- [ ] **Step 7: Run repository static gates**

```bash
python3 scripts/align_csharp_blanklines.py --repo . --check
find scripts -name '*.sh' -print0 | xargs -0 shellcheck -x -P SCRIPTDIR
actionlint
git diff --check 165e23656b48c618ca67b9cdd76edb3e3256f1a3..HEAD
git status --short --branch
```

Expected: every command exits 0 and the worktree is clean. If a formatter changes files, inspect, commit, and re-run only the affected focused/static gate; because that creates a new reviewed tree, perform a bounded diff review before delivery rather than silently claiming the previous review covered it.

- [ ] **Step 8: Record intentionally inapplicable gates**

Closeout must state that production signing/notarization, Windows/Linux packaging lanes, operator keychain/jail integration, and #220's benchmark are not locally applicable to this documentation slice unless a current wrapper explicitly ran them. Do not claim them as passed.

---

### Task 7: Integrate, push, and close only issue #221

**Files:**
- Merge: verified `codex/issue-221-ward-documentation-sweep` into local `remove-wards`
- Delete: local `codex/issue-221-ward-documentation-sweep` after merge proof
- Push: `origin/remove-wards` only
- Update: GitHub issue #221 and its Feature Tracker item only

**Interfaces:**
- Consumes: verified feature HEAD, base `165e23656b48c618ca67b9cdd76edb3e3256f1a3`, live GitHub issue/project state.
- Produces: pushed tracked `remove-wards`, closed/Done #221, open/In Progress #197, open/Ready #230, and no temporary #221 branch.

- [ ] **Step 1: Fetch and reject unexpected remote movement**

```bash
git status --short --branch
git fetch origin main remove-wards
git rev-parse origin/main
git rev-parse origin/remove-wards
git merge-base --is-ancestor origin/main origin/remove-wards
```

Expected identities remain:

```text
origin/main         decdf011f69ab91c1e48a0d50c2bbf97cd928162
origin/remove-wards 165e23656b48c618ca67b9cdd76edb3e3256f1a3
```

If either remote moved, stop before merge/push and report the exact advance; do not silently absorb it after qualification.

- [ ] **Step 2: Capture the verified tree and merge history-only**

```bash
git rev-parse HEAD^{tree}
git switch remove-wards
git merge --no-ff codex/issue-221-ward-documentation-sweep -m "Merge issue #221 Ward documentation sweep"
git rev-parse HEAD^{tree}
git diff --exit-code codex/issue-221-ward-documentation-sweep..remove-wards
```

Expected: the pre-merge feature tree id and post-merge `remove-wards` tree id are identical, and `git diff --exit-code` exits 0. Do not repeat Task 6 on a byte-identical tree.

- [ ] **Step 3: Delete the merged temporary branch and prove cleanup**

```bash
git branch --merged remove-wards --list 'codex/issue-221-*'
git branch -d codex/issue-221-ward-documentation-sweep
git branch --list 'codex/issue-221-*'
git worktree list --porcelain
```

Expected: no #221 feature branch remains and no unrelated branch/worktree is changed.

- [ ] **Step 4: Push only the tracked aggregation branch and read it back**

```bash
git push origin remove-wards:remove-wards
git ls-remote --heads origin remove-wards 'codex/issue-221-*'
git rev-parse remove-wards
```

Expected: `origin/remove-wards` resolves to the local merge commit and no remote #221 feature ref exists.

- [ ] **Step 5: Read live issue hierarchy and project state before mutation**

```bash
gh api graphql -f query='query { repository(owner:"Retro-Downfall", name:"RetroDownfall.Arcanum") { parent: issue(number:197) { id number state title projectItems(first:20) { nodes { id project { id title } fieldValues(first:20) { nodes { ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2SingleSelectField { id name options { id name } } } } } } } } } target: issue(number:221) { id number state title parent { number } projectItems(first:20) { nodes { id project { id title } fieldValues(first:20) { nodes { ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2SingleSelectField { id name options { id name } } } } } } } } } followOn: issue(number:230) { id number state title parent { number } projectItems(first:20) { nodes { id project { id title } fieldValues(first:20) { nodes { ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2SingleSelectField { id name options { id name } } } } } } } } } } }'
```

Expected before close: #221 is open/In Progress with parent #197; #197 is open/In Progress; #230 is open/Ready with parent #197. Query #216–#220 as well; if any prerequisite has reopened or left Done, stop closeout and report it.

- [ ] **Step 6: Post evidence and close #221 as completed**

Construct one GitHub comment from the recorded evidence containing:

```text
pushed remove-wards merge commit and verified feature commit
RED/GREEN contracts added
31-name inventory and browse_web alias accounting
exact final build/test/coverage/AOT/SQLCipher/static results
canonical grep survivor classifications
review findings and dispositions
tree-identity proof and branch cleanup
#197 remains open because #230 is the separate Annals follow-on
```

Create `/private/tmp/issue-221-closeout.md` with `apply_patch`, replacing the evidence list above with the exact recorded SHAs, counts, gate results, review disposition, search classification, and #230 boundary. Inspect that file, then post it and close only #221:

```bash
gh issue comment 221 --repo Retro-Downfall/RetroDownfall.Arcanum --body-file /private/tmp/issue-221-closeout.md
gh issue close 221 --repo Retro-Downfall/RetroDownfall.Arcanum --reason completed
```

Do not invent values. Inspect the rendered comment after posting. Do not close #197 or #230.

- [ ] **Step 7: Set #221's live Feature Tracker status to Done if automation did not**

From the Step 5 response, use the #221 project-item id, its `Status` field id, and the option id whose name is exactly `Done` in this mutation:

```graphql
mutation MarkIssue221Done($project: ID!, $item: ID!, $field: ID!, $done: String!) {
  updateProjectV2ItemFieldValue(
    input: {
      projectId: $project
      itemId: $item
      fieldId: $field
      value: { singleSelectOptionId: $done }
    }
  ) {
    projectV2Item { id }
  }
}
```

Skip the mutation only if live readback already says `Done`. Resolve ids from the live response; never hard-code a stale project or option id.

- [ ] **Step 8: Perform final live readback**

Read #216–#221, #197, and #230 again through GitHub GraphQL and verify:

```text
#216–#221 CLOSED / Done
#197 OPEN / In Progress
#230 OPEN / Ready
```

Also verify `git status --short --branch` is clean on tracked `remove-wards` and `git ls-remote` matches its merge commit. Report exact SHAs and evidence; do not claim any deferred platform gate passed.
