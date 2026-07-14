using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
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

}
