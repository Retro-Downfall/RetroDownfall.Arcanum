using System.Reflection;

using RetroDownfall.Arcanum.Api.Intelligence.Subagents;

using RetroDownfall.Arcanum.Api.Intelligence.Tools;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class UnrestrictedHarnessContractTests
{

    [Fact]
    public void Runtime_intelligence_projection_has_no_arbitrary_turn_or_command_limits()
    {

        string[] removedProperties =
        [
            "ExecuteCommandTimeoutSeconds",
            "MaxModelCallsPerTurn",
            "MaxToolRoundsPerTurn",
            "MaxToolCallsPerTurn",
            "MaxToolResultTokensPerTurn",
            "MaxToolResultBytesPerTurn",
            "MaxTurnElapsedSeconds",
            "MaxTurnEstimatedCostUsd",
            "MaxTurnReservedCostUsd",
            "EnableAdaptiveLoopBudget",
        ];

        foreach (string propertyName in removedProperties)
        {

            Assert.Null(
                typeof(IntelligenceSettings).GetProperty(
                    propertyName,
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly));

        }

        Assert.Null(
            typeof(ITurnBudget).Assembly.GetType(
                "RetroDownfall.Arcanum.Core.Intelligence.TurnLimits"));

        Assert.Null(
            typeof(ITurnBudget).Assembly.GetType(
                "RetroDownfall.Arcanum.Core.Intelligence.TurnBudget"));

    }

    [Fact]
    public void Public_configuration_omits_internal_workflow_counters_and_deadlines()
    {

        Assert.Null(typeof(McpSettings).GetProperty("RequestTimeoutSeconds"));

        Assert.Null(typeof(McpSettings).GetProperty("MaxPaginationPages"));

        Assert.Null(typeof(McpSettings).GetProperty("MaxServers"));

        Assert.Null(typeof(McpSettings).GetProperty("MaxToolsPerServer"));

        Assert.Null(typeof(McpSettings).GetProperty("MaxToolsPerListPage"));

        Assert.Null(
            typeof(EmbeddingIntegrationSettings).GetProperty(
                "CodebaseIndexing",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(EmbeddingIntegrationSettings).GetProperty(
                "AttachmentIndexing",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(ExecutionSettings).GetProperty(
                "MaxPendingApprenticeStarts",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(RetentionSettings).GetProperty(
                "MaxItemsPerSweep",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(RetentionSettings).GetProperty(
                "CheckpointInterval",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(SessionSettings).GetProperty(
                "MaxEntriesPerSession",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(SessionSettings).GetProperty(
                "MaxForkDepth",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(SessionContextPinMaterializer).GetField(
                "MaxPinsPerTurn",
                BindingFlags.Public
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(SessionContextPinMaterializer).GetField(
                "MaxSourceFileBytes",
                BindingFlags.Public
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(BatchesSettings).GetProperty(
                "MaxRequestsPerBatch",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(BatchesSettings).GetProperty(
                "BatchExpiryHours",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.Null(
            typeof(IntelligenceSettings).GetProperty(
                "ListDirectoryMaxPaths",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        string[] removedWorkspaceSearchProperties =
        [
            "MaxElapsedMilliseconds",
            "MaxFiles",
            "MaxBytes",
            "MaxTraversalSteps",
            "MaxMatches",
        ];

        foreach (string propertyName in removedWorkspaceSearchProperties)
        {

            Assert.Null(
                typeof(WorkspaceSearchSettings).GetProperty(
                    propertyName,
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly));

        }

        string[] removedWorkspacePatchProperties =
        [
            "MaxTotalInputBytes",
            "MaxElapsedMilliseconds",
            "MaxFiles",
            "MaxHunks",
            "MaxLinesPerHunk",
            "MaxResultItems",
        ];

        foreach (string propertyName in removedWorkspacePatchProperties)
        {

            Assert.Null(
                typeof(WorkspacePatchSettings).GetProperty(
                    propertyName,
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly));

        }

    }

    [Fact]
    public void Encryption_diagnostics_has_no_total_file_scan_ceiling()
    {

        Assert.Null(
            typeof(EncryptedBlobDiagnostics).GetField(
                "ScanLimit",
                BindingFlags.NonPublic
                | BindingFlags.Static));

    }

    [Fact]
    public void Isolated_subagents_use_only_explicit_token_or_cost_budgets()
    {

        Assert.Null(
            typeof(SubagentRunRequest).GetProperty(
                "MaxTurns",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

        Assert.DoesNotContain(
            Enum.GetNames<DelegatedBudgetExhaustionReason>(),
            static name => string.Equals(
                name,
                "Turns",
                StringComparison.Ordinal));

        using System.Text.Json.JsonDocument schema =
            System.Text.Json.JsonDocument.Parse(
                new ArcanumDelegateTaskTool(
                    new NeverInvokedSubagentRunner(),
                    inheritedModel: null)
                .JsonSchema.GetRawText());

        Assert.False(
            schema.RootElement
                .GetProperty("properties")
                .TryGetProperty("max_turns", out _));

    }

    private sealed class NeverInvokedSubagentRunner : ISubagentRunner
    {

        public Task<SubagentRunResult> RunAsync(
            SubagentRunRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The contract test does not invoke the tool.");

    }

}
