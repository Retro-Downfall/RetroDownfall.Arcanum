using System.Text.Json;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("WorkspacePathPolicy")]
public sealed class ApplyPatchToolTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public void Parser_accepts_create_modify_rename_delete_and_normalizes_manifest()
    {

        const string patch =
            """
            diff --git "a/src/old name.txt" "b/src/new name.txt"
            similarity index 91%
            rename from "src/old name.txt"
            rename to "src/new name.txt"
            index 1111111..2222222 100644
            --- "a/src/old name.txt"
            +++ "b/src/new name.txt"
            @@ -1 +1 @@
            -old
            +new
            diff --git a/src/modify.txt b/src/modify.txt
            index 3333333..4444444 100644
            --- a/src/modify.txt
            +++ b/src/modify.txt
            @@ -1,2 +1,2 @@
             keep
            -before
            +after
            diff --git a/src/create.sh b/src/create.sh
            new file mode 100755
            index 0000000..5555555
            --- /dev/null
            +++ b/src/create.sh
            @@ -0,0 +1,2 @@
            +#!/bin/sh
            +echo created
            diff --git a/src/delete.txt b/src/delete.txt
            deleted file mode 100644
            index 6666666..0000000
            --- a/src/delete.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone
            \ No newline at end of file
            """;

        UnifiedDiffParseResult result = UnifiedDiffParser.Parse(
            patch,
            DefaultPatchSettings());

        Assert.True(result.Success, result.Message);
        UnifiedDiffManifest manifest = Assert.IsType<UnifiedDiffManifest>(result.Manifest);

        Assert.Equal(
            [
                ("src/new name.txt", UnifiedDiffOperationKind.Rename),
                ("src/modify.txt", UnifiedDiffOperationKind.Modify),
                ("src/create.sh", UnifiedDiffOperationKind.Create),
                ("src/delete.txt", UnifiedDiffOperationKind.Delete),
            ],
            manifest.Files.Select(static file => (file.ResultPath, file.Operation)));
        Assert.Equal(
            [
                "src/old name.txt",
                "src/new name.txt",
                "src/modify.txt",
                "src/create.sh",
                "src/delete.txt",
            ],
            manifest.NormalizedPaths);
        Assert.Equal(
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute,
            manifest.Files[2].NewFileUnixMode);
        Assert.True(manifest.Files[3].Hunks[0].Lines[0].OldHasNoFinalNewline);

    }

    [Theory]
    [InlineData("/absolute.txt", "absolute_path")]
    [InlineData("../parent.txt", "invalid_path")]
    [InlineData("safe/../../parent.txt", "invalid_path")]
    [InlineData("nul\0path.txt", "invalid_path")]
    public void Parser_rejects_unsafe_paths_without_a_workspace(
        string path,
        string expectedCode)
    {

        string patch =
            $"""
             --- /dev/null
             +++ {path}
             @@ -0,0 +1 @@
             +created
             """;

        UnifiedDiffParseResult result = UnifiedDiffParser.Parse(
            patch,
            DefaultPatchSettings());

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.Null(result.Manifest);

    }

    [Theory]
    [InlineData("GIT binary patch", "binary_patch")]
    [InlineData("Binary files a/a.bin and b/a.bin differ", "binary_patch")]
    [InlineData("old mode 100644\nnew mode 100755", "unsupported_metadata")]
    [InlineData("copy from source.txt\ncopy to target.txt", "unsupported_metadata")]
    [InlineData("new file mode 160000", "submodule_patch")]
    [InlineData("new file mode 120000", "symlink_patch")]
    public void Parser_rejects_binary_submodule_mode_only_and_unsupported_metadata(
        string metadata,
        string expectedCode)
    {

        string patch =
            $"""
             diff --git a/item.txt b/item.txt
             {metadata}
             --- /dev/null
             +++ b/item.txt
             @@ -0,0 +1 @@
             +created
             """;

        UnifiedDiffParseResult result = UnifiedDiffParser.Parse(
            patch,
            DefaultPatchSettings());

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);

    }

    [Fact]
    public void Parser_rejects_duplicate_destination_aliases_and_rename_cycles()
    {

        const string duplicateDestination =
            """
            --- /dev/null
            +++ b/Folder/File.txt
            @@ -0,0 +1 @@
            +first
            --- /dev/null
            +++ b/folder/file.txt
            @@ -0,0 +1 @@
            +second
            """;

        const string renameCycle =
            """
            diff --git a/a.txt b/b.txt
            similarity index 100%
            rename from a.txt
            rename to b.txt
            diff --git a/b.txt b/a.txt
            similarity index 100%
            rename from b.txt
            rename to a.txt
            """;

        UnifiedDiffParseResult duplicate = UnifiedDiffParser.Parse(
            duplicateDestination,
            DefaultPatchSettings());
        UnifiedDiffParseResult cycle = UnifiedDiffParser.Parse(
            renameCycle,
            DefaultPatchSettings());

        Assert.False(duplicate.Success);
        Assert.Equal("duplicate_destination", duplicate.Code);

        Assert.False(cycle.Success);
        Assert.Equal("rename_cycle", cycle.Code);

    }

    [Fact]
    public void Parser_rejects_duplicate_and_out_of_order_git_metadata()
    {

        const string duplicate =
            """
            diff --git a/item.txt b/item.txt
            index 1111111..2222222 100644
            index 1111111..2222222 100644
            --- a/item.txt
            +++ b/item.txt
            @@ -1 +1 @@
            -before
            +after
            """;
        const string outOfOrder =
            """
            diff --git a/old.txt b/new.txt
            similarity index 100%
            rename to new.txt
            rename from old.txt
            """;

        UnifiedDiffParseResult duplicateResult = UnifiedDiffParser.Parse(
            duplicate,
            DefaultPatchSettings());
        UnifiedDiffParseResult outOfOrderResult = UnifiedDiffParser.Parse(
            outOfOrder,
            DefaultPatchSettings());

        Assert.False(duplicateResult.Success);
        Assert.Equal("invalid_metadata", duplicateResult.Code);
        Assert.False(outOfOrderResult.Success);
        Assert.Equal("invalid_metadata", outOfOrderResult.Code);

    }

    [Fact]
    public void Parser_rejects_ancestor_descendant_path_topology_collisions()
    {

        const string patch =
            """
            --- /dev/null
            +++ b/tree
            @@ -0,0 +1 @@
            +file
            --- /dev/null
            +++ b/tree/child.txt
            @@ -0,0 +1 @@
            +child
            """;

        UnifiedDiffParseResult result = UnifiedDiffParser.Parse(
            patch,
            DefaultPatchSettings());

        Assert.False(result.Success);
        Assert.Equal("path_topology", result.Code);

    }

    [Fact]
    public void Parser_honors_cancellation_before_and_during_parse()
    {

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => UnifiedDiffParser.Parse(
                """
                --- /dev/null
                +++ b/file.txt
                @@ -0,0 +1 @@
                +value
                """,
                DefaultPatchSettings(),
                cancellation.Token));

    }

    [Fact]
    public void Parser_enforces_patch_file_hunk_and_line_caps()
    {

        const string patch =
            """
            --- a/file.txt
            +++ b/file.txt
            @@ -1 +1 @@
            -old
            +new
            """;

        Assert.Equal(
            "patch_too_large",
            UnifiedDiffParser.Parse(
                patch,
                DefaultPatchSettings() with { MaxPatchBytes = 8 }).Code);
        Assert.Equal(
            "max_files",
            UnifiedDiffParser.Parse(
                patch + "\n" + patch,
                DefaultPatchSettings() with { MaxFiles = 1 }).Code);
        Assert.Equal(
            "max_hunks",
            UnifiedDiffParser.Parse(
                patch,
                DefaultPatchSettings() with { MaxHunks = 0 }).Code);
        Assert.Equal(
            "max_lines_per_hunk",
            UnifiedDiffParser.Parse(
                patch,
                DefaultPatchSettings() with { MaxLinesPerHunk = 1 }).Code);

    }

    [Fact]
    public async Task Planner_enforces_per_file_and_aggregate_input_output_and_staging_caps()
    {
        static string ModifyPatch(
            string path,
            string before,
            string after) =>
            $"""
            --- a/{path}
            +++ b/{path}
            @@ -1 +1 @@
            -{before}
            +{after}
            """;

        string inputFileBefore = new('i', 1_024);
        string inputTotalBefore = new('t', 600);
        string outputFileAfter = new('o', 1_024);
        string outputTotalAfter = new('u', 600);
        string stagingFileBefore = new('s', 600);
        string stagingFileAfter = new('r', 600);
        string stagingTotalBefore = new('g', 300);
        string stagingTotalAfter = new('h', 300);

        _workspace.WriteFile("input-file.txt", inputFileBefore + "\n");
        _workspace.WriteFile("input-total-a.txt", inputTotalBefore + "\n");
        _workspace.WriteFile("input-total-b.txt", inputTotalBefore + "\n");
        _workspace.WriteFile("output-file.txt", "before\n");
        _workspace.WriteFile("output-total-a.txt", "before\n");
        _workspace.WriteFile("output-total-b.txt", "before\n");
        _workspace.WriteFile("staging-file.txt", stagingFileBefore + "\n");
        _workspace.WriteFile("staging-total-a.txt", stagingTotalBefore + "\n");
        _workspace.WriteFile("staging-total-b.txt", stagingTotalBefore + "\n");

        UnifiedDiffManifest inputFile = ParseManifest(
            ModifyPatch("input-file.txt", inputFileBefore, "after"));
        UnifiedDiffManifest inputTotal = ParseManifest(
            ModifyPatch("input-total-a.txt", inputTotalBefore, "after")
            + "\n"
            + ModifyPatch("input-total-b.txt", inputTotalBefore, "after"));
        UnifiedDiffManifest outputFile = ParseManifest(
            ModifyPatch("output-file.txt", "before", outputFileAfter));
        UnifiedDiffManifest outputTotal = ParseManifest(
            ModifyPatch("output-total-a.txt", "before", outputTotalAfter)
            + "\n"
            + ModifyPatch("output-total-b.txt", "before", outputTotalAfter));
        UnifiedDiffManifest stagingFile = ParseManifest(
            ModifyPatch(
                "staging-file.txt",
                stagingFileBefore,
                stagingFileAfter));
        UnifiedDiffManifest stagingTotal = ParseManifest(
            ModifyPatch(
                "staging-total-a.txt",
                stagingTotalBefore,
                stagingTotalAfter)
            + "\n"
            + ModifyPatch(
                "staging-total-b.txt",
                stagingTotalBefore,
                stagingTotalAfter));

        async Task<string?> FailureCodeAsync(
            UnifiedDiffManifest manifest,
            WorkspacePatchSettings settings)
        {

            WorkspacePatchPlanResult result = await new WorkspacePatchPlanner(
                settings)
                .PlanAsync(
                    _workspace.Root,
                    manifest,
                    CancellationToken.None);

            Assert.False(result.Success);

            return result.Code;

        }

        WorkspacePatchSettings defaults = DefaultPatchSettings();

        Assert.Equal(
            "input_file_too_large",
            await FailureCodeAsync(
                inputFile,
                defaults with { MaxInputBytesPerFile = 1_024 }));

        Assert.Equal(
            "input_total_too_large",
            await FailureCodeAsync(
                inputTotal,
                defaults with { MaxTotalInputBytes = 1_024 }));

        Assert.Equal(
            "output_file_too_large",
            await FailureCodeAsync(
                outputFile,
                defaults with { MaxOutputBytesPerFile = 1_024 }));

        Assert.Equal(
            "output_total_too_large",
            await FailureCodeAsync(
                outputTotal,
                defaults with { MaxTotalOutputBytes = 1_024 }));

        Assert.Equal(
            "staging_file_too_large",
            await FailureCodeAsync(
                stagingFile,
                defaults with { MaxStagingBytesPerFile = 1_024 }));

        Assert.Equal(
            "staging_total_too_large",
            await FailureCodeAsync(
                stagingTotal,
                defaults with { MaxTotalStagingBytes = 1_024 }));

        Assert.Equal(
            inputFileBefore + "\n",
            await ReadTextAsync("input-file.txt"));
        Assert.Equal(
            stagingTotalBefore + "\n",
            await ReadTextAsync("staging-total-b.txt"));

    }

    [Fact]
    public async Task Planner_validates_every_file_and_builds_one_reversible_transaction()
    {

        _workspace.WriteFile("modify.txt", "keep\nbefore\n");
        _workspace.WriteFile("rename-old.txt", "rename before\n");
        _workspace.WriteFile("delete.txt", "gone");

        UnifiedDiffManifest manifest = ParseManifest(
            """
            diff --git a/modify.txt b/modify.txt
            --- a/modify.txt
            +++ b/modify.txt
            @@ -1,2 +1,2 @@
             keep
            -before
            +after
            diff --git a/rename-old.txt b/rename-new.txt
            similarity index 80%
            rename from rename-old.txt
            rename to rename-new.txt
            --- a/rename-old.txt
            +++ b/rename-new.txt
            @@ -1 +1 @@
            -rename before
            +rename after
            diff --git a/create.txt b/create.txt
            new file mode 100644
            --- /dev/null
            +++ b/create.txt
            @@ -0,0 +1 @@
            +created
            diff --git a/delete.txt b/delete.txt
            deleted file mode 100644
            --- a/delete.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone
            \ No newline at end of file
            """);

        WorkspacePatchPlanResult planning = await new WorkspacePatchPlanner(
            DefaultPatchSettings())
            .PlanAsync(
                _workspace.Root,
                manifest,
                CancellationToken.None);

        Assert.True(planning.Success, planning.Message);
        WorkspacePatchPlan plan = Assert.IsType<WorkspacePatchPlan>(planning.Plan);

        Assert.Equal(manifest.NormalizedPaths, plan.NormalizedPaths);
        Assert.Equal(5, plan.CommitOperations.Count);
        Assert.Equal(4, plan.Files.Count);
        Assert.All(plan.Files, static file => Assert.Equal(1, file.AppliedHunks));

        Assert.Equal("keep\nbefore\n", await ReadTextAsync("modify.txt"));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "create.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "rename-new.txt")));

        WorkspaceCommitResult commit = await new MultiFileCommitCoordinator(
            _workspace.Root)
            .CommitAsync(
                plan.CommitOperations,
                CancellationToken.None);

        Assert.Equal(WorkspaceCommitStatus.Committed, commit.Status);
        Assert.Equal("keep\nafter\n", await ReadTextAsync("modify.txt"));
        Assert.Equal("rename after\n", await ReadTextAsync("rename-new.txt"));
        Assert.Equal("created\n", await ReadTextAsync("create.txt"));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "rename-old.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "delete.txt")));

        WorkspaceRollbackResult rollback =
            await commit.Transaction!.RollbackAsync(CancellationToken.None);

        Assert.True(rollback.Complete);
        Assert.Equal("keep\nbefore\n", await ReadTextAsync("modify.txt"));
        Assert.Equal("rename before\n", await ReadTextAsync("rename-old.txt"));
        Assert.Equal("gone", await ReadTextAsync("delete.txt"));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "create.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "rename-new.txt")));

    }

    [Fact]
    public async Task Planner_preserves_bom_unicode_mixed_untouched_delimiters_and_no_final_newline()
    {

        string path = Path.Combine(_workspace.Root, "mixed.txt");
        byte[] original =
        [
            0xEF, 0xBB, 0xBF,
            .. System.Text.Encoding.UTF8.GetBytes(
                "one\r\nmatch ✨\nthree\rfour"),
        ];
        await File.WriteAllBytesAsync(path, original);

        UnifiedDiffManifest manifest = ParseManifest(
            """
            --- a/mixed.txt
            +++ b/mixed.txt
            @@ -1,3 +1,3 @@
             one
            -match ✨
            +changed ✨
             three
            @@ -4 +4 @@
            -four
            \ No newline at end of file
            +FOUR
            \ No newline at end of file
            """);

        WorkspacePatchPlanResult planning = await new WorkspacePatchPlanner(
            DefaultPatchSettings())
            .PlanAsync(
                _workspace.Root,
                manifest,
                CancellationToken.None);

        Assert.True(planning.Success, planning.Message);
        WorkspaceFileCommitOperation operation =
            Assert.Single(planning.Plan!.CommitOperations);

        Assert.Equal(
            [
                0xEF, 0xBB, 0xBF,
                .. System.Text.Encoding.UTF8.GetBytes(
                    "one\r\nchanged ✨\r\nthree\rFOUR"),
            ],
            operation.OutputBytes!.Value.ToArray());
        Assert.Equal(original, await File.ReadAllBytesAsync(path));

    }

    [Fact]
    public async Task Planner_relocates_only_a_unique_best_match_with_exact_deletions()
    {

        _workspace.WriteFile(
            "relocate.txt",
            "zero\ncontext   one\ndelete-me\ncontext\ttwo\nlast\n");

        UnifiedDiffManifest manifest = ParseManifest(
            """
            --- a/relocate.txt
            +++ b/relocate.txt
            @@ -20,3 +20,3 @@
             context one
            -delete-me
            +replacement
             context two
            """);

        WorkspacePatchPlanResult result = await new WorkspacePatchPlanner(
            DefaultPatchSettings() with { FuzzyMatchWindowLines = 25 })
            .PlanAsync(
                _workspace.Root,
                manifest,
                CancellationToken.None);

        Assert.True(result.Success, result.Message);
        WorkspacePatchPlannedFile file = Assert.Single(result.Plan!.Files);

        Assert.Equal(1, file.RelocatedHunks);
        Assert.Equal(2, file.MatchedLines[0]);
        WorkspacePatchHunkDiagnostic diagnostic = Assert.Single(file.Hunks);
        Assert.Equal("relocate.txt", diagnostic.Path);
        Assert.Equal(1, diagnostic.Ordinal);
        Assert.Equal("@@ -20,3 +20,3 @@", diagnostic.Header);
        Assert.Equal(20, diagnostic.ExpectedLine);
        Assert.Equal(2, diagnostic.MatchedLine);
        Assert.True(diagnostic.Relocated);
        Assert.Equal([2], diagnostic.CandidateLines);
        Assert.Equal(
            "zero\ncontext   one\nreplacement\ncontext\ttwo\nlast\n",
            System.Text.Encoding.UTF8.GetString(
                result.Plan.CommitOperations[0].OutputBytes!.Value.Span));

        _workspace.WriteFile(
            "relocate.txt",
            "zero\ncontext   one\ndelete me\ncontext\ttwo\nlast\n");

        WorkspacePatchPlanResult deletionMismatch = await new WorkspacePatchPlanner(
            DefaultPatchSettings() with { FuzzyMatchWindowLines = 25 })
            .PlanAsync(
                _workspace.Root,
                manifest,
                CancellationToken.None);

        Assert.False(deletionMismatch.Success);
        Assert.Equal("hunk_conflict", deletionMismatch.Code);

    }

    [Fact]
    public async Task Planner_rejects_tied_fuzzy_matches_and_respects_the_window()
    {

        _workspace.WriteFile(
            "ambiguous.txt",
            "start\ncontext\ndelete\nmiddle\nother\ncontext\ndelete\nend\n");

        UnifiedDiffManifest manifest = ParseManifest(
            """
            --- a/ambiguous.txt
            +++ b/ambiguous.txt
            @@ -4,2 +4,2 @@
             context
            -delete
            +replacement
            """);

        WorkspacePatchPlanResult ambiguous = await new WorkspacePatchPlanner(
            DefaultPatchSettings() with { FuzzyMatchWindowLines = 10 })
            .PlanAsync(
                _workspace.Root,
                manifest,
                CancellationToken.None);
        WorkspacePatchPlanResult bounded = await new WorkspacePatchPlanner(
            DefaultPatchSettings() with { FuzzyMatchWindowLines = 1 })
            .PlanAsync(
                _workspace.Root,
                manifest,
                CancellationToken.None);

        Assert.False(ambiguous.Success);
        Assert.Equal("ambiguous_hunk", ambiguous.Code);
        WorkspacePatchHunkDiagnostic diagnostic =
            Assert.IsType<WorkspacePatchHunkDiagnostic>(ambiguous.Diagnostic);
        Assert.Equal("ambiguous.txt", diagnostic.Path);
        Assert.Equal(1, diagnostic.Ordinal);
        Assert.Equal("@@ -4,2 +4,2 @@", diagnostic.Header);
        Assert.Equal(4, diagnostic.ExpectedLine);
        Assert.Null(diagnostic.MatchedLine);
        Assert.False(diagnostic.Relocated);
        Assert.Equal([2, 6], diagnostic.CandidateLines);
        Assert.False(bounded.Success);
        Assert.Equal("hunk_conflict", bounded.Code);

    }

    [Fact]
    public async Task Planner_honors_cancellation_during_fuzzy_candidate_scan()
    {

        string[] lines = Enumerable.Range(0, 256)
            .Select(static index => index == 180 ? "needle" : $"line-{index}")
            .ToArray();
        _workspace.WriteFile("cancel-fuzzy.txt", string.Join('\n', lines) + "\n");
        using CancellationTokenSource cancellation = new();
        int checkpoints = 0;
        WorkspacePatchPlanner planner = new(
            DefaultPatchSettings() with { FuzzyMatchWindowLines = 256 },
            new WorkspacePatchPlannerOptions
            {
                FuzzyMatchCheckpoint = () =>
                {
                    checkpoints++;
                    cancellation.Cancel();
                },
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => planner.PlanAsync(
                _workspace.Root,
                ParseManifest(
                    """
                    --- a/cancel-fuzzy.txt
                    +++ b/cancel-fuzzy.txt
                    @@ -1 +1 @@
                    -needle
                    +replacement
                    """),
                cancellation.Token));

        Assert.True(checkpoints > 0);
        Assert.Equal(string.Join('\n', lines) + "\n", await ReadTextAsync("cancel-fuzzy.txt"));

    }

    [Fact]
    public async Task Planner_honors_cancellation_before_exact_hunk_match()
    {

        _workspace.WriteFile("cancel-exact.txt", "before\n");

        using CancellationTokenSource cancellation = new();

        WorkspacePatchPlanner planner = new(
            DefaultPatchSettings(),
            new WorkspacePatchPlannerOptions
            {
                ExactMatchCheckpoint = cancellation.Cancel,
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => planner.PlanAsync(
                _workspace.Root,
                ParseManifest(
                    """
                    --- a/cancel-exact.txt
                    +++ b/cancel-exact.txt
                    @@ -1 +1 @@
                    -before
                    +after
                    """),
                cancellation.Token));

        Assert.Equal("before\n", await ReadTextAsync("cancel-exact.txt"));

    }

    [Fact]
    public async Task Planner_enforces_monotonic_absolute_deadline()
    {

        _workspace.WriteFile("deadline.txt", "before\n");

        WorkspacePatchPlanResult result = await new WorkspacePatchPlanner(
            DefaultPatchSettings() with
            {
                MaxElapsedMilliseconds = 100,
                RollbackReserveMilliseconds = 25,
            },
            new WorkspacePatchPlannerOptions
            {
                TimeProvider = new IncrementingPatchTimeProvider(),
            })
            .PlanAsync(
                _workspace.Root,
                ParseManifest(
                    """
                    --- a/deadline.txt
                    +++ b/deadline.txt
                    @@ -1 +1 @@
                    -before
                    +after
                    """),
                CancellationToken.None);

        Assert.False(result.Success);

        Assert.Equal("max_elapsed", result.Code);

        Assert.Equal("before\n", await ReadTextAsync("deadline.txt"));

    }

    [Fact]
    public async Task Planner_clamps_extreme_resource_settings_at_entry()
    {
        WorkspacePatchSettings settings = new()
        {
            MaxInputBytesPerFile = long.MinValue,
            MaxTotalInputBytes = long.MinValue,
            MaxOutputBytesPerFile = long.MinValue,
            MaxTotalOutputBytes = long.MinValue,
            MaxStagingBytesPerFile = long.MinValue,
            MaxTotalStagingBytes = long.MinValue,
            MaxElapsedMilliseconds = int.MaxValue,
            RollbackReserveMilliseconds = int.MinValue,
            FuzzyMatchWindowLines = int.MinValue,
        };

        WorkspacePatchPlanResult result = await new WorkspacePatchPlanner(
            settings,
            new WorkspacePatchPlannerOptions
            {
                TimeProvider = new ManualPatchTimeProvider(),
            })
            .PlanAsync(
                _workspace.Root,
                ParseManifest(
                    """
                    --- /dev/null
                    +++ b/extreme-settings.txt
                    @@ -0,0 +1 @@
                    +value
                    """),
                CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            "value\n",
            System.Text.Encoding.UTF8.GetString(
                Assert.Single(result.Plan!.CommitOperations).OutputBytes!.Value.Span));
    }

    [Fact]
    public async Task Planner_snapshots_mutable_settings_at_construction()
    {
        WorkspacePatchSettings settings = DefaultPatchSettings();
        WorkspacePatchPlanner planner = new(settings);

        settings.MaxOutputBytesPerFile = 0;
        settings.MaxTotalOutputBytes = 0;
        settings.MaxStagingBytesPerFile = 0;
        settings.MaxTotalStagingBytes = 0;

        WorkspacePatchPlanResult result = await planner.PlanAsync(
            _workspace.Root,
            ParseManifest(
                """
                --- /dev/null
                +++ b/snapshot-settings.txt
                @@ -0,0 +1 @@
                +value
                """),
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task Planner_normalizes_extreme_deadline_relation_before_budgeting()
    {
        WorkspacePatchPlanResult result = await new WorkspacePatchPlanner(
            DefaultPatchSettings() with
            {
                MaxElapsedMilliseconds = int.MaxValue,
                RollbackReserveMilliseconds = int.MinValue,
            },
            new WorkspacePatchPlannerOptions
            {
                TimeProvider = new IncrementingPatchTimeProvider(),
            })
            .PlanAsync(
                _workspace.Root,
                ParseManifest(
                    """
                    --- /dev/null
                    +++ b/relational-deadline.txt
                    @@ -0,0 +1 @@
                    +value
                    """),
                CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task Executor_deadline_starts_before_receipt_probe_and_reserves_rollback_time()
    {

        _workspace.WriteFile("absolute-deadline.txt", "before\n");

        ManualPatchTimeProvider time = new();

        AdvancingProbeReceiptSink sink = new(
            time,
            TimeSpan.FromMilliseconds(80));

        ApplyPatchToolExecutionResponse response = await CreateExecutor(
            settings: DefaultPatchSettings() with
            {
                MaxElapsedMilliseconds = 100,
                RollbackReserveMilliseconds = 25,
            },
            timeProvider: time)
            .ExecuteAsync(
                ModifyRequest("absolute-deadline.txt", "before", "after"),
                InvocationContext(sink),
                CancellationToken.None);

        Assert.Equal("max_elapsed", ResultCode(response));

        Assert.Equal(
            "before\n",
            await ReadTextAsync("absolute-deadline.txt"));

        Assert.False(sink.PreflightCalled);

    }

    [Fact]
    public async Task Executor_normalizes_extreme_deadline_relation_before_starting_timers()
    {
        _workspace.WriteFile("normalized-deadline.txt", "before\n");

        ManualPatchTimeProvider time = new();
        AdvancingSuccessfulReceiptSink sink = new(
            time,
            TimeSpan.FromMilliseconds(80));

        ApplyPatchToolExecutionResponse response = await CreateExecutor(
            settings: DefaultPatchSettings() with
            {
                MaxElapsedMilliseconds = int.MaxValue,
                RollbackReserveMilliseconds = int.MinValue,
            },
            timeProvider: time)
            .ExecuteAsync(
                ModifyRequest("normalized-deadline.txt", "before", "after"),
                InvocationContext(sink),
                CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(
            response.SerializedResult);

        Assert.Equal(
            "ok",
            payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "after\n",
            await ReadTextAsync("normalized-deadline.txt"));
    }

    [Fact]
    public async Task Planner_dry_plan_captures_fingerprints_without_creating_destinations()
    {

        UnifiedDiffManifest manifest = ParseManifest(
            """
            --- /dev/null
            +++ b/new/deep/file.txt
            @@ -0,0 +1 @@
            +created
            """);

        WorkspacePatchPlanResult result = await new WorkspacePatchPlanner(
            DefaultPatchSettings())
            .PlanAsync(
                _workspace.Root,
                manifest,
                CancellationToken.None);

        Assert.True(result.Success, result.Message);
        WorkspaceFileCommitOperation operation =
            Assert.Single(result.Plan!.CommitOperations);

        Assert.False(operation.ExpectedFingerprint.Exists);
        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "new")));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "new", "deep", "file.txt")));

    }

    [Fact]
    public async Task Planner_rejects_binary_symlink_and_hard_link_mutation_targets()
    {

        await File.WriteAllBytesAsync(
            Path.Combine(_workspace.Root, "binary.txt"),
            [0x61, 0x00, 0x62]);
        _workspace.WriteFile("linked.txt", "before\n");

        string outsideAlias = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-patch-link-{Guid.NewGuid():N}.txt");

        try
        {
            Assert.True(HardLinkTestSupport.TryCreate(
                outsideAlias,
                Path.Combine(_workspace.Root, "linked.txt")));

            WorkspacePatchPlanResult binary = await PlanSingleModifyAsync(
                "binary.txt",
                "a",
                "b");
            WorkspacePatchPlanResult linked = await PlanSingleModifyAsync(
                "linked.txt",
                "before",
                "after");

            Assert.False(binary.Success);
            Assert.Equal("binary_file", binary.Code);
            Assert.False(linked.Success);
            Assert.Equal("hard_link", linked.Code);
        }
        finally
        {
            File.Delete(outsideAlias);
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            _workspace.WriteFile("target.txt", "before\n");
            File.CreateSymbolicLink(
                Path.Combine(_workspace.Root, "alias.txt"),
                Path.Combine(_workspace.Root, "target.txt"));

            WorkspacePatchPlanResult symlink = await PlanSingleModifyAsync(
                "alias.txt",
                "before",
                "after");

            Assert.False(symlink.Success);
            Assert.Equal("symlink", symlink.Code);
        }

    }

    [Fact]
    public async Task Planner_fails_closed_when_source_path_is_replaced_after_handle_open()
    {

        string target = Path.Combine(_workspace.Root, "race.txt");
        string displaced = Path.Combine(_workspace.Root, "race.displaced.txt");
        await File.WriteAllTextAsync(target, "before\n");
        bool replaced = false;
        WorkspacePatchPlanner planner = new(
            DefaultPatchSettings(),
            new WorkspacePatchPlannerOptions
            {
                AfterSourceHandleOpened = relativePath =>
                {
                    Assert.Equal("race.txt", relativePath);
                    File.Move(target, displaced);
                    File.WriteAllText(target, "before\n");
                    replaced = true;
                },
            });

        WorkspacePatchPlanResult result = await planner.PlanAsync(
            _workspace.Root,
            ParseManifest(
                """
                --- a/race.txt
                +++ b/race.txt
                @@ -1 +1 @@
                -before
                +after
                """),
            CancellationToken.None);

        Assert.True(replaced);
        Assert.False(result.Success);
        Assert.Equal("concurrent_edit", result.Code);
        Assert.Equal("before\n", await File.ReadAllTextAsync(target));
        Assert.Equal("before\n", await File.ReadAllTextAsync(displaced));

    }

    [Fact]
    public async Task Planner_fails_closed_when_source_becomes_symlink_after_handle_open()
    {

        string target = Path.Combine(_workspace.Root, "symlink-race.txt");
        string displaced = Path.Combine(
            _workspace.Root,
            "symlink-race.displaced.txt");
        string replacement = Path.Combine(
            _workspace.Root,
            "symlink-race.replacement.txt");
        await File.WriteAllTextAsync(target, "before\n");
        await File.WriteAllTextAsync(replacement, "before\n");
        bool replaced = false;
        WorkspacePatchPlanner planner = new(
            DefaultPatchSettings(),
            new WorkspacePatchPlannerOptions
            {
                AfterSourceHandleOpened = _ =>
                {
                    File.Move(target, displaced);
                    try
                    {
                        File.CreateSymbolicLink(target, replacement);
                        replaced = true;
                    }
                    catch (Exception exception) when (
                        exception is UnauthorizedAccessException
                            or PlatformNotSupportedException
                            or IOException)
                    {
                        File.Move(displaced, target);
                    }
                },
            });

        WorkspacePatchPlanResult result = await planner.PlanAsync(
            _workspace.Root,
            ParseManifest(
                """
                --- a/symlink-race.txt
                +++ b/symlink-race.txt
                @@ -1 +1 @@
                -before
                +after
                """),
            CancellationToken.None);

        if (!replaced)
        {
            return;
        }

        Assert.False(result.Success);
        Assert.Contains(result.Code, new[] { "symlink", "concurrent_edit" });
        Assert.Equal("before\n", await File.ReadAllTextAsync(target));
        Assert.Equal("before\n", await File.ReadAllTextAsync(displaced));

    }

    [Fact]
    public async Task Planner_rejects_a_partial_multi_file_hunk_set_before_mutation()
    {

        _workspace.WriteFile("good.txt", "before\n");
        _workspace.WriteFile("bad.txt", "different\n");

        UnifiedDiffManifest manifest = ParseManifest(
            """
            --- a/good.txt
            +++ b/good.txt
            @@ -1 +1 @@
            -before
            +after
            --- a/bad.txt
            +++ b/bad.txt
            @@ -1 +1 @@
            -expected
            +after
            """);

        WorkspacePatchPlanResult result = await new WorkspacePatchPlanner(
            DefaultPatchSettings())
            .PlanAsync(
                _workspace.Root,
                manifest,
                CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("hunk_conflict", result.Code);
        Assert.Null(result.Plan);
        Assert.Equal("before\n", await ReadTextAsync("good.txt"));
        Assert.Equal("different\n", await ReadTextAsync("bad.txt"));
        Assert.Empty(
            Directory.GetFiles(
                _workspace.Root,
                "*.arcanum-*",
                SearchOption.AllDirectories));

    }

    [Fact]
    public async Task Executor_dry_run_plans_and_fingerprints_without_mutation_or_receipt()
    {

        RecordingPendingReceiptSink sink = new();
        ApplyPatchToolExecutionService executor = CreateExecutor();
        ApplyPatchParams request = new(
            """
            --- /dev/null
            +++ b/new/deep/dry-run.txt
            @@ -0,0 +1 @@
            +planned
            """,
            DryRun: true);

        ApplyPatchToolExecutionResponse response = await executor.ExecuteAsync(
            request,
            InvocationContext(sink),
            CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(response.SerializedResult);
        Assert.Equal("dry_run", payload.RootElement.GetProperty("status").GetString());
        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "new")));
        Assert.Empty(sink.Receipts);

    }

    [Fact]
    public async Task Executor_applies_create_modify_delete_rename_and_new_file_mode_end_to_end()
    {

        _workspace.WriteFile("modify.txt", "before\n");
        _workspace.WriteFile("delete.txt", "gone\n");
        _workspace.WriteFile("rename-old.txt", "old\n");
        OutcomePendingReceiptSink sink = new(
            MandatoryToolInteractionAppendOutcome.NewlyCommitted);
        ApplyPatchParams request = new(
            """
            diff --git a/create.sh b/create.sh
            new file mode 100755
            --- /dev/null
            +++ b/create.sh
            @@ -0,0 +1,2 @@
            +#!/bin/sh
            +echo created
            --- a/modify.txt
            +++ b/modify.txt
            @@ -1 +1 @@
            -before
            +after
            --- a/delete.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -gone
            diff --git a/rename-old.txt b/rename-new.txt
            similarity index 75%
            rename from rename-old.txt
            rename to rename-new.txt
            --- a/rename-old.txt
            +++ b/rename-new.txt
            @@ -1 +1 @@
            -old
            +new
            """,
            DryRun: false);

        ApplyPatchToolExecutionResponse response = await CreateExecutor()
            .ExecuteAsync(
                request,
                InvocationContext(
                    sink,
                    serializedArguments: JsonSerializer.Serialize(
                        request,
                        McpJsonSerializerContext.Default.ApplyPatchParams)),
                CancellationToken.None);

        Assert.Equal("#!/bin/sh\necho created\n", await ReadTextAsync("create.sh"));
        Assert.Equal("after\n", await ReadTextAsync("modify.txt"));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "delete.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace.Root, "rename-old.txt")));
        Assert.Equal("new\n", await ReadTextAsync("rename-new.txt"));
        using JsonDocument payload = JsonDocument.Parse(response.SerializedResult);
        Assert.Equal(
            ["create", "modify", "delete", "rename"],
            payload.RootElement.GetProperty("files")
                .EnumerateArray()
                .Select(static file =>
                    file.GetProperty("operation").GetString()));

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(
                Path.Combine(_workspace.Root, "create.sh"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        }

    }

    [Fact]
    public async Task Executor_commits_then_hands_off_one_immutable_result_while_still_reversible()
    {

        _workspace.WriteFile("pending.txt", "before\n");
        RecordingPendingReceiptSink sink = new();
        ApplyPatchParams request = ModifyRequest(
            "pending.txt",
            "before",
            "after");
        string exactArgumentsSnapshot =
            "{\"dryRun\":false,\"patch\":"
            + JsonSerializer.Serialize(request.Patch)
            + "}";
        ApplyPatchInvocationContext context = InvocationContext(
            sink,
            serializedArguments: exactArgumentsSnapshot);

        ApplyPatchToolExecutionResponse response = await CreateExecutor()
            .ExecuteAsync(
                request,
                context,
                CancellationToken.None);

        PendingApplyPatchReceipt pending = Assert.Single(sink.Receipts);
        ToolInteractionReceipt expectedReceipt =
            ToolInteractionReceiptDerivation.Derive(context.Identity);

        Assert.Equal(expectedReceipt, pending.Receipt);
        Assert.Equal(context.SessionId, pending.SessionId);
        Assert.Equal(response.SerializedResult, pending.SerializedResult);
        Assert.Equal(exactArgumentsSnapshot, pending.SerializedArguments);
        Assert.Equal("after\n", await ReadTextAsync("pending.txt"));
        Assert.NotEmpty(pending.Recovery!.ArtifactPaths);
        Assert.All(pending.Recovery.ArtifactPaths, AssertRelativeRecoveryPath);

        WorkspaceRollbackResult rollback =
            await pending.RollbackAsync(CancellationToken.None);

        Assert.True(rollback.Complete);
        Assert.Equal("before\n", await ReadTextAsync("pending.txt"));

    }

    [Fact]
    public async Task Executor_replays_exact_committed_receipt_before_parsing_or_mutation()
    {

        const string committedResult =
            """{"status":"ok","files":[],"totalFileCount":0,"omittedFileCount":0,"affectedPaths":[],"totalAffectedPathCount":0,"omittedAffectedPathCount":0,"recoveryArtifactPaths":[],"totalRecoveryArtifactPathCount":0,"omittedRecoveryArtifactPathCount":0,"truncated":false}""";

        PreflightPendingReceiptSink sink = new(
            new ApplyPatchReceiptProbeResult(
                ApplyPatchReceiptProbeOutcome.Replayed,
                committedResult),
            new ApplyPatchReceiptPreflightResult(
                ApplyPatchReceiptPreflightOutcome.Admitted,
                SerializedResult: null));

        bool coordinatorCreated = false;

        ApplyPatchToolExecutionResponse response = await CreateExecutor(
            coordinatorFactory: root =>
            {

                coordinatorCreated = true;

                return new MultiFileCommitCoordinator(root);

            })
            .ExecuteAsync(
                new ApplyPatchParams("not a patch", DryRun: false),
                InvocationContext(sink),
                CancellationToken.None);

        Assert.Equal(committedResult, response.SerializedResult);

        Assert.False(coordinatorCreated);

        Assert.Equal(1, sink.ProbeCount);

        Assert.Equal(0, sink.PreflightCount);

        Assert.Empty(sink.Receipts);

    }

    [Fact]
    public async Task Executor_rejects_mismatched_retry_payload_before_planning()
    {

        _workspace.WriteFile("retry.txt", "before\n");

        PreflightPendingReceiptSink sink = new(
            new ApplyPatchReceiptProbeResult(
                ApplyPatchReceiptProbeOutcome.Mismatched,
                SerializedResult: null),
            new ApplyPatchReceiptPreflightResult(
                ApplyPatchReceiptPreflightOutcome.Admitted,
                SerializedResult: null));

        ApplyPatchToolExecutionResponse response = await CreateExecutor()
            .ExecuteAsync(
                ModifyRequest("retry.txt", "before", "after"),
                InvocationContext(sink),
                CancellationToken.None);

        Assert.Equal("receipt_mismatch", ResultCode(response));

        Assert.Equal("before\n", await ReadTextAsync("retry.txt"));

        Assert.Equal(1, sink.ProbeCount);

        Assert.Equal(0, sink.PreflightCount);

        Assert.Empty(sink.Receipts);

    }

    [Fact]
    public async Task Executor_preflights_exact_receipt_capacity_before_commit()
    {

        _workspace.WriteFile("capacity.txt", "before\n");

        PreflightPendingReceiptSink sink = new(
            new ApplyPatchReceiptProbeResult(
                ApplyPatchReceiptProbeOutcome.NotFound,
                SerializedResult: null),
            new ApplyPatchReceiptPreflightResult(
                ApplyPatchReceiptPreflightOutcome.Rejected,
                SerializedResult: null));

        bool coordinatorCreated = false;

        ApplyPatchToolExecutionResponse response = await CreateExecutor(
            coordinatorFactory: root =>
            {

                coordinatorCreated = true;

                return new MultiFileCommitCoordinator(root);

            })
            .ExecuteAsync(
                ModifyRequest("capacity.txt", "before", "after"),
                InvocationContext(sink),
                CancellationToken.None);

        Assert.Equal("receipt_capacity", ResultCode(response));

        Assert.Equal("before\n", await ReadTextAsync("capacity.txt"));

        Assert.False(coordinatorCreated);

        Assert.Equal(1, sink.PreflightCount);

        Assert.NotNull(sink.Preflight);

        using JsonDocument exactResult = JsonDocument.Parse(
            sink.Preflight!.SerializedResult);

        Assert.Equal(
            "ok",
            exactResult.RootElement.GetProperty("status").GetString());

        Assert.Empty(sink.Receipts);

    }

    [Theory]
    [InlineData(MandatoryToolInteractionAppendOutcome.NewlyCommitted)]
    [InlineData(MandatoryToolInteractionAppendOutcome.RecoveredCommitted)]
    public async Task Executor_committed_handoff_marks_receipt_handled_and_keeps_patch(
        MandatoryToolInteractionAppendOutcome outcome)
    {

        _workspace.WriteFile("committed.txt", "before\n");
        OutcomePendingReceiptSink sink = new(outcome);
        ApplyPatchInvocationContext context = InvocationContext(sink);

        _ = await CreateExecutor()
            .ExecuteAsync(
                ModifyRequest("committed.txt", "before", "after"),
                context,
                CancellationToken.None);

        Assert.True(context.ReceiptHandled);
        Assert.Equal(outcome, context.HandoffOutcome);
        Assert.Equal("after\n", await ReadTextAsync("committed.txt"));
        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Executor_failed_handoff_rolls_back_and_returns_structured_failure()
    {

        _workspace.WriteFile("failed.txt", "before\n");
        OutcomePendingReceiptSink sink = new(
            MandatoryToolInteractionAppendOutcome.Failed);
        ApplyPatchInvocationContext context = InvocationContext(sink);

        ApplyPatchToolExecutionResponse response = await CreateExecutor()
            .ExecuteAsync(
                ModifyRequest("failed.txt", "before", "after"),
                context,
                CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(response.SerializedResult);
        Assert.Equal("conflict", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("receipt_failed", payload.RootElement.GetProperty("code").GetString());
        Assert.False(context.ReceiptHandled);
        Assert.False(context.RequiresTurnFailure);
        Assert.Equal("before\n", await ReadTextAsync("failed.txt"));
        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Executor_failed_handoff_returns_rollback_incomplete_with_relative_recovery_paths()
    {

        _workspace.WriteFile("failed-incomplete.txt", "before\n");
        FailedAfterExternalEditSink sink = new(
            _workspace.Root,
            "failed-incomplete.txt");
        ApplyPatchInvocationContext context = InvocationContext(sink);

        ApplyPatchToolExecutionResponse response = await CreateExecutor()
            .ExecuteAsync(
                ModifyRequest("failed-incomplete.txt", "before", "after"),
                context,
                CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(response.SerializedResult);
        Assert.Equal(
            "rollback_incomplete",
            payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "rollback_incomplete",
            payload.RootElement.GetProperty("code").GetString());
        Assert.All(
            payload.RootElement.GetProperty("affectedPaths")
                .EnumerateArray()
                .Select(static item => item.GetString()!),
            AssertRelativeRecoveryPath);
        Assert.All(
            payload.RootElement.GetProperty("recoveryArtifactPaths")
                .EnumerateArray()
                .Select(static item => item.GetString()!),
            AssertRelativeRecoveryPath);
        Assert.Equal(
            "external-after-handoff\n",
            await ReadTextAsync("failed-incomplete.txt"));
        Assert.NotEmpty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Executor_ambiguous_handoff_retains_applied_patch_and_recovery_artifacts()
    {

        _workspace.WriteFile("ambiguous.txt", "before\n");
        OutcomePendingReceiptSink sink = new(
            MandatoryToolInteractionAppendOutcome.Ambiguous);
        ApplyPatchInvocationContext context = InvocationContext(sink);

        ApplyPatchReceiptHandoffException exception =
            await Assert.ThrowsAsync<ApplyPatchReceiptHandoffException>(
                () => CreateExecutor().ExecuteAsync(
                    ModifyRequest("ambiguous.txt", "before", "after"),
                    context,
                    CancellationToken.None));

        Assert.Equal(MandatoryToolInteractionAppendOutcome.Ambiguous, exception.Outcome);
        Assert.NotEmpty(exception.AffectedPaths);
        Assert.NotEmpty(exception.RecoveryArtifactPaths);
        Assert.All(exception.AffectedPaths, AssertRelativeRecoveryPath);
        Assert.All(exception.RecoveryArtifactPaths, AssertRelativeRecoveryPath);
        Assert.DoesNotContain(
            exception.RecoveryArtifactPaths,
            path => path.Contains(_workspace.Root, StringComparison.Ordinal));
        Assert.False(context.ReceiptHandled);
        Assert.True(context.RequiresTurnFailure);
        Assert.Equal("after\n", await ReadTextAsync("ambiguous.txt"));
        Assert.NotEmpty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Executor_post_commit_deadline_propagates_ambiguous_handoff_instead_of_normal_timeout()
    {

        _workspace.WriteFile("handoff-timeout.txt", "before\n");
        ApplyPatchInvocationContext context = InvocationContext(
            new WaitForHandoffCancellationSink());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateExecutor(
                    settings: DefaultPatchSettings() with
                    {
                        MaxElapsedMilliseconds = 150,
                        RollbackReserveMilliseconds = 50,
                    })
                .ExecuteAsync(
                    ModifyRequest(
                        "handoff-timeout.txt",
                        "before",
                        "after"),
                    context,
                    CancellationToken.None));

        Assert.True(context.CancellationClassified);
        Assert.True(context.RequiresTurnFailure);
        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.Ambiguous,
            context.HandoffOutcome);
        Assert.Equal(
            "after\n",
            await ReadTextAsync("handoff-timeout.txt"));
        Assert.NotEmpty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Executor_keeps_multiple_patches_in_one_turn_as_independent_transactions()
    {

        _workspace.WriteFile("first.txt", "one\n");
        _workspace.WriteFile("second.txt", "two\n");
        RecordingPendingReceiptSink sink = new();
        const string invocationId = "turn-with-two-patches";

        ApplyPatchInvocationContext firstContext = InvocationContext(
            sink,
            invocationId,
            callOrdinal: 0);
        ApplyPatchInvocationContext secondContext = InvocationContext(
            sink,
            invocationId,
            callOrdinal: 1);

        _ = await CreateExecutor().ExecuteAsync(
            ModifyRequest("first.txt", "one", "ONE"),
            firstContext,
            CancellationToken.None);
        PendingApplyPatchReceipt first = sink.Receipts[0];
        _ = await first.MarkIrreversibleAsync(CancellationToken.None);

        _ = await CreateExecutor().ExecuteAsync(
            ModifyRequest("second.txt", "two", "TWO"),
            secondContext,
            CancellationToken.None);
        PendingApplyPatchReceipt second = sink.Receipts[1];

        Assert.NotEqual(first.Receipt.Id, second.Receipt.Id);
        Assert.Equal("ONE\n", await ReadTextAsync("first.txt"));
        Assert.Equal("TWO\n", await ReadTextAsync("second.txt"));

        WorkspaceRollbackResult secondRollback =
            await second.RollbackAsync(CancellationToken.None);

        Assert.True(secondRollback.Complete);
        Assert.Equal("ONE\n", await ReadTextAsync("first.txt"));
        Assert.Equal("two\n", await ReadTextAsync("second.txt"));

    }

    [Fact]
    public async Task Executor_returns_normal_structured_parse_conflict_and_concurrent_edit_outcomes()
    {

        _workspace.WriteFile("concurrent.txt", "before\n");
        RecordingPendingReceiptSink sink = new();

        ApplyPatchToolExecutionResponse invalid = await CreateExecutor()
            .ExecuteAsync(
                new ApplyPatchParams("not a diff", DryRun: false),
                InvocationContext(sink),
                CancellationToken.None);
        ApplyPatchToolExecutionResponse conflict = await CreateExecutor()
            .ExecuteAsync(
                ModifyRequest("concurrent.txt", "different", "after"),
                InvocationContext(sink, callOrdinal: 1),
                CancellationToken.None);
        ApplyPatchToolExecutionResponse concurrent = await CreateExecutor(
            coordinatorFactory: root =>
                new MultiFileCommitCoordinator(
                    root,
                    new MultiFileCommitCoordinatorOptions
                    {
                        BeforeCommitStepAsync = (step, _) =>
                        {
                            if (step.Index == 0)
                            {
                                File.WriteAllText(
                                    Path.Combine(root, "concurrent.txt"),
                                    "external\n");
                            }

                            return ValueTask.CompletedTask;
                        },
                    }))
            .ExecuteAsync(
                ModifyRequest("concurrent.txt", "before", "after"),
                InvocationContext(sink, callOrdinal: 2),
                CancellationToken.None);

        Assert.Equal("unsupported_metadata", ResultCode(invalid));
        Assert.Equal("hunk_conflict", ResultCode(conflict));
        Assert.Equal("concurrent_edit", ResultCode(concurrent));
        Assert.Equal("external\n", await ReadTextAsync("concurrent.txt"));
        Assert.Empty(sink.Receipts);

    }

    [Fact]
    public async Task Executor_cancellation_rolls_back_and_propagates_without_handoff()
    {

        _workspace.WriteFile("cancel.txt", "before\n");
        using CancellationTokenSource cancellation = new();
        RecordingPendingReceiptSink sink = new();

        ApplyPatchToolExecutionService executor = CreateExecutor(
            coordinatorFactory: root =>
                new MultiFileCommitCoordinator(
                    root,
                    new MultiFileCommitCoordinatorOptions
                    {
                        AfterDestinationMutation = _ => cancellation.Cancel(),
                    }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(
                ModifyRequest("cancel.txt", "before", "after"),
                InvocationContext(sink),
                cancellation.Token));

        Assert.Equal("before\n", await ReadTextAsync("cancel.txt"));
        Assert.Empty(sink.Receipts);
        Assert.Empty(ArcanumArtifacts());

    }

    [Fact]
    public async Task Executor_cancellation_reports_relative_recovery_when_cleanup_cannot_restore()
    {

        _workspace.WriteFile("cancel-recovery.txt", "before\n");
        using CancellationTokenSource cancellation = new();
        CancellingAfterExternalEditSink sink = new(
            _workspace.Root,
            "cancel-recovery.txt",
            cancellation);

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CreateExecutor().ExecuteAsync(
                    ModifyRequest(
                        "cancel-recovery.txt",
                        "before",
                        "after"),
                    InvocationContext(sink),
                    cancellation.Token));

        WorkspaceCommitRecovery recovery = Assert.IsType<WorkspaceCommitRecovery>(
            exception.Data[nameof(WorkspaceCommitRecovery)]);
        Assert.All(recovery.AffectedPaths, AssertRelativeRecoveryPath);
        Assert.All(recovery.ArtifactPaths, AssertRelativeRecoveryPath);
        Assert.NotEmpty(recovery.ArtifactPaths);
        Assert.Equal(
            "external-after-handoff\n",
            await ReadTextAsync("cancel-recovery.txt"));

    }

    [Fact]
    public async Task Executor_reports_relative_recovery_when_rollback_is_incomplete()
    {

        _workspace.WriteFile("first.txt", "one\n");
        _workspace.WriteFile("second.txt", "two\n");
        RecordingPendingReceiptSink sink = new();

        ApplyPatchToolExecutionService executor = CreateExecutor(
            coordinatorFactory: root =>
                new MultiFileCommitCoordinator(
                    root,
                    new MultiFileCommitCoordinatorOptions
                    {
                        AfterCommitStepAsync = (step, _) =>
                        {
                            if (step.Index == 0)
                            {
                                File.WriteAllText(
                                    Path.Combine(root, "first.txt"),
                                    "external\n");
                            }

                            return ValueTask.CompletedTask;
                        },
                        BeforeCommitStepAsync = (step, _) =>
                            step.Index == 1
                                ? ValueTask.FromException(
                                    new IOException("injected failure"))
                                : ValueTask.CompletedTask,
                    }));

        ApplyPatchToolExecutionResponse response = await executor.ExecuteAsync(
            new ApplyPatchParams(
                """
                --- a/first.txt
                +++ b/first.txt
                @@ -1 +1 @@
                -one
                +ONE
                --- a/second.txt
                +++ b/second.txt
                @@ -1 +1 @@
                -two
                +TWO
                """,
                DryRun: false),
            InvocationContext(sink),
            CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(response.SerializedResult);
        Assert.Equal(
            "rollback_incomplete",
            payload.RootElement.GetProperty("code").GetString());
        Assert.All(
            payload.RootElement.GetProperty("recoveryArtifactPaths")
                .EnumerateArray()
                .Select(static item => item.GetString()!),
            AssertRelativeRecoveryPath);
        Assert.Equal("external\n", await ReadTextAsync("first.txt"));
        Assert.Equal("two\n", await ReadTextAsync("second.txt"));
        Assert.Empty(sink.Receipts);

    }

    [Fact]
    public async Task Executor_independently_caps_recovery_arrays_without_losing_status_or_counts()
    {

        const int committedBeforeFailure = 8;
        const int fileCount = 10;
        System.Text.StringBuilder patch = new();

        for (int index = 0; index < fileCount; index++)
        {
            string path =
                $"nested/long-recovery-path-{index:D2}-abcdefghijklmnopqrstuvwxyz.txt";
            _workspace.WriteFile(path, $"before-{index:D2}\n");
            _ = patch.Append("--- a/").AppendLine(path)
                .Append("+++ b/").AppendLine(path)
                .AppendLine("@@ -1 +1 @@")
                .Append("-before-").AppendLine(index.ToString("D2"))
                .Append("+after-").AppendLine(index.ToString("D2"));
        }

        ApplyPatchToolExecutionResponse response = await CreateExecutor(
            outputBudgetBytes: 700,
            coordinatorFactory: root =>
                new MultiFileCommitCoordinator(
                    root,
                    new MultiFileCommitCoordinatorOptions
                    {
                        AfterCommitStepAsync = (step, _) =>
                        {
                            File.WriteAllText(
                                Path.Combine(root, step.RelativePath),
                                $"external-{step.Index}\n");
                            return ValueTask.CompletedTask;
                        },
                        BeforeCommitStepAsync = (step, _) =>
                            step.Index == committedBeforeFailure
                                ? ValueTask.FromException(
                                    new IOException("injected failure"))
                                : ValueTask.CompletedTask,
                    }))
            .ExecuteAsync(
                new ApplyPatchParams(patch.ToString(), DryRun: false),
                InvocationContext(new RecordingPendingReceiptSink()),
                CancellationToken.None);

        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(response.SerializedResult)
            <= 700);
        using JsonDocument payload = JsonDocument.Parse(response.SerializedResult);
        Assert.Equal(
            "rollback_incomplete",
            payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "rollback_incomplete",
            payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            committedBeforeFailure,
            payload.RootElement.GetProperty("totalAffectedPathCount").GetInt32());
        Assert.True(
            payload.RootElement.GetProperty("totalRecoveryArtifactPathCount")
                .GetInt32() >= committedBeforeFailure);
        Assert.True(
            payload.RootElement.GetProperty("omittedAffectedPathCount").GetInt32()
            > 0);
        Assert.True(
            payload.RootElement.GetProperty("omittedRecoveryArtifactPathCount")
                .GetInt32() > 0);
        Assert.NotEmpty(
            payload.RootElement.GetProperty("affectedPaths").EnumerateArray());
        Assert.NotEmpty(
            payload.RootElement.GetProperty("recoveryArtifactPaths")
                .EnumerateArray());

    }

    [Fact]
    public async Task Pending_receipt_classifies_post_irreversible_backup_cleanup_without_changing_result()
    {

        _workspace.WriteFile("cleanup.txt", "before\n");
        RecordingPendingReceiptSink sink = new();

        ApplyPatchToolExecutionResponse response = await CreateExecutor()
            .ExecuteAsync(
                ModifyRequest("cleanup.txt", "before", "after"),
                InvocationContext(sink),
                CancellationToken.None);
        PendingApplyPatchReceipt pending = Assert.Single(sink.Receipts);
        string resultBeforeCleanup = pending.SerializedResult;
        string backup = Assert.Single(pending.Recovery!.ArtifactPaths);

        await File.WriteAllTextAsync(
            Path.Combine(
                _workspace.Root,
                backup.Replace('/', Path.DirectorySeparatorChar)),
            "external-backup-change");

        WorkspaceArtifactCleanupResult cleanup =
            await pending.MarkIrreversibleAsync(CancellationToken.None);

        Assert.False(cleanup.Complete);
        Assert.Equal([backup], cleanup.RetainedArtifactPaths);
        Assert.Equal(resultBeforeCleanup, pending.SerializedResult);
        Assert.Equal(response.SerializedResult, pending.SerializedResult);
        Assert.Equal("after\n", await ReadTextAsync("cleanup.txt"));

    }

    [Fact]
    public async Task Executor_bounds_file_results_as_valid_json_before_handoff()
    {

        const int fileCount = 24;
        System.Text.StringBuilder patch = new();

        for (int index = 0; index < fileCount; index++)
        {
            _ = patch.AppendLine("--- /dev/null")
                .Append("+++ b/generated/file-")
                .Append(index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))
                .AppendLine(".txt")
                .AppendLine("@@ -0,0 +1 @@")
                .Append("+value-")
                .Append(index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))
                .AppendLine();
        }

        RecordingPendingReceiptSink sink = new();
        ApplyPatchToolExecutionResponse response = await CreateExecutor(
            outputBudgetBytes: 1_024)
            .ExecuteAsync(
                new ApplyPatchParams(patch.ToString(), DryRun: false),
                InvocationContext(sink),
                CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(response.SerializedResult);
        PendingApplyPatchReceipt pending = Assert.Single(sink.Receipts);

        Assert.True(payload.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            fileCount,
            payload.RootElement.GetProperty("totalFileCount").GetInt32());
        Assert.True(
            payload.RootElement.GetProperty("omittedFileCount").GetInt32() > 0);
        Assert.Equal(response.SerializedResult, pending.SerializedResult);

        WorkspaceRollbackResult rollback =
            await pending.RollbackAsync(CancellationToken.None);

        Assert.True(rollback.Complete);

    }

    private ApplyPatchToolExecutionService CreateExecutor(
        long outputBudgetBytes = 1024 * 1024,
        Func<string, MultiFileCommitCoordinator>? coordinatorFactory = null,
        WorkspacePatchSettings? settings = null,
        TimeProvider? timeProvider = null) =>
        new(
            _workspace.Root,
            settings ?? DefaultPatchSettings(),
            outputBudgetBytes,
            McpJsonSerializerContext.Default,
            coordinatorFactory,
            timeProvider);

    private static ApplyPatchInvocationContext InvocationContext(
        IApplyPatchPendingReceiptSink sink,
        string invocationId = "turn-apply-patch",
        int callOrdinal = 0,
        string serializedArguments = "{\"patch\":\"test-snapshot\"}") =>
        new(
            SessionId: Guid.Parse("53e1e81f-7eb3-4e33-a34e-e0a783ea6445"),
            AssistantEntryId: Guid.Parse("efb88724-dcee-453e-a268-918399e14938"),
            Identity: new ToolInvocationIdentity(
                invocationId,
                ProviderToolCallId: $"provider-{callOrdinal}",
                ToolRoundOrdinal: 0,
                CallOrdinal: callOrdinal,
                ToolName: ToolRiskClassifier.ApplyPatchToolName),
            SerializedArguments: serializedArguments,
            ModelUsed: "test-model",
            CreatedAt: DateTimeOffset.Parse(
                "2026-07-26T12:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            Sink: sink);

    private static ApplyPatchParams ModifyRequest(
        string path,
        string oldText,
        string newText) =>
        new(
            $"""
             --- a/{path}
             +++ b/{path}
             @@ -1 +1 @@
             -{oldText}
             +{newText}
             """,
            DryRun: false);

    private static string ResultCode(
        ApplyPatchToolExecutionResponse response)
    {

        using JsonDocument payload = JsonDocument.Parse(response.SerializedResult);

        return payload.RootElement.GetProperty("code").GetString()!;

    }

    private string[] ArcanumArtifacts() =>
        Directory.GetFiles(
            _workspace.Root,
            "*.arcanum-*",
            SearchOption.AllDirectories);

    private static void AssertRelativeRecoveryPath(string path)
    {

        Assert.False(Path.IsPathRooted(path));
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', path);

    }

    private abstract class AdmittingPendingReceiptSink
        : IApplyPatchPendingReceiptSink
    {
        public virtual ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                new ApplyPatchReceiptProbeResult(
                    ApplyPatchReceiptProbeOutcome.NotFound,
                    SerializedResult: null));

        }

        public virtual ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                new ApplyPatchReceiptPreflightResult(
                    ApplyPatchReceiptPreflightOutcome.Admitted,
                    SerializedResult: null));

        }

        public virtual ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                MandatoryToolInteractionAppendOutcome.NewlyCommitted);

        }

        public abstract ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken);
    }

    private sealed class AdvancingProbeReceiptSink(
        ManualPatchTimeProvider timeProvider,
        TimeSpan advanceBy)
        : AdmittingPendingReceiptSink
    {
        internal bool PreflightCalled { get; private set; }

        public override ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken)
        {

            timeProvider.Advance(advanceBy);

            return base.ProbeAsync(probe, cancellationToken);

        }

        public override ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken)
        {

            PreflightCalled = true;

            return base.PreflightAsync(preflight, cancellationToken);

        }

        public override ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A deadline-expired patch must not reach receipt handoff.");
    }

    private sealed class AdvancingSuccessfulReceiptSink(
        ManualPatchTimeProvider timeProvider,
        TimeSpan advanceBy)
        : AdmittingPendingReceiptSink
    {
        public override ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken)
        {
            timeProvider.Advance(advanceBy);

            return base.ProbeAsync(probe, cancellationToken);
        }

        public override ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                new ApplyPatchPendingReceiptHandoffResult(
                    MandatoryToolInteractionAppendOutcome.NewlyCommitted,
                    Cleanup: null,
                    Rollback: null));
        }
    }

    private sealed class RecordingPendingReceiptSink
        : AdmittingPendingReceiptSink
    {
        internal List<PendingApplyPatchReceipt> Receipts { get; } = [];

        public override ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();
            Receipts.Add(receipt);

            return ValueTask.FromResult(
                new ApplyPatchPendingReceiptHandoffResult(
                    MandatoryToolInteractionAppendOutcome.NewlyCommitted,
                    Cleanup: null,
                    Rollback: null));

        }
    }

    private sealed class PreflightPendingReceiptSink(
        ApplyPatchReceiptProbeResult probeResult,
        ApplyPatchReceiptPreflightResult preflightResult)
        : IApplyPatchPendingReceiptSink
    {
        internal int ProbeCount { get; private set; }

        internal int PreflightCount { get; private set; }

        internal ApplyPatchReceiptPreflight? Preflight { get; private set; }

        internal List<PendingApplyPatchReceipt> Receipts { get; } = [];

        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            ProbeCount++;

            return ValueTask.FromResult(probeResult);

        }

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            PreflightCount++;

            Preflight = preflight;

            return ValueTask.FromResult(preflightResult);

        }

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                MandatoryToolInteractionAppendOutcome.NewlyCommitted);

        }

        public ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Receipts.Add(receipt);

            return ValueTask.FromResult(
                new ApplyPatchPendingReceiptHandoffResult(
                    MandatoryToolInteractionAppendOutcome.NewlyCommitted,
                    Cleanup: null,
                    Rollback: null));

        }
    }

    private sealed class OutcomePendingReceiptSink(
        MandatoryToolInteractionAppendOutcome outcome)
        : AdmittingPendingReceiptSink
    {
        public override async ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            WorkspaceArtifactCleanupResult? cleanup = null;
            WorkspaceRollbackResult? rollback = null;

            if (outcome is MandatoryToolInteractionAppendOutcome.NewlyCommitted
                or MandatoryToolInteractionAppendOutcome.RecoveredCommitted)
            {
                cleanup = await receipt.MarkIrreversibleAsync(CancellationToken.None);
            }
            else if (outcome == MandatoryToolInteractionAppendOutcome.Failed)
            {
                rollback = await receipt.RollbackAsync(CancellationToken.None);
            }

            return new ApplyPatchPendingReceiptHandoffResult(
                outcome,
                cleanup,
                rollback);

        }
    }

    private sealed class WaitForHandoffCancellationSink
        : AdmittingPendingReceiptSink
    {
        public override async ValueTask<ApplyPatchPendingReceiptHandoffResult>
            HandoffAsync(
                PendingApplyPatchReceipt receipt,
                CancellationToken cancellationToken)
        {

            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);

            throw new InvalidOperationException(
                "The handoff cancellation wait unexpectedly completed.");
        }
    }

    private sealed class CancellingAfterExternalEditSink(
        string workspaceRoot,
        string relativePath,
        CancellationTokenSource cancellation)
        : AdmittingPendingReceiptSink
    {
        public override ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken)
        {

            File.WriteAllText(
                Path.Combine(workspaceRoot, relativePath),
                "external-after-handoff\n");
            cancellation.Cancel();

            return ValueTask.FromException<ApplyPatchPendingReceiptHandoffResult>(
                new OperationCanceledException(cancellation.Token));

        }
    }

    private sealed class FailedAfterExternalEditSink(
        string workspaceRoot,
        string relativePath)
        : AdmittingPendingReceiptSink
    {
        public override async ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken)
        {

            File.WriteAllText(
                Path.Combine(workspaceRoot, relativePath),
                "external-after-handoff\n");

            WorkspaceRollbackResult rollback =
                await receipt.RollbackAsync(CancellationToken.None);

            return new ApplyPatchPendingReceiptHandoffResult(
                MandatoryToolInteractionAppendOutcome.Failed,
                Cleanup: null,
                rollback);

        }
    }

    private async Task<WorkspacePatchPlanResult> PlanSingleModifyAsync(
        string path,
        string oldText,
        string newText)
    {

        UnifiedDiffManifest manifest = ParseManifest(
            $"""
             --- a/{path}
             +++ b/{path}
             @@ -1 +1 @@
             -{oldText}
             +{newText}
             """);

        return await new WorkspacePatchPlanner(DefaultPatchSettings())
            .PlanAsync(
                _workspace.Root,
                manifest,
                CancellationToken.None);

    }

    private static UnifiedDiffManifest ParseManifest(string patch)
    {

        UnifiedDiffParseResult parsed = UnifiedDiffParser.Parse(
            patch,
            DefaultPatchSettings());

        Assert.True(parsed.Success, parsed.Message);

        return Assert.IsType<UnifiedDiffManifest>(parsed.Manifest);

    }

    private Task<string> ReadTextAsync(string relativePath) =>
        File.ReadAllTextAsync(
            Path.Combine(_workspace.Root, relativePath),
            CancellationToken.None);

    private sealed class IncrementingPatchTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() =>
            Interlocked.Add(ref _timestamp, 1_000);
    }

    private sealed class ManualPatchTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() =>
            Interlocked.Read(ref _timestamp);

        internal void Advance(TimeSpan elapsed) =>
            Interlocked.Add(
                ref _timestamp,
                (long)(elapsed.TotalSeconds * TimestampFrequency));
    }

    private static WorkspacePatchSettings DefaultPatchSettings() =>
        new()
        {
            MaxPatchBytes = 4L * 1024L * 1024L,
            MaxFiles = 128,
            MaxHunks = 1_024,
            MaxLinesPerHunk = 10_000,
            FuzzyMatchWindowLines = 100,
            MaxResultItems = 256,
        };

}
