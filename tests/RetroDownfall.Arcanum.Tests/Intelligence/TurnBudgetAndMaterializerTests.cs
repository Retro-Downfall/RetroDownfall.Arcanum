using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnBudgetAndMaterializerTests
{
    private const string Emoji = "\uD83D\uDE00";
    private const string TruncationMarkerBody = "[truncated: tool result exceeded token/byte budget]";
    private const string TruncationMarker = "\n" + TruncationMarkerBody;

    [Fact]
    public void TurnBudget_contract_has_no_workflow_count_members()
    {
        string[] forbiddenMemberNames =
        [
            "MaxToolRounds",
            "MaxModelCalls",
            "MaxToolCalls",
            "MaxToolCallsPerRound",
            "MaxSideEffectingToolCalls",
            "RemainingModelCalls",
            "RemainingToolRounds",
            "RemainingToolCalls",
            "TryConsumeModelCall",
            "TryConsumeToolRound",
            "TryConsumeToolCalls",
        ];
        HashSet<string> forbidden = new(
            forbiddenMemberNames,
            StringComparer.OrdinalIgnoreCase);
        Type budgetContract = typeof(ITurnBudget);
        Type? budgetImplementation = budgetContract.Assembly.GetType(
            "RetroDownfall.Arcanum.Core.Intelligence.TurnBudget");
        List<string> violations = [];

        foreach (Type type in new[] { budgetContract, budgetImplementation }
            .OfType<Type>())
        {
            violations.AddRange(type
                .GetProperties()
                .Where(property => forbidden.Contains(property.Name))
                .Select(property => $"{type.Name}.{property.Name}"));
            violations.AddRange(type
                .GetMethods()
                .Where(method => forbidden.Contains(method.Name))
                .Select(method => $"{type.Name}.{method.Name}"));
            violations.AddRange(type
                .GetConstructors()
                .SelectMany(static constructor => constructor.GetParameters())
                .Where(parameter => parameter.Name is not null
                    && forbidden.Contains(parameter.Name))
                .Select(parameter => $"{type.Name} constructor parameter {parameter.Name}"));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Turn_limit_defaults_have_no_workflow_count_constants()
    {
        Type? defaultsType = typeof(ITurnBudget).Assembly.GetType(
            "RetroDownfall.Arcanum.Core.Configuration.TurnLimitsDefaults");
        string[] workflowCountConstants = defaultsType?
            .GetFields()
            .Where(static field =>
                field.Name.Contains("ModelCall", StringComparison.Ordinal)
                || field.Name.Contains("ToolRound", StringComparison.Ordinal)
                || field.Name.Contains("ToolCall", StringComparison.Ordinal)
                || field.Name.Contains("SideEffect", StringComparison.Ordinal))
            .Select(static field => field.Name)
            .ToArray()
            ?? [];

        Assert.Empty(workflowCountConstants);
    }

    [Fact]
    public void ToolResultMaterializer_TruncatesAndMarks()
    {
        ToolResultMaterializer materializer = new();
        string huge = new('x', 20_000);

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            huge,
            new ToolResultMaterializerOptions(MaxTokens: 32));

        Assert.True(result.WasTruncated);
        Assert.Contains("[truncated", result.TextForModel, StringComparison.Ordinal);
        Assert.True(result.TextForModel.Length < huge.Length);
    }

    [Fact]
    public void ToolResultMaterializer_NullText_UsesDefaultBudgetsAndReturnsEmptyResult()
    {
        ToolResultMaterializer materializer = new();

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            null!);

        Assert.False(result.WasTruncated);
        Assert.Equal(string.Empty, result.TextForModel);
        Assert.Equal(0, result.OriginalCharLength);
        Assert.Equal(0, result.OriginalEstimatedTokens);
    }

    [Fact]
    public void ToolResultMaterializer_PrefixOnlyMode_PreservesMarkerWhenByteBudgetAllows()
    {
        ToolResultMaterializer materializer = new();
        string text = new('x', 100);

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            text,
            new ToolResultMaterializerOptions(
                MaxTokens: 16,
                MaxUtf8Bytes: 1_024,
                PreservePrefixAndSuffix: false));

        Assert.True(result.WasTruncated);
        Assert.EndsWith(TruncationMarker, result.TextForModel, StringComparison.Ordinal);
        Assert.DoesNotContain('…', result.TextForModel);
        AssertWithinBudgets(result.TextForModel, maxTokens: 16, maxUtf8Bytes: 1_024);
    }

    [Fact]
    public void ToolResultMaterializer_PrefixOnly_EmojiAtBoundary_DropsWholePairAndKeepsMarker()
    {
        ToolResultMaterializer materializer = new();
        string expectedPrefix = new('a', 27);
        string text = expectedPrefix + Emoji + new string('b', 80);

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            text,
            new ToolResultMaterializerOptions(
                MaxTokens: 20,
                MaxUtf8Bytes: 1_024,
                PreservePrefixAndSuffix: false));

        Assert.True(result.WasTruncated);
        Assert.Equal(expectedPrefix + TruncationMarker, result.TextForModel);
        AssertValidUtf8(result.TextForModel);
        AssertWithinBudgets(result.TextForModel, maxTokens: 20, maxUtf8Bytes: 1_024);
    }

    [Fact]
    public void ToolResultMaterializer_PreserveEnds_EmojiAtHeadBoundary_DropsWholePair()
    {
        ToolResultMaterializer materializer = new();
        string expectedHead = new('h', 32);
        string expectedTail = new('t', 32);
        string text = expectedHead + Emoji + new string('m', 60) + expectedTail;

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            text,
            new ToolResultMaterializerOptions(
                MaxTokens: 30,
                MaxUtf8Bytes: 1_024,
                PreservePrefixAndSuffix: true));

        Assert.True(result.WasTruncated);
        Assert.StartsWith(expectedHead + "\n…\n", result.TextForModel, StringComparison.Ordinal);
        Assert.EndsWith(expectedTail + TruncationMarker, result.TextForModel, StringComparison.Ordinal);
        AssertValidUtf8(result.TextForModel);
        AssertWithinBudgets(result.TextForModel, maxTokens: 30, maxUtf8Bytes: 1_024);
    }

    [Fact]
    public void ToolResultMaterializer_PreserveEnds_EmojiAtTailBoundary_KeepsWholePair()
    {
        ToolResultMaterializer materializer = new();
        string expectedTail = Emoji + new string('z', 30);
        string text = new string('a', 100) + expectedTail;

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            text,
            new ToolResultMaterializerOptions(
                MaxTokens: 30,
                MaxUtf8Bytes: 1_024,
                PreservePrefixAndSuffix: true));

        Assert.True(result.WasTruncated);
        Assert.EndsWith(expectedTail + TruncationMarker, result.TextForModel, StringComparison.Ordinal);
        Assert.Contains("\n…\n", result.TextForModel, StringComparison.Ordinal);
        AssertValidUtf8(result.TextForModel);
        AssertWithinBudgets(result.TextForModel, maxTokens: 30, maxUtf8Bytes: 1_024);
    }

    [Fact]
    public void ToolResultMaterializer_PreserveEnds_AsymmetricUtf8Budget_KeepsBothEndsWhenPossible()
    {
        ToolResultMaterializer materializer = new();
        string text = Emoji + new string('m', 100) + "z";

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            text,
            new ToolResultMaterializerOptions(
                MaxTokens: 20,
                MaxUtf8Bytes: 62,
                PreservePrefixAndSuffix: true));

        Assert.True(result.WasTruncated);
        Assert.Equal(
            Emoji + "\n…\nz" + TruncationMarker,
            result.TextForModel);
        AssertValidUtf8(result.TextForModel);
        AssertWithinBudgets(result.TextForModel, maxTokens: 20, maxUtf8Bytes: 62);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "[")]
    [InlineData(2, "[t")]
    [InlineData(3, "[tr")]
    [InlineData(4, "[tru")]
    public void ToolResultMaterializer_TinyByteBudget_UsesLargestBoundedMarkerFallback(
        int maxUtf8Bytes,
        string expected)
    {
        ToolResultMaterializer materializer = new();

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            Emoji + new string('x', 80),
            new ToolResultMaterializerOptions(
                MaxTokens: 1,
                MaxUtf8Bytes: maxUtf8Bytes,
                PreservePrefixAndSuffix: false));

        Assert.True(result.WasTruncated);
        Assert.Equal(expected, result.TextForModel);
        AssertValidUtf8(result.TextForModel);
        AssertWithinBudgets(
            result.TextForModel,
            maxTokens: 1,
            maxUtf8Bytes: maxUtf8Bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToolResultMaterializer_MalformedLoneSurrogate_RespectsTinyByteBudget(
        bool highSurrogate)
    {
        ToolResultMaterializer materializer = new();
        string malformed = new(
            highSurrogate ? '\uD83D' : '\uDE00',
            1);

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            malformed + new string('x', 80),
            new ToolResultMaterializerOptions(
                MaxTokens: 1,
                MaxUtf8Bytes: 2,
                PreservePrefixAndSuffix: false));

        Assert.True(result.WasTruncated);
        Assert.Equal("[t", result.TextForModel);
        AssertValidUtf8(result.TextForModel);
        AssertWithinBudgets(result.TextForModel, maxTokens: 1, maxUtf8Bytes: 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToolResultMaterializer_UntruncatedMalformedUtf16_ReturnsNormalizedString(
        bool highSurrogate)
    {
        ToolResultMaterializer materializer = new();
        string malformed = new(
            highSurrogate ? '\uD83D' : '\uDE00',
            1);

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            malformed,
            new ToolResultMaterializerOptions(
                MaxTokens: 1,
                MaxUtf8Bytes: 3,
                PreservePrefixAndSuffix: false));

        Assert.False(result.WasTruncated);
        Assert.Equal("\uFFFD", result.TextForModel);
        AssertValidUtf8(result.TextForModel);
        AssertWithinBudgets(result.TextForModel, maxTokens: 1, maxUtf8Bytes: 3);
    }

    [Fact]
    public void ToolResultMaterializer_MalformedUtf16_NormalizesBeforeFitChecks()
    {
        ToolResultMaterializer materializer = new();
        string malformed = new('\uD83D', 1);

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            malformed + "x",
            new ToolResultMaterializerOptions(
                MaxTokens: 1,
                MaxUtf8Bytes: 4,
                PreservePrefixAndSuffix: false));

        Assert.False(result.WasTruncated);
        Assert.Equal("\uFFFDx", result.TextForModel);
        AssertValidUtf8(result.TextForModel);
        AssertWithinBudgets(result.TextForModel, maxTokens: 1, maxUtf8Bytes: 4);
    }

    [Fact]
    public void ToolResultMaterializer_CompleteMarkerAtExactBudget_StaysComplete()
    {
        ToolResultMaterializer materializer = new();

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            new string('x', 100),
            new ToolResultMaterializerOptions(
                MaxTokens: 13,
                MaxUtf8Bytes: TruncationMarkerBody.Length,
                PreservePrefixAndSuffix: false));

        Assert.True(result.WasTruncated);
        Assert.Equal(TruncationMarkerBody, result.TextForModel);
        AssertWithinBudgets(
            result.TextForModel,
            maxTokens: 13,
            maxUtf8Bytes: TruncationMarkerBody.Length);
    }

    [Fact]
    public void ToolResultMaterializer_TokenBudgetIncludesCompleteMarker()
    {
        ToolResultMaterializer materializer = new();

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            new string('x', 1_000),
            new ToolResultMaterializerOptions(
                MaxTokens: 16,
                MaxUtf8Bytes: 1_024,
                PreservePrefixAndSuffix: false));

        Assert.True(result.WasTruncated);
        Assert.EndsWith(TruncationMarker, result.TextForModel, StringComparison.Ordinal);
        AssertWithinBudgets(result.TextForModel, maxTokens: 16, maxUtf8Bytes: 1_024);
    }

    [Fact]
    public void ToolResultMaterializer_ByteBudgetIncludesCompleteMarker()
    {
        ToolResultMaterializer materializer = new();

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            new string('x', 1_000),
            new ToolResultMaterializerOptions(
                MaxTokens: 1_024,
                MaxUtf8Bytes: 60,
                PreservePrefixAndSuffix: false));

        Assert.True(result.WasTruncated);
        Assert.EndsWith(TruncationMarker, result.TextForModel, StringComparison.Ordinal);
        AssertWithinBudgets(result.TextForModel, maxTokens: 1_024, maxUtf8Bytes: 60);
    }

    [Fact]
    public void ToolResultMaterializer_NegativeBudgets_ClampToEmptyValidOutput()
    {
        ToolResultMaterializer materializer = new();

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            "é",
            new ToolResultMaterializerOptions(
                MaxTokens: -1,
                MaxUtf8Bytes: -1,
                PreservePrefixAndSuffix: false));

        Assert.True(result.WasTruncated);
        Assert.Equal(string.Empty, result.TextForModel);
        Assert.Equal(1, result.OriginalCharLength);
        Assert.Equal(1, result.OriginalEstimatedTokens);
    }

    [Fact]
    public void ToolResultMaterializer_MaximumTokenBudget_DoesNotOverflowDefaultByteBudget()
    {
        ToolResultMaterializer materializer = new();

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            "small",
            new ToolResultMaterializerOptions(MaxTokens: int.MaxValue));

        Assert.False(result.WasTruncated);
        Assert.Equal("small", result.TextForModel);
    }

    [Fact]
    public void ToolResultMaterializer_MaximumTokenBudget_WithTinyByteBudget_DoesNotOverflow()
    {
        ToolResultMaterializer materializer = new();

        ToolResultMaterialization result = materializer.Materialize(
            "read_file",
            "small",
            new ToolResultMaterializerOptions(
                MaxTokens: int.MaxValue,
                MaxUtf8Bytes: 1,
                PreservePrefixAndSuffix: false));

        Assert.True(result.WasTruncated);
        Assert.Equal("[", result.TextForModel);
        AssertWithinBudgets(
            result.TextForModel,
            maxTokens: int.MaxValue,
            maxUtf8Bytes: 1);
    }

    private static void AssertValidUtf8(string text)
    {
        var strictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        _ = strictUtf8.GetBytes(text);
    }

    private static void AssertWithinBudgets(
        string text,
        int maxTokens,
        int maxUtf8Bytes)
    {
        int estimatedTokens =
            string.IsNullOrEmpty(text)
                ? 0
                : (int)Math.Min(
                    int.MaxValue,
                    ((long)text.Length + 3L) / 4L);

        Assert.True(
            estimatedTokens <= maxTokens,
            $"Estimated tokens {estimatedTokens} exceeded {maxTokens}.");
        Assert.True(
            Encoding.UTF8.GetByteCount(text) <= maxUtf8Bytes,
            $"UTF-8 bytes exceeded {maxUtf8Bytes}.");
    }

    [Fact]
    public void ToolResultMaterializer_StructuredResultWithinBudget_ReturnsCompleteJson()
    {
        ToolResultMaterializer materializer = new();
        WorkspaceSearchToolResultEnvelope envelope = new()
        {
            Matches = [new WorkspaceSearchToolResultItem("a.cs", 2, 3, "match")],
            TotalMatchCount = 1,
        };

        ToolResultMaterialization result = materializer.MaterializeStructured(
            ToolRiskClassifier.SearchWorkspaceToolName,
            envelope,
            McpJsonSerializerContext.Default.WorkspaceSearchToolResultEnvelope);

        using JsonDocument parsed = JsonDocument.Parse(result.TextForModel);

        Assert.False(result.WasTruncated);
        Assert.Equal(1, parsed.RootElement.GetProperty("matches").GetArrayLength());
        Assert.False(parsed.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData(BrokenRetainBehavior.ReturnsNull)]
    [InlineData(BrokenRetainBehavior.ReturnsWrongCount)]
    public void ToolResultMaterializer_BrokenStructuredRetentionContract_Throws(
        BrokenRetainBehavior behavior)
    {
        ToolResultMaterializer materializer = new();
        JsonSerializerOptions serializerOptions = new()
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        JsonTypeInfo<BrokenStructuredResult> typeInfo =
            (JsonTypeInfo<BrokenStructuredResult>)serializerOptions.GetTypeInfo(
                typeof(BrokenStructuredResult));
        BrokenStructuredResult result = new(
            ItemCount: 1,
            Payload: new string('x', 128),
            Behavior: behavior);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => materializer.MaterializeStructured(
                "broken",
                result,
                typeInfo,
                new ToolResultMaterializerOptions(MaxTokens: 1, MaxUtf8Bytes: 1)));

        Assert.Contains(
            "must retain exactly the requested leading item count",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ToolResultMaterializer_StructuredResult_TrimsItemsWithoutTruncatingJson()
    {
        ToolResultMaterializer materializer = new();
        WorkspaceSearchToolResultEnvelope envelope = new()
        {
            Matches = Enumerable.Range(1, 12)
                .Select(static line => new WorkspaceSearchToolResultItem(
                    "src/quoted\"file.cs",
                    line,
                    3,
                    $"match {line}\nwith escaped text"))
                .ToArray(),
            TotalMatchCount = 12,
        };

        ToolResultMaterialization result = materializer.MaterializeStructured(
            ToolRiskClassifier.SearchWorkspaceToolName,
            envelope,
            McpJsonSerializerContext.Default.WorkspaceSearchToolResultEnvelope,
            new ToolResultMaterializerOptions(MaxTokens: 10_000, MaxUtf8Bytes: 420));

        using JsonDocument parsed = JsonDocument.Parse(result.TextForModel);
        JsonElement root = parsed.RootElement;
        int retained = root.GetProperty("matches").GetArrayLength();

        Assert.True(result.WasTruncated);
        Assert.InRange(retained, 1, 11);
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(12 - retained, root.GetProperty("omittedMatchCount").GetInt32());
        Assert.DoesNotContain("[truncated", result.TextForModel, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.TextForModel) <= 420);
    }

    [Fact]
    public void ToolResultMaterializer_StructuredResult_UsesMinimalValidFallbackWhenEmptyEnvelopeCannotFit()
    {
        ToolResultMaterializer materializer = new();
        WorkspaceSearchToolResultEnvelope envelope = new()
        {
            Status = new string('x', 1000),
            Matches = [new WorkspaceSearchToolResultItem("a.cs", 1, 1, "match")],
            TotalMatchCount = 1,
        };

        ToolResultMaterialization result = materializer.MaterializeStructured(
            ToolRiskClassifier.SearchWorkspaceToolName,
            envelope,
            McpJsonSerializerContext.Default.WorkspaceSearchToolResultEnvelope,
            new ToolResultMaterializerOptions(MaxTokens: 1, MaxUtf8Bytes: 1));

        using JsonDocument parsed = JsonDocument.Parse(result.TextForModel);

        Assert.True(result.WasTruncated);
        Assert.Equal("truncated", parsed.RootElement.GetProperty("status").GetString());
        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
    }

    public enum BrokenRetainBehavior
    {
        ReturnsNull,
        ReturnsWrongCount,
    }

    public sealed record BrokenStructuredResult(
        int ItemCount,
        string Payload,
        BrokenRetainBehavior Behavior)
        : IStructuredToolResult<BrokenStructuredResult>
    {
        [JsonIgnore]
        public int MaterializationItemCount => ItemCount;

        public BrokenStructuredResult RetainLeadingItems(int itemCount) =>
            Behavior == BrokenRetainBehavior.ReturnsNull
                ? null!
                : this with { ItemCount = itemCount + 1 };
    }

    [Fact]
    public void McpJsonSerializerContext_RegistersStructuredToolResultContracts()
    {
        Assert.NotNull(McpJsonSerializerContext.Default.WorkspaceSearchToolResultEnvelope);
        Assert.NotNull(McpJsonSerializerContext.Default.WorkspacePatchToolResultEnvelope);
        Assert.NotNull(McpJsonSerializerContext.Default.WorkspaceCheckToolResultEnvelope);
        Assert.NotNull(McpJsonSerializerContext.Default.MinimalStructuredToolResultEnvelope);
    }

    [Fact]
    public void ToolExecutionPipeline_routes_search_results_through_structured_materialization()
    {
        WorkspaceSearchToolResultEnvelope envelope = new()
        {
            Matches = Enumerable.Range(1, 20)
                .Select(static line => new WorkspaceSearchToolResultItem(
                    "src/quoted\"file.cs",
                    line,
                    1,
                    $"needle {line} with a long preview"))
                .ToArray(),
            TotalMatchCount = 20,
        };
        string raw = JsonSerializer.Serialize(
            envelope,
            McpJsonSerializerContext.Default.WorkspaceSearchToolResultEnvelope);

        string materialized = ToolExecutionPipeline.MaterializeToolResultForModel(
            ToolRiskClassifier.SearchWorkspaceToolName,
            new TrustedStructuredToolResult(
                TrustedStructuredToolResultKind.WorkspaceSearch,
                raw),
            new ToolResultMaterializer(),
            new ToolResultMaterializerOptions(MaxTokens: 10_000, MaxUtf8Bytes: 420));

        using JsonDocument parsed = JsonDocument.Parse(materialized);

        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
        Assert.InRange(parsed.RootElement.GetProperty("matches").GetArrayLength(), 1, 19);
        Assert.DoesNotContain("[truncated", materialized, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(materialized) <= 420);
    }

    [Fact]
    public void ToolExecutionPipeline_routes_check_results_through_structured_materialization()
    {

        WorkspaceCheckToolResultEnvelope envelope = new()
        {
            Status = "failed",
            ProfileId = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
            SelectedSdkVersion = "10.0.302",
            Diagnostics = Enumerable.Range(1, 20)
                .Select(static line => new WorkspaceCheckToolResultItem(
                    "src/quoted\"file.cs",
                    line,
                    2,
                    "error",
                    "CS1002",
                    $"diagnostic {line} with a bounded message"))
                .ToArray(),
            TotalDiagnosticCount = 20,
            ErrorCount = 20,
            ExitCode = 1,
        };
        string raw = JsonSerializer.Serialize(
            envelope,
            McpJsonSerializerContext.Default.WorkspaceCheckToolResultEnvelope);

        string materialized = ToolExecutionPipeline.MaterializeToolResultForModel(
            ToolRiskClassifier.WorkspaceCheckToolName,
            new TrustedStructuredToolResult(
                TrustedStructuredToolResultKind.WorkspaceCheck,
                raw),
            new ToolResultMaterializer(),
            new ToolResultMaterializerOptions(
                MaxTokens: 10_000,
                MaxUtf8Bytes: 520));

        using JsonDocument parsed = JsonDocument.Parse(materialized);

        Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
        Assert.InRange(
            parsed.RootElement.GetProperty("diagnostics").GetArrayLength(),
            1,
            19);
        Assert.Equal(
            "10.0.302",
            parsed.RootElement.GetProperty("selectedSdkVersion").GetString());
        Assert.DoesNotContain(
            "[truncated",
            materialized,
            StringComparison.Ordinal);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(materialized) <= 520);
    }

    [Fact]
    public void Workspace_check_materialization_drops_raw_streams_before_diagnostics()
    {

        WorkspaceCheckToolResultEnvelope envelope = new()
        {
            Status = "failed",
            Code = "check_failed",
            ProfileId = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
            SelectedSdkVersion = "10.0.302",
            Diagnostics =
            [
                new WorkspaceCheckToolResultItem(
                    "src/One.cs",
                    1,
                    2,
                    "error",
                    "CS1001",
                    "first actionable diagnostic"),
                new WorkspaceCheckToolResultItem(
                    "src/Two.cs",
                    3,
                    4,
                    "error",
                    "CS1002",
                    "second actionable diagnostic"),
            ],
            TotalDiagnosticCount = 2,
            ErrorCount = 2,
            ExitCode = 1,
            StandardOutput = new string('o', 32_000),
            StandardError = new string('e', 32_000),
        };
        string raw = JsonSerializer.Serialize(
            envelope,
            McpJsonSerializerContext.Default
                .WorkspaceCheckToolResultEnvelope);

        string materialized =
            ToolExecutionPipeline.MaterializeToolResultForModel(
                ToolRiskClassifier.WorkspaceCheckToolName,
                new TrustedStructuredToolResult(
                    TrustedStructuredToolResultKind.WorkspaceCheck,
                    raw),
                new ToolResultMaterializer(),
                new ToolResultMaterializerOptions(
                    MaxTokens: 10_000,
                    MaxUtf8Bytes: 620));

        using JsonDocument parsed = JsonDocument.Parse(materialized);
        JsonElement root = parsed.RootElement;

        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("totalDiagnosticCount").GetInt32());
        Assert.Equal(2, root.GetProperty("errorCount").GetInt32());
        Assert.InRange(
            root.GetProperty("diagnostics").GetArrayLength(),
            1,
            2);
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(
            !root.TryGetProperty("standardOutput", out JsonElement stdout)
            || stdout.ValueKind == JsonValueKind.Null);
        Assert.True(
            !root.TryGetProperty("standardError", out JsonElement stderr)
            || stderr.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void ToolExecutionPipeline_keeps_receipt_handled_patch_result_byte_exact()
    {

        const string frozen =
            "{\"omittedFileCount\":0,\"totalFileCount\":1,\"truncated\":false,\"files\":[{\"path\":\"quoted\\\".txt\",\"operation\":\"modify\",\"status\":\"applied\",\"appliedHunks\":1}],\"status\":\"ok\"}";

        string materialized = ToolExecutionPipeline.MaterializeToolResultForModel(
            ToolRiskClassifier.ApplyPatchToolName,
            new TrustedStructuredToolResult(
                TrustedStructuredToolResultKind.WorkspacePatch,
                frozen,
                ReceiptHandled: true),
            new ToolResultMaterializer(),
            new ToolResultMaterializerOptions(
                MaxTokens: 1,
                MaxUtf8Bytes: 1));

        Assert.Equal(frozen, materialized);

    }

    [Fact]
    public void ToolExecutionPipeline_keeps_colliding_external_search_tool_result_opaque()
    {
        WorkspaceSearchToolResultEnvelope envelope = new()
        {
            Matches = Enumerable.Range(1, 20)
                .Select(static line => new WorkspaceSearchToolResultItem(
                    "external.cs",
                    line,
                    1,
                    $"external payload {line}"))
                .ToArray(),
            TotalMatchCount = 20,
        };
        string raw = JsonSerializer.Serialize(
            envelope,
            McpJsonSerializerContext.Default.WorkspaceSearchToolResultEnvelope);

        string materialized = ToolExecutionPipeline.MaterializeToolResultForModel(
            ToolRiskClassifier.SearchWorkspaceToolName,
            raw,
            new ToolResultMaterializer(),
            new ToolResultMaterializerOptions(MaxTokens: 10_000, MaxUtf8Bytes: 420));

        Assert.NotEqual(raw, materialized);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(materialized));
    }

    [Fact]
    public void StructuredResultEnvelopes_RetainLeadingItemsAndUpdateOmissionCounters()
    {
        WorkspacePatchToolResultEnvelope patch = new()
        {
            Files =
            [
                new WorkspacePatchToolResultItem("a.cs", "modify", "applied", 1),
                new WorkspacePatchToolResultItem("b.cs", "create", "applied", 1),
            ],
            TotalFileCount = 2,
        };
        WorkspaceCheckToolResultEnvelope check = new()
        {
            Diagnostics =
            [
                new WorkspaceCheckToolResultItem("a.cs", 1, 2, "error", "CS0001", "first"),
                new WorkspaceCheckToolResultItem("b.cs", 3, 4, "warning", "CS0002", "second"),
            ],
            TotalDiagnosticCount = 2,
        };

        WorkspacePatchToolResultEnvelope trimmedPatch = patch.RetainLeadingItems(1);
        WorkspaceCheckToolResultEnvelope trimmedCheck = check.RetainLeadingItems(0);

        Assert.Single(trimmedPatch.Files);
        Assert.Equal(1, trimmedPatch.OmittedFileCount);
        Assert.True(trimmedPatch.Truncated);
        Assert.Empty(trimmedCheck.Diagnostics);
        Assert.Equal(2, trimmedCheck.OmittedDiagnosticCount);
        Assert.True(trimmedCheck.Truncated);
    }

    [Fact]
    public void WorkspacePatchResult_independently_caps_files_affected_and_recovery_paths()
    {
        WorkspacePatchToolResultEnvelope patch = new()
        {
            Status = "rollback_incomplete",
            Code = "rollback_incomplete",
            Files =
            [
                new WorkspacePatchToolResultItem("a.cs", "modify", "recovery_required", 1),
                new WorkspacePatchToolResultItem("b.cs", "delete", "recovery_required", 1),
            ],
            TotalFileCount = 2,
            AffectedPaths = ["a.cs", "b.cs", "c.cs"],
            TotalAffectedPathCount = 3,
            RecoveryArtifactPaths =
            [
                ".a.cs.arcanum-backup",
                ".b.cs.arcanum-backup",
                ".c.cs.arcanum-backup",
                ".d.cs.arcanum-backup",
            ],
            TotalRecoveryArtifactPathCount = 4,
        };

        WorkspacePatchToolResultEnvelope retained =
            patch.RetainLeadingItems(1);

        Assert.Equal("rollback_incomplete", retained.Status);
        Assert.Equal("rollback_incomplete", retained.Code);
        Assert.Single(retained.Files);
        Assert.Single(retained.AffectedPaths);
        Assert.Single(retained.RecoveryArtifactPaths);
        Assert.Equal(1, retained.OmittedFileCount);
        Assert.Equal(2, retained.OmittedAffectedPathCount);
        Assert.Equal(3, retained.OmittedRecoveryArtifactPathCount);
        Assert.True(retained.Truncated);
    }

}
