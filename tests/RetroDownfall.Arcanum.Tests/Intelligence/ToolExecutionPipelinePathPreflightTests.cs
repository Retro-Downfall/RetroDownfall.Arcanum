using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolExecutionPipelinePathPreflightTests
{
    [Fact]
    public void TryResolvePathUnderWorkspace_AllowsChildUnderRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            bool ok = ToolExecutionPipeline.TryResolvePathUnderWorkspace(root, "notes/a.txt", out string absolute);
            Assert.True(ok);
            Assert.StartsWith(Path.GetFullPath(root), absolute, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public void TryResolvePathUnderWorkspace_RejectsDotDotEscape()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            bool ok = ToolExecutionPipeline.TryResolvePathUnderWorkspace(
                root,
                Path.Combine("..", "outside.txt"),
                out string absolute);

            Assert.False(ok);
            Assert.Equal(string.Empty, absolute);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public void TryResolvePathUnderWorkspace_RejectsAbsoluteOutsideRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            bool ok = ToolExecutionPipeline.TryResolvePathUnderWorkspace(root, outside, out string absolute);
            Assert.False(ok);
            Assert.Equal(string.Empty, absolute);
        }
        finally
        {
            _ = TestDirectoryCleanup.TryDelete(root, nameof(ToolExecutionPipelinePathPreflightTests));
        }
    }

    [Fact]
    public void TryResolveSearchRootUnderWorkspace_normalizes_explicit_relative_root()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-search-preflight-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "src", "nested");
        Directory.CreateDirectory(nested);

        try
        {
            bool ok = ToolExecutionPipeline.TryResolveSearchRootUnderWorkspace(
                root,
                @"src\nested\.",
                out string absolute);

            Assert.True(ok);
            Assert.Equal(Path.GetFullPath(nested), absolute);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryResolveSearchRootUnderWorkspace_rejects_escape()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-search-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            bool ok = ToolExecutionPipeline.TryResolveSearchRootUnderWorkspace(
                root,
                "../outside",
                out string absolute);

            Assert.False(ok);
            Assert.Equal(string.Empty, absolute);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Apply_patch_preflight_uses_the_pure_parser_manifest_without_workspace_reads()
    {
        JsonElement arguments = JsonSerializer.SerializeToElement(
            new
            {
                patch =
                    """
                    diff --git a/missing-old.txt b/missing-new.txt
                    similarity index 100%
                    rename from missing-old.txt
                    rename to missing-new.txt
                    """,
                dryRun = false,
            }, AdHocJson.Options);

        bool parsed = ToolExecutionPipeline.TryParseApplyPatchManifest(
            arguments,
            new WorkspacePatchSettings(),
            CancellationToken.None,
            out var manifest);

        Assert.True(parsed);
        Assert.NotNull(manifest);
        Assert.Equal(
            ["missing-old.txt", "missing-new.txt"],
            manifest!.NormalizedPaths);
    }

    [Fact]
    public void Apply_patch_preflight_clamps_validator_bypassed_extreme_limits()
    {
        JsonElement arguments = JsonSerializer.SerializeToElement(
            new
            {
                patch =
                    """
                    --- /dev/null
                    +++ b/new.txt
                    @@ -0,0 +1 @@
                    +value
                    """,
            }, AdHocJson.Options);
        WorkspacePatchSettings settings = new()
        {
            MaxPatchBytes = long.MinValue,
            MaxInputBytesPerFile = long.MinValue,
            MaxOutputBytesPerFile = long.MinValue,
            RecoveryTimeoutMilliseconds = int.MaxValue,
        };

        bool parsed = ToolExecutionPipeline.TryParseApplyPatchManifest(
            arguments,
            settings,
            CancellationToken.None,
            out var manifest);

        Assert.True(parsed);
        Assert.NotNull(manifest);
        Assert.Equal(["new.txt"], manifest.NormalizedPaths);
    }

    [Fact]
    public void Apply_patch_preflight_propagates_parser_cancellation()
    {
        JsonElement arguments = JsonSerializer.SerializeToElement(
            new
            {
                patch =
                    """
                    --- a/cancelled.txt
                    +++ b/cancelled.txt
                    @@ -1 +1 @@
                    -before
                    +after
                    """,
            }, AdHocJson.Options);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ToolExecutionPipeline.TryParseApplyPatchManifest(
                arguments,
                new WorkspacePatchSettings(),
                cancellation.Token,
                out _));
    }

    [Fact]
    public async Task Apply_patch_without_persisted_turn_records_ungated_resolution_before_rejection()
    {
        bool invoked = false;
        DenyingWard ward = new();
        ArcanumSettings settings = new()
        {
            Security = new SecuritySettings
            {
                Ward = new WardPolicySettings
                {
                    ForbiddenArts = [],
                },
            },
        };
        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            ward,
            new AllowAllSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);
        FunctionCallContent call = new(
            "patch-call",
            ToolRiskClassifier.ApplyPatchToolName,
            new Dictionary<string, object?>
            {
                ["patch"] =
                    """
                    --- a/never-read.txt
                    +++ b/never-read.txt
                    @@ -1 +1 @@
                    -before
                    +after
                    """,
            });
        ChatOptions options = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    () =>
                    {
                        invoked = true;
                        return "workspace reader invoked";
                    },
                    ToolRiskClassifier.ApplyPatchToolName),
            ],
        };

        ToolExecutionPipeline.ProcessedToolCall processed =
            await pipeline.ProcessSingleToolCallAsync(
                call,
                new PingRequest("patch", WorkingDirectory: "/unavailable"),
                options,
                activeSpell: null,
                sessionId: "persisted-session",
                turnContext: new ToolExecutionPipeline.TurnContext
                {
                    WorkspaceRoot = "/unavailable",
                },
                suppressInvocationFailures: false,
                cancellationToken: CancellationToken.None);

        Assert.Contains(
            "session_required",
            processed.ResultText,
            StringComparison.Ordinal);
        Assert.False(invoked);

        Assert.Equal(0, ward.RequestCount);

        Assert.Equal(1, ward.AutomaticResolutionCount);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static evt => evt.Type));

        Assert.All(
            processed.WardEvents,
            static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));
    }

    /// <summary>
    /// In-process tools whose arguments carry a workspace path, with the argument that carries it. The
    /// Sanctum preflight is a name switch that fails open for any tool it does not list, so this table is
    /// the contract the invariant below holds the registry to.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PathArgumentByTool =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["read_file_chunk"] = "relativePath",
            ["replace_text_block"] = "relativePath",
            ["write_file"] = "relativePath",
            ["list_directory"] = "relativePath",
            [ToolRiskClassifier.SearchWorkspaceToolName] = "root",
            [ToolRiskClassifier.ExecuteCommandToolName] = "workingDirectory",
        };

    /// <summary>Tools whose paths arrive inside a unified diff, preflighted from its parsed manifest.</summary>
    private static readonly string[] ManifestPathTools = [ToolRiskClassifier.ApplyPatchToolName];

    /// <summary>
    /// Registered tools with no workspace path argument at all. Adding a tool here is the explicit
    /// decision that nothing it accepts names a file or directory.
    /// </summary>
    private static readonly string[] NoWorkspacePathTools =
    [
        ToolRiskClassifier.WorkspaceCheckToolName,
        "read_command_output",
        "adjust_initiative",
        "send_commlink_alert",
        "petition_dungeon_master",
        "cast_sending",
        "dispatch_sending",
        "continue_sending",
        "ask_human",
        "scribe_lexicon",
        "delete_lexicon",
        "search_archives",
        "propose_covenant",
        "retire_covenant",
        "read_saga",
        "attach_session_file",
        "refresh_session_file",
    ];

    public static IEnumerable<object[]> PathArgumentTools() =>
        PathArgumentByTool.Select(static pair => new object[] { pair.Key, pair.Value });

    [Fact]
    public async Task ListDirectory_EscapingPath_IsDeniedAndRecordsBreach()
    {
        string workspace = Path.Combine(
            Path.GetTempPath(),
            "arcanum-preflight-list-" + Guid.NewGuid().ToString("N"));

        try
        {
            SanctumPipelineHarness harness = SanctumPipelineHarness.Create(workspace);

            (ToolExecutionPipeline.ProcessedToolCall processed, bool invoked) =
                await harness.ProcessStandInAsync(
                    "list_directory",
                    new Dictionary<string, object?> { ["relativePath"] = "../outside" },
                    harness.StrictTurnContext());

            Assert.True(processed.Denied);

            Assert.False(invoked);

            SanctumBreachRecord breach = Assert.Single(harness.Breaches.Records);

            Assert.Equal("PathEscape", breach.BreachType);

            Assert.Equal("list_directory", breach.ToolName);
        }
        finally
        {
            _ = TestDirectoryCleanup.TryDelete(workspace, nameof(ToolExecutionPipelinePathPreflightTests));
        }
    }

    [Theory]
    [MemberData(nameof(PathArgumentTools))]
    public async Task Path_argument_tool_with_an_escaping_path_is_denied_and_records_a_breach(
        string toolName,
        string argumentName)
    {
        string workspace = Path.Combine(
            Path.GetTempPath(),
            "arcanum-preflight-escape-" + Guid.NewGuid().ToString("N"));

        try
        {
            SanctumPipelineHarness harness = SanctumPipelineHarness.Create(workspace);

            (ToolExecutionPipeline.ProcessedToolCall processed, bool invoked) =
                await harness.ProcessStandInAsync(
                    toolName,
                    new Dictionary<string, object?> { [argumentName] = "../outside" },
                    harness.StrictTurnContext());

            Assert.True(
                processed.Denied,
                $"{toolName} accepted an escaping '{argumentName}' without a Sanctum denial.");

            Assert.False(invoked);

            SanctumBreachRecord breach = Assert.Single(harness.Breaches.Records);

            Assert.Equal("PathEscape", breach.BreachType);

            Assert.Equal(toolName, breach.ToolName);
        }
        finally
        {
            _ = TestDirectoryCleanup.TryDelete(workspace, nameof(ToolExecutionPipelinePathPreflightTests));
        }
    }

    /// <summary>
    /// The guard resolves what it is handed against the host process's current directory, so an escape
    /// written relative to the workspace is only judged correctly when the pipeline anchors it to the
    /// workspace root first. The campaign here is the directory above the test host's own, which puts the
    /// host's current directory inside it: <c>../outside</c> read against that directory is inside the
    /// campaign, while read against the workspace root it leaves it.
    /// </summary>
    [Theory]
    [MemberData(nameof(PathArgumentTools))]
    public async Task Escape_is_recorded_even_when_the_host_working_directory_is_inside_the_campaign(
        string toolName,
        string argumentName)
    {
        string hostDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(global::System.Environment.CurrentDirectory));

        string? workspace = Path.GetDirectoryName(hostDirectory);

        Assert.False(
            string.IsNullOrEmpty(workspace) || Path.GetDirectoryName(workspace) is null,
            $"The test host's directory '{hostDirectory}' is too close to the filesystem root for this probe.");

        SanctumPipelineHarness harness = SanctumPipelineHarness.Create(workspace!);

        (ToolExecutionPipeline.ProcessedToolCall processed, bool invoked) =
            await harness.ProcessStandInAsync(
                toolName,
                new Dictionary<string, object?> { [argumentName] = "../outside" },
                harness.StrictTurnContext());

        Assert.True(
            processed.Denied,
            $"{toolName} accepted an escaping '{argumentName}' judged against the host working directory.");

        Assert.False(invoked);

        SanctumBreachRecord breach = Assert.Single(harness.Breaches.Records);

        Assert.Equal("PathEscape", breach.BreachType);

        Assert.Equal(toolName, breach.ToolName);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    public void A_relative_spelling_is_anchored_to_the_workspace_root(string spelling)
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-anchor-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "..", "outside")),
            ToolExecutionPipeline.AnchorToWorkspaceRoot(root, spelling));

        Assert.Equal(
            Path.Combine(root, "inside", "file.txt"),
            ToolExecutionPipeline.AnchorToWorkspaceRoot(root, "inside/file.txt"));

        string absolute = Path.Combine(Path.GetTempPath(), "arcanum-elsewhere", "file.txt");

        Assert.Equal(absolute, ToolExecutionPipeline.AnchorToWorkspaceRoot(root, absolute));
    }

    [Fact]
    public void An_unresolvable_spelling_is_handed_over_as_written_for_the_guard_to_reject()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-anchor-" + Guid.NewGuid().ToString("N"));

        Assert.Equal("bad\0name", ToolExecutionPipeline.AnchorToWorkspaceRoot(root, "bad\0name"));
    }

    [Fact]
    public void Every_registered_internal_tool_is_classified_for_Sanctum_path_preflight()
    {
        HashSet<string> registered = [.. InternalToolHandlerNames.Registered()];

        HashSet<string> classified =
        [
            .. PathArgumentByTool.Keys,
            .. ManifestPathTools,
            .. NoWorkspacePathTools,
        ];

        string[] unclassified = [.. registered.Except(classified).Order(StringComparer.Ordinal)];

        Assert.True(
            unclassified.Length == 0,
            "Registered tools with no Sanctum path-preflight classification (add each to PathArgumentByTool "
            + "with a preflight case, or to NoWorkspacePathTools if it takes no path): "
            + string.Join(", ", unclassified));

        string[] stale = [.. classified.Except(registered).Order(StringComparer.Ordinal)];

        Assert.True(
            stale.Length == 0,
            "Classified tools that are no longer registered: " + string.Join(", ", stale));
    }

    private sealed class DenyingWard : IWard
    {
        internal int RequestCount { get; private set; }

        internal int AutomaticResolutionCount { get; private set; }

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            return Task.FromResult(
                new WardResolution(
                    Allowed: false,
                    Reason: "test denial",
                    ResolvedAt: DateTimeOffset.UtcNow));
        }

        public ResolveStatus Resolve(
            string wardId,
            bool allow,
            string? reason) =>
            ResolveStatus.Success;

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin)
        {
            AutomaticResolutionCount++;

            return new WardResolution(
                allowed,
                reason,
                DateTimeOffset.UtcNow,
                origin);
        }

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];
    }

    private sealed class AllowAllSanctumGuard : ISanctumGuard
    {
        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?>
            GetChildProcessBoundaryForWorkspaceAsync(
                string? workspaceRoot,
                CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
