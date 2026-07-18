using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Models.DiagnosticMcp;
using RetroDownfall.TheForge.Core.Serialization;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class TheForgeJsonContextTests
{

    [Fact]
    public void ApiResponse_SpellDetail_RoundTrips()
    {

        SpellDetail spell = new(
            Name: "greater-heal",
            Description: "Heals a target apprentice.",
            Source: SpellSource.Workspace,
            Tags: ["healing", "support"],
            SystemPrompt: "You are a healer.",
            Template: null,
            Body: "# Greater Heal",
            Model: "gpt-4o",
            Provider: "openai",
            Tools: ["search_web"],
            RequiredMcpServers: [],
            WorkingDirectory: "/tmp/campaign",
            FilePath: "/tmp/campaign/spells/greater-heal/SPELL.md");

        ApiResponse<SpellDetail> original = new(spell, true, null, "trace-123");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseSpellDetail);

        ApiResponse<SpellDetail>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseSpellDetail);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.Equal("trace-123", roundTripped.TraceId);

        Assert.NotNull(roundTripped.Data);

        Assert.Equal("greater-heal", roundTripped.Data.Name);

        Assert.Equal(SpellSource.Workspace, roundTripped.Data.Source);

        Assert.Equal(["healing", "support"], roundTripped.Data.Tags);

    }

    [Fact]
    public void ApiResponse_Failure_OmitsDataAndCarriesError()
    {

        ApiResponse<bool> failure = new(default, false, new Error("Connection.Failed", "boom"), "trace-456");

        string json = JsonSerializer.Serialize(failure, TheForgeJsonContext.Default.ApiResponseBoolean);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.TryGetProperty("data", out _));

        Assert.Equal("Connection.Failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());

        ApiResponse<bool>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(roundTripped);

        Assert.False(roundTripped.IsSuccess);

        Assert.Equal("boom", roundTripped.Error?.Message);

    }

    [Theory]
    [InlineData("""{"type":"token","message":"","data":"Hello"}""", IntelligenceEventType.Token, "Hello")]
    [InlineData("""{"type":"sessionBound","message":"session bound"}""", IntelligenceEventType.SessionBound, null)]
    [InlineData("""{"type":"conversationBound","message":"deprecated alias"}""", IntelligenceEventType.ConversationBound, null)]
    [InlineData("""{"type":"toolError","message":"boom"}""", IntelligenceEventType.ToolError, null)]
    public void IntelligenceEvent_DeserializesEachNdjsonFrameType(string ndjsonLine, IntelligenceEventType expectedType, string? expectedData)
    {

        IntelligenceEvent? frame = JsonSerializer.Deserialize(ndjsonLine, TheForgeJsonContext.Default.IntelligenceEvent);

        Assert.NotNull(frame);

        Assert.Equal(expectedType, frame.Type);

        Assert.Equal(expectedData, frame.Data);

    }

    [Fact]
    public void IntelligenceEvent_ResultFrame_CarriesUsage()
    {

        const string ndjsonLine =
            """{"type":"result","message":"done","usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""";

        IntelligenceEvent? frame = JsonSerializer.Deserialize(ndjsonLine, TheForgeJsonContext.Default.IntelligenceEvent);

        Assert.NotNull(frame);

        Assert.Equal(IntelligenceEventType.Result, frame.Type);

        Assert.NotNull(frame.Usage);

        Assert.Equal(10, frame.Usage.PromptTokens);

        Assert.Equal(15, frame.Usage.TotalTokens);

    }

    [Fact]
    public void CreateSpellRequest_RoundTrips()
    {

        CreateSpellRequest request = new(
            Name: "light",
            Description: "Create light",
            Tags: ["cantrip"],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: [],
            RequiredMcpServers: [],
            Body: "# Light");

        string json = JsonSerializer.Serialize(request, TheForgeJsonContext.Default.CreateSpellRequest);

        CreateSpellRequest? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.CreateSpellRequest);

        Assert.NotNull(roundTripped);

        Assert.Equal("light", roundTripped.Name);

        Assert.Equal(["cantrip"], roundTripped.Tags);

        Assert.Equal("# Light", roundTripped.Body);

    }

    [Fact]
    public void CreatePromptRequest_RoundTrips()
    {

        CreatePromptRequest request = new(
            Name: "greeting",
            Version: "v1",
            Template: "Hello {{name}}",
            Description: "Say hello",
            Tags: ["social"],
            ParameterSchema: null,
            DefaultParameters: null,
            Model: "gpt-4o",
            Provider: "openai",
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            CampaignId: null);

        string json = JsonSerializer.Serialize(request, TheForgeJsonContext.Default.CreatePromptRequest);

        CreatePromptRequest? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.CreatePromptRequest);

        Assert.NotNull(roundTripped);

        Assert.Equal("greeting", roundTripped.Name);

        Assert.Equal("v1", roundTripped.Version);

        Assert.Equal("Hello {{name}}", roundTripped.Template);

        Assert.Equal("gpt-4o", roundTripped.Model);

    }

    [Fact]
    public void CreateSessionRequest_RoundTrips()
    {

        CreateSessionRequest request = new(CampaignId: null, Title: "My session");

        string json = JsonSerializer.Serialize(request, TheForgeJsonContext.Default.CreateSessionRequest);

        CreateSessionRequest? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.CreateSessionRequest);

        Assert.NotNull(roundTripped);

        Assert.Equal("My session", roundTripped.Title);

    }

    [Fact]
    public void ApiResponse_PromptDetailDto_RoundTrips()
    {

        PromptDetailDto prompt = new(
            Id: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            CampaignId: null,
            Name: "greeting",
            Version: "v1",
            Description: "Say hello",
            Tags: ["social"],
            Template: "Hello {{name}}",
            ParameterSchema: null,
            DefaultParameters: null,
            Model: "gpt-4o",
            Provider: "openai",
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        ApiResponse<PromptDetailDto> original = new(prompt, true, null, "trace-p");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponsePromptDetailDto);

        ApiResponse<PromptDetailDto>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponsePromptDetailDto);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.Equal("greeting", roundTripped.Data.Name);

        Assert.Equal("Hello {{name}}", roundTripped.Data.Template);

    }

    [Fact]
    public void ApiResponse_SessionDetailDto_RoundTrips()
    {

        SessionDetailDto session = new(
            Id: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            CampaignId: null,
            Title: "My session",
            Status: "active",
            EntryCount: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Summary: null,
            TotalTokensUsed: 0);

        ApiResponse<SessionDetailDto> original = new(session, true, null, "trace-s");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseSessionDetailDto);

        ApiResponse<SessionDetailDto>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.Equal("My session", roundTripped.Data.Title);

        Assert.Equal("active", roundTripped.Data.Status);

    }

    [Fact]
    public void PromptRenderResultDto_RoundTrips()
    {

        PromptRenderResultDto result = new(RenderedText: "Hello world", TokenCount: 7);

        string json = JsonSerializer.Serialize(result, TheForgeJsonContext.Default.PromptRenderResultDto);

        PromptRenderResultDto? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.PromptRenderResultDto);

        Assert.NotNull(roundTripped);

        Assert.Equal("Hello world", roundTripped.RenderedText);

        Assert.Equal(7, roundTripped.TokenCount);

    }

    [Fact]
    public void PromptTestResultDto_RoundTrips()
    {

        PromptTestResultDto result = new(
            AssembledText: "Assembled prompt",
            TokenCount: 12,
            ResolvedSpell: new ResolvedSpellInfoDto("greeting", "v1"),
            McpServerCount: 2);

        string json = JsonSerializer.Serialize(result, TheForgeJsonContext.Default.PromptTestResultDto);

        PromptTestResultDto? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.PromptTestResultDto);

        Assert.NotNull(roundTripped);

        Assert.Equal("Assembled prompt", roundTripped.AssembledText);

        Assert.Equal(12, roundTripped.TokenCount);

        Assert.NotNull(roundTripped.ResolvedSpell);

        Assert.Equal("greeting", roundTripped.ResolvedSpell.Name);

        Assert.Equal(2, roundTripped.McpServerCount);

    }

    [Fact]
    public void ToolInvokeRequest_RoundTrips()
    {

        ToolInvokeRequest request = new("echo", JsonDocument.Parse("""{"path":"/tmp"}""").RootElement.Clone());

        string json = JsonSerializer.Serialize(request, TheForgeJsonContext.Default.ToolInvokeRequest);

        ToolInvokeRequest? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ToolInvokeRequest);

        Assert.NotNull(roundTripped);

        Assert.Equal("echo", roundTripped.ToolName);

        Assert.Equal("/tmp", roundTripped.Arguments.GetProperty("path").GetString());

    }

    [Fact]
    public void ApiResponse_ToolInvokeResponse_RoundTrips()
    {

        ToolInvokeResponse result = new(JsonDocument.Parse("""{"ok":true}""").RootElement.Clone());

        ApiResponse<ToolInvokeResponse> original = new(result, true, null, "trace-ti");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseToolInvokeResponse);

        ApiResponse<ToolInvokeResponse>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseToolInvokeResponse);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.Equal(JsonValueKind.Object, roundTripped.Data!.Result.ValueKind);

        Assert.True(roundTripped.Data.Result.GetProperty("ok").GetBoolean());

    }

    [Fact]
    public void McpToolInvokeRequest_RoundTrips_WithServerAndWorkspace()
    {

        McpToolInvokeRequest request = new()
        {
            ToolName = "echo",
            Arguments = JsonDocument.Parse("""{"path":"/tmp"}""").RootElement.Clone(),
            ServerName = "srv-a",
            WorkingDirectory = "/ws",
        };

        string json = JsonSerializer.Serialize(request, TheForgeJsonContext.Default.McpToolInvokeRequest);

        Assert.Contains("\"serverName\":\"srv-a\"", json);
        Assert.Contains("\"workingDirectory\":\"/ws\"", json);

        McpToolInvokeRequest? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.McpToolInvokeRequest);

        Assert.NotNull(roundTripped);
        Assert.Equal("echo", roundTripped!.ToolName);
        Assert.Equal("srv-a", roundTripped.ServerName);
        Assert.Equal("/ws", roundTripped.WorkingDirectory);
        Assert.Equal("/tmp", roundTripped.Arguments.GetProperty("path").GetString());

    }

    [Fact]
    public void McpToolInvokeRequest_OmitsNullServerAndWorkspace()
    {

        McpToolInvokeRequest request = new()
        {
            ToolName = "echo",
            Arguments = JsonDocument.Parse("{}").RootElement.Clone(),
        };

        string json = JsonSerializer.Serialize(request, TheForgeJsonContext.Default.McpToolInvokeRequest);

        Assert.DoesNotContain("serverName", json);
        Assert.DoesNotContain("workingDirectory", json);

    }

    [Fact]
    public void ApiResponse_McpToolInvokeResponse_RoundTrips()
    {

        McpToolInvokeResponse result = new()
        {
            Result = JsonDocument.Parse("""{"ok":true}""").RootElement.Clone(),
            ServerName = "srv-a",
            ToolName = "echo",
            DurationMs = 42,
            Truncated = true,
        };

        ApiResponse<McpToolInvokeResponse> original = new(result, true, null, "trace-mcp");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseMcpToolInvokeResponse);

        ApiResponse<McpToolInvokeResponse>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseMcpToolInvokeResponse);

        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.IsSuccess);
        Assert.NotNull(roundTripped.Data);
        Assert.Equal("srv-a", roundTripped.Data!.ServerName);
        Assert.Equal("echo", roundTripped.Data.ToolName);
        Assert.Equal(42, roundTripped.Data.DurationMs);
        Assert.True(roundTripped.Data.Truncated);
        Assert.True(roundTripped.Data.Result.GetProperty("ok").GetBoolean());

    }

    [Fact]
    public void DiagnosticMcpFixtureStoreDocument_RoundTrips_WithCamelCase()
    {

        DiagnosticMcpFixtureRecord fixture = new(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "echo-fixture",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "echo",
            "srv-a",
            "/ws",
            "{}",
            null,
            null);

        DiagnosticMcpFixtureStoreDocument original = new(1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [fixture]);

        string json = JsonSerializer.Serialize(original, TheForgeDiagnosticMcpFixturesJsonContext.Default.DiagnosticMcpFixtureStoreDocument);

        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"createdAt\"", json);
        Assert.Contains("\"updatedAt\"", json);
        Assert.Contains("\"fixtures\"", json);
        Assert.Contains("\"toolName\":\"echo\"", json);
        Assert.Contains("\"serverName\":\"srv-a\"", json);

        DiagnosticMcpFixtureStoreDocument? roundTripped = JsonSerializer.Deserialize(json, TheForgeDiagnosticMcpFixturesJsonContext.Default.DiagnosticMcpFixtureStoreDocument);

        Assert.NotNull(roundTripped);
        Assert.Equal(1, roundTripped!.SchemaVersion);
        Assert.Single(roundTripped.Fixtures);
        Assert.Equal("echo-fixture", roundTripped.Fixtures[0].Name);
        Assert.Equal("echo", roundTripped.Fixtures[0].ToolName);

    }

    [Fact]
    public void ProviderTestRequest_RoundTrips()
    {

        ProviderTestRequest request = new("http://localhost:8080", "sk-test", AiProviderKind.OpenAICompatible);

        string json = JsonSerializer.Serialize(request, TheForgeJsonContext.Default.ProviderTestRequest);

        ProviderTestRequest? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ProviderTestRequest);

        Assert.NotNull(roundTripped);

        Assert.Equal("http://localhost:8080", roundTripped.Endpoint);

        Assert.Equal(AiProviderKind.OpenAICompatible, roundTripped.Type);

    }

    [Fact]
    public void ApiResponse_ProviderTestResult_RoundTrips()
    {

        ProviderTestResult result = new(true, 42, ["gpt-4o"], null);

        ApiResponse<ProviderTestResult> original = new(result, true, null, "trace-pt");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseProviderTestResult);

        ApiResponse<ProviderTestResult>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseProviderTestResult);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.True(roundTripped.Data!.IsReachable);

        Assert.Equal(42L, roundTripped.Data.LatencyMs);

        Assert.Equal(["gpt-4o"], roundTripped.Data.ModelsFound);

    }

    [Fact]
    public void OptionalWorkspaceRequest_RoundTripsNonNullAndNull()
    {

        OptionalWorkspaceRequest nonNull = new("/tmp/campaign");

        string json = JsonSerializer.Serialize(nonNull, TheForgeJsonContext.Default.OptionalWorkspaceRequest);

        OptionalWorkspaceRequest? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.OptionalWorkspaceRequest);

        Assert.NotNull(roundTripped);

        Assert.Equal("/tmp/campaign", roundTripped.WorkingDirectory);

        string nullJson = JsonSerializer.Serialize(new OptionalWorkspaceRequest(null), TheForgeJsonContext.Default.OptionalWorkspaceRequest);

        OptionalWorkspaceRequest? nullRoundTripped = JsonSerializer.Deserialize(nullJson, TheForgeJsonContext.Default.OptionalWorkspaceRequest);

        Assert.NotNull(nullRoundTripped);

        Assert.Null(nullRoundTripped.WorkingDirectory);

    }

    [Fact]
    public void WorkspaceArsenalDto_RoundTrips()
    {

        WorkspaceArsenalDto arsenal = new([], ["fs.read", "fs.write"], [], []);

        string json = JsonSerializer.Serialize(arsenal, TheForgeJsonContext.Default.WorkspaceArsenalDto);

        WorkspaceArsenalDto? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.WorkspaceArsenalDto);

        Assert.NotNull(roundTripped);

        Assert.Equal(["fs.read", "fs.write"], roundTripped.NativeTools);

        ApiResponse<WorkspaceArsenalDto> original = new(arsenal, true, null, "trace-a");

        string envelopeJson = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseWorkspaceArsenalDto);

        ApiResponse<WorkspaceArsenalDto>? envelopeRoundTripped = JsonSerializer.Deserialize(envelopeJson, TheForgeJsonContext.Default.ApiResponseWorkspaceArsenalDto);

        Assert.NotNull(envelopeRoundTripped);

        Assert.True(envelopeRoundTripped.IsSuccess);

        Assert.NotNull(envelopeRoundTripped.Data);

        Assert.Equal(["fs.read", "fs.write"], envelopeRoundTripped.Data!.NativeTools);

    }

    [Fact]
    public void CompactResult_RoundTrips()
    {

        CompactResult original = new(120, 45, 5);

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.CompactResult);

        CompactResult? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.CompactResult);

        Assert.NotNull(roundTripped);

        Assert.Equal(120, roundTripped.TokensBefore);

        Assert.Equal(45, roundTripped.TokensAfter);

        Assert.Equal(5, roundTripped.EntriesRemoved);

    }

    [Fact]
    public void ApiResponse_CompactResult_RoundTrips()
    {

        CompactResult compact = new(80, 30, 2);

        ApiResponse<CompactResult> original = new(compact, true, null, "trace-compact");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseCompactResult);

        ApiResponse<CompactResult>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseCompactResult);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.Equal(2, roundTripped.Data!.EntriesRemoved);

    }

    [Fact]
    public void ApiResponse_EntryDtoArray_RoundTrips()
    {

        EntryDto[] entries =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), "user", "hello", null, null, DateTimeOffset.UtcNow),
        ];

        ApiResponse<EntryDto[]> original = new(entries, true, null, "trace-entries");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseEntryDtoArray);

        ApiResponse<EntryDto[]>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseEntryDtoArray);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.Single(roundTripped.Data!);

        Assert.Equal("hello", roundTripped.Data![0].Content);

    }

    [Fact]
    public void SagaStats_RoundTrips()
    {

        SagaStats original = new(10, 3, DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow);

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.SagaStats);

        SagaStats? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.SagaStats);

        Assert.NotNull(roundTripped);

        Assert.Equal(10, roundTripped.TotalCount);

        Assert.Equal(3, roundTripped.SessionCount);

    }

    [Fact]
    public void ApiResponse_SagaStats_RoundTrips()
    {

        SagaStats stats = new(4, 2, null, DateTimeOffset.UtcNow);

        ApiResponse<SagaStats> original = new(stats, true, null, "trace-saga-stats");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseSagaStats);

        ApiResponse<SagaStats>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseSagaStats);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.Equal(4, roundTripped.Data!.TotalCount);

    }

    [Fact]
    public void FileReadResult_RoundTrips()
    {

        FileReadResult original = new("notes.md", "# Notes", "utf-8", 128, DateTimeOffset.UtcNow);

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.FileReadResult);

        FileReadResult? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.FileReadResult);

        Assert.NotNull(roundTripped);

        Assert.Equal("notes.md", roundTripped.RelativePath);

        Assert.Equal("# Notes", roundTripped.Content);

    }

    [Fact]
    public void ApiResponse_FileReadResult_RoundTrips()
    {

        FileReadResult read = new("readme.md", "hello", "utf-8", 5, DateTimeOffset.UtcNow);

        ApiResponse<FileReadResult> original = new(read, true, null, "trace-read");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseFileReadResult);

        ApiResponse<FileReadResult>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseFileReadResult);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.Equal("hello", roundTripped.Data!.Content);

    }

    [Fact]
    public void FileWriteResult_RoundTrips()
    {

        FileWriteResult original = new("draft.md", 256, DateTimeOffset.UtcNow);

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.FileWriteResult);

        FileWriteResult? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.FileWriteResult);

        Assert.NotNull(roundTripped);

        Assert.Equal("draft.md", roundTripped.RelativePath);

        Assert.Equal(256, roundTripped.BytesWritten);

    }

    [Fact]
    public void ApiResponse_FileWriteResult_RoundTrips()
    {

        FileWriteResult write = new("save.md", 64, DateTimeOffset.UtcNow);

        ApiResponse<FileWriteResult> original = new(write, true, null, "trace-write");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseFileWriteResult);

        ApiResponse<FileWriteResult>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseFileWriteResult);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.Equal(64, roundTripped.Data!.BytesWritten);

    }

    [Fact]
    public void CodexContentDto_RoundTrips()
    {

        CodexContentDto original = new("CODEX.md", "# Codex", true);

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.CodexContentDto);

        CodexContentDto? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.CodexContentDto);

        Assert.NotNull(roundTripped);

        Assert.Equal("CODEX.md", roundTripped.Path);

        Assert.True(roundTripped.Exists);

    }

    [Fact]
    public void ApiResponse_CodexContentDto_RoundTrips()
    {

        CodexContentDto codex = new("campaigns/foo/CODEX.md", "# Campaign", true);

        ApiResponse<CodexContentDto> original = new(codex, true, null, "trace-codex");

        string json = JsonSerializer.Serialize(original, TheForgeJsonContext.Default.ApiResponseCodexContentDto);

        ApiResponse<CodexContentDto>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ApiResponseCodexContentDto);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.Equal("# Campaign", roundTripped.Data!.Content);

    }

    [Fact]
    public void InquisitorArray_RoundTrips_EachKindViaTheForgeJsonContext()
    {

        List<Inquisitor> inquisitors =
        [
            new RegexInquisitor("hello", ShouldMatch: true, IgnoreCase: true) { Label = "greeting" },
            new JsonSchemaInquisitor(JsonDocument.Parse("""{"type":"object","required":["name"]}""").RootElement),
            new SemanticInquisitor("Is the output polite?", ExpectedAnswer: false) { Label = "polite" },
        ];

        string json = JsonSerializer.Serialize(inquisitors, TheForgeJsonContext.Default.ListInquisitor);

        Assert.Contains("\"kind\":\"regex\"", json, StringComparison.Ordinal);

        Assert.Contains("\"kind\":\"jsonSchema\"", json, StringComparison.Ordinal);

        Assert.Contains("\"kind\":\"semantic\"", json, StringComparison.Ordinal);

        List<Inquisitor>? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.ListInquisitor);

        Assert.NotNull(roundTripped);

        Assert.Equal(3, roundTripped.Count);

        RegexInquisitor regex = Assert.IsType<RegexInquisitor>(roundTripped[0]);

        Assert.Equal("hello", regex.Pattern);

        Assert.True(regex.IgnoreCase);

        Assert.Equal("greeting", regex.Label);

        Assert.IsType<JsonSchemaInquisitor>(roundTripped[1]);

        SemanticInquisitor semantic = Assert.IsType<SemanticInquisitor>(roundTripped[2]);

        Assert.Equal("Is the output polite?", semantic.Question);

        Assert.False(semantic.ExpectedAnswer);

        Assert.Equal("polite", semantic.Label);

    }

    [Fact]
    public void Trial_RoundTrips_WithPolymorphicInquisitorsViaTheForgeJsonContext()
    {

        Trial trial = new(
            TrialTargetKind.Spell,
            "greater-heal",
            [
                new RegexInquisitor(@"healed", ShouldMatch: true),
                new SemanticInquisitor("Did healing occur?", ExpectedAnswer: true),
            ],
            Variables: new Dictionary<string, string> { ["target"] = "ally" },
            Model: "fast",
            Workspace: "/ws/a",
            Name: "heal-check");

        string json = JsonSerializer.Serialize(trial, TheForgeJsonContext.Default.Trial);

        Trial? roundTripped = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.Trial);

        Assert.NotNull(roundTripped);

        Assert.Equal(TrialTargetKind.Spell, roundTripped.TargetKind);

        Assert.Equal("greater-heal", roundTripped.Target);

        Assert.Equal(2, roundTripped.Inquisitors.Count);

        Assert.IsType<RegexInquisitor>(roundTripped.Inquisitors[0]);

        Assert.IsType<SemanticInquisitor>(roundTripped.Inquisitors[1]);

        Assert.Equal("ally", roundTripped.Variables!["target"]);

    }

    [Fact]
    public void ApiResponse_TrialResult_RoundTripsViaTheForgeJsonContext()
    {

        TrialResult result = new(
            TrialName: "heal-check",
            TargetKind: TrialTargetKind.Prompt,
            Target: Guid.NewGuid().ToString("D"),
            Passed: true,
            Output: "ok",
            Verdicts:
            [
                new InquisitorVerdict("regex", "match", true, "matched"),
                new InquisitorVerdict("semantic", null, true, "yes"),
            ],
            InquisitorsPassed: 2,
            InquisitorsTotal: 2,
            Usage: new ChatCompletionUsage(10, 5, 15));

        ApiResponse<TrialResult> envelope = new(result, true, null, "trace-pg");

        string json = JsonSerializer.Serialize(envelope, TheForgeJsonContext.Default.ApiResponseTrialResult);

        ApiResponse<TrialResult>? roundTripped = JsonSerializer.Deserialize(
            json,
            TheForgeJsonContext.Default.ApiResponseTrialResult);

        Assert.NotNull(roundTripped);

        Assert.True(roundTripped.IsSuccess);

        Assert.NotNull(roundTripped.Data);

        Assert.True(roundTripped.Data.Passed);

        Assert.Equal(2, roundTripped.Data.Verdicts.Count);

        Assert.Equal(15, roundTripped.Data.Usage!.TotalTokens);

    }

}
