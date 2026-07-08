using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ForgeJsonContextTests
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

        string json = JsonSerializer.Serialize(original, ForgeJsonContext.Default.ApiResponseSpellDetail);

        ApiResponse<SpellDetail>? roundTripped = JsonSerializer.Deserialize(json, ForgeJsonContext.Default.ApiResponseSpellDetail);

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

        string json = JsonSerializer.Serialize(failure, ForgeJsonContext.Default.ApiResponseBoolean);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.TryGetProperty("data", out _));

        Assert.Equal("Connection.Failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());

        ApiResponse<bool>? roundTripped = JsonSerializer.Deserialize(json, ForgeJsonContext.Default.ApiResponseBoolean);

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

        IntelligenceEvent? frame = JsonSerializer.Deserialize(ndjsonLine, ForgeJsonContext.Default.IntelligenceEvent);

        Assert.NotNull(frame);

        Assert.Equal(expectedType, frame.Type);

        Assert.Equal(expectedData, frame.Data);

    }

    [Fact]
    public void IntelligenceEvent_ResultFrame_CarriesUsage()
    {

        const string ndjsonLine =
            """{"type":"result","message":"done","usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""";

        IntelligenceEvent? frame = JsonSerializer.Deserialize(ndjsonLine, ForgeJsonContext.Default.IntelligenceEvent);

        Assert.NotNull(frame);

        Assert.Equal(IntelligenceEventType.Result, frame.Type);

        Assert.NotNull(frame.Usage);

        Assert.Equal(10, frame.Usage.PromptTokens);

        Assert.Equal(15, frame.Usage.TotalTokens);

    }

}
