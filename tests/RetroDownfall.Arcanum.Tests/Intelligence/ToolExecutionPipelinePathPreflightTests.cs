using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
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
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
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
            });

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
            });
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
    public async Task Apply_patch_without_persisted_turn_is_rejected_before_Ward_or_workspace_reader()
    {

        bool invoked = false;
        DenyingWard ward = new();
        ArcanumSettings settings = new()
        {
            Ward = new WardSettings
            {
                Enabled = true,
                ForbiddenArts = [],
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

    }

    private sealed class DenyingWard : IWard
    {
        internal int RequestCount { get; private set; }

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
