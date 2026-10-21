using System.Text.Json;
using System.Text.Json.Nodes;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Memory;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class LocalOllamaRecallQualificationContractTests
{
    private static readonly Guid Session = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid Version = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string Query = "local.recall.subject";

    private const string Token = "OPAQUE_MEMORY_TOKEN";

    private const string Arguments = "{\"query\":\"local.recall.subject\",\"scope\":\"Covenant\",\"limit\":1}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_recall_evidence_preserves_opaque_call_and_exact_result_and_consumes_version(bool includeNullCursor)
    {
        string arguments = includeNullCursor ? Arguments[..^1] + ",\"cursor\":null}" : Arguments;

        var frames = Frames(arguments);

        Assert.Equal(arguments, frames[0]["data"]!.GetValue<string>());

        using var reader = new StringReader(Text(frames));

        var evidence = await LocalOllamaRecallQualificationGuards.ReadAsync(reader, Session, Query, Token, Version, default);

        Assert.NotNull(evidence);

        Assert.Equal("opaque:recall/call-17", evidence.CallId);

        Assert.Equal(arguments, evidence.ArgumentsJson);

        Assert.Contains(Token, evidence.ResultJson);

        Assert.Equal($"RECALLED_TOKEN={Token}; VERSION={Version:D}", evidence.Answer);
    }

    [Theory]
    [InlineData("wrong-call-id")]
    [InlineData("extra-call")]
    [InlineData("missing-result")]
    [InlineData("wrong-version")]
    [InlineData("wrong-query")]
    [InlineData("prefixed-display-arguments")]
    [InlineData("nonnull-first-page-cursor")]
    [InlineData("extra-authority")]
    [InlineData("wrong-token")]
    [InlineData("replayed-terminal")]
    [InlineData("error")]
    [InlineData("wrong-session")]
    [InlineData("duplicate-field")]
    [InlineData("unterminated")]
    public async Task Native_recall_evidence_refuses_incomplete_forged_or_uncorrelated_streams(string defect)
    {
        var frames = Frames();

        switch (defect)
        {
            case "wrong-call-id": frames[1]["toolCall"]!["callId"] = "different"; break;
            case "extra-call": frames.Insert(1, frames[0].DeepClone().AsObject()); break;
            case "missing-result": frames.RemoveAt(1); break;
            case "wrong-version": frames[1]["data"] = frames[1]["data"]!.GetValue<string>().Replace(Version.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal); break;
            case "prefixed-display-arguments": frames[0]["data"] = "recall_memory: " + Arguments; break;
            case "nonnull-first-page-cursor":
                string continued = Arguments[..^1] + ",\"cursor\":\"opaque-continuation\"}";
                frames[0]["toolCall"]!["argumentsJson"] = continued;
                frames[1]["toolCall"]!["argumentsJson"] = continued;
                frames[0]["data"] = continued;
                break;
            case "wrong-query": frames[0]["toolCall"]!["argumentsJson"] = "{\"query\":\"other\",\"scope\":\"Covenant\",\"limit\":1}"; break;
            case "extra-authority": frames[0]["toolCall"]!["argumentsJson"] = "{\"query\":\"local.recall.subject\",\"scope\":\"Covenant\",\"limit\":1,\"campaignId\":\"forged\"}"; break;
            case "wrong-token": frames[2]["message"] = "guessed"; frames[2]["data"] = "guessed"; frames[3]["message"] = "guessed"; break;
            case "replayed-terminal": frames.Add(frames[3].DeepClone().AsObject()); break;
            case "error": frames.Insert(2, Frame(new(IntelligenceEventType.Error, "refused"))); break;
            case "wrong-session": frames.Insert(0, Frame(new(IntelligenceEventType.SessionBound, "bound", Guid.NewGuid().ToString("D")))); break;
        }

        if (defect is "wrong-query" or "extra-authority") frames[0]["data"] = frames[0]["toolCall"]!["argumentsJson"]!.GetValue<string>();

        string text = Text(frames);

        if (defect == "duplicate-field") text = text.Replace("\"callId\":", "\"callId\":\"forged\",\"callId\":", StringComparison.Ordinal);

        if (defect == "unterminated") text = text.TrimEnd('\n');

        using var reader = new StringReader(text);

        Assert.Null(await LocalOllamaRecallQualificationGuards.ReadAsync(reader, Session, Query, Token, Version, default));
    }

    [Theory]
    [InlineData("display", "ToolCall.DisplayMismatch", 1)]
    [InlineData("malformed", "Envelope.MalformedJson", 1)]
    [InlineData("missing-result", "Terminal.MissingToolResult", 2)]
    [InlineData("missing-terminal", "Terminal.Missing", 3)]
    [InlineData("answer", "Terminal.AnswerMismatch", 4)]
    public async Task Recall_refusals_identify_boundary_without_protected_content(string defect, string code, int ordinal)
    {
        const string sentinel = "PROTECTED_SENTINEL_MUST_NOT_ENTER_DIAGNOSTICS";

        var frames = Frames();

        string text;

        switch (defect)
        {
            case "display": frames[0]["data"] = sentinel; break;
            case "missing-result": frames.RemoveRange(1, 2); break;
            case "missing-terminal": frames.RemoveAt(3); break;
            case "answer":
                frames[2]["message"] = sentinel;
                frames[2]["data"] = sentinel;
                frames[3]["message"] = sentinel;
                break;
        }

        text = defect == "malformed" ? "{\"type\":\"" + sentinel + "\n" : Text(frames);

        List<LocalOllamaRecallQualificationGuards.Refusal> refusals = [];

        using var reader = new StringReader(text);

        Assert.Null(await LocalOllamaRecallQualificationGuards.ReadAsync(reader, Session, Query, Token, Version,
            default, refusals.Add));

        var refusal = Assert.Single(refusals);

        Assert.Equal(code, refusal.Code);

        Assert.Equal(ordinal, refusal.FrameOrdinal);

        Assert.DoesNotContain(sentinel, refusal.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Isolated_qualification_enables_native_recall_and_tools_without_extra_providers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanum-recall-config-{Guid.NewGuid():N}.json");

        try
        {
            await PublishedArcanumOllamaQualificationTests.WriteConfigurationAsync(path, Path.GetTempPath(), 12345,
                new Uri("http://127.0.0.1:11434/v1/"), "installed-model");

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            var settings = document.RootElement.GetProperty("Arcanum");

            Assert.True(settings.GetProperty("features").GetProperty("agentMemoryRecall").GetBoolean());

            Assert.True(settings.GetProperty("features").GetProperty("covenant").GetBoolean());

            var provider = Assert.Single(settings.GetProperty("providers").EnumerateArray());

            Assert.True(provider.GetProperty("models")[0].GetProperty("supportsTools").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Recall_persistence_retains_history_and_exact_observed_call_and_result_bytes()
    {
        var evidence = Evidence();

        JsonElement[] before = [Row("user", "old question"), Row("assistant", "old answer")];

        JsonElement[] after = [.. before, Row("user", "recall prompt"), Row("assistant", evidence.Answer),
            Row("assistant", $"[ToolCall: recall_memory({evidence.ArgumentsJson})]", "recall_memory"),
            Row("system", $"[ToolResult: {evidence.ResultJson}]")];

        Assert.True(LocalOllamaRecallQualificationGuards.MatchesPersistedTurn(before, after, Session, "recall prompt", evidence));
    }

    [Theory]
    [InlineData("changed-history")]
    [InlineData("changed-arguments")]
    [InlineData("changed-result")]
    [InlineData("missing-result")]
    [InlineData("extra-write")]
    [InlineData("duplicate-id")]
    public void Recall_persistence_refuses_other_writes_or_reconstructed_evidence(string defect)
    {
        var evidence = Evidence();

        JsonElement[] before = [Row("user", "old question"), Row("assistant", "old answer")];

        List<JsonElement> after = [.. before, Row("user", "recall prompt"), Row("assistant", evidence.Answer),
            Row("assistant", $"[ToolCall: recall_memory({evidence.ArgumentsJson})]", "recall_memory"),
            Row("system", $"[ToolResult: {evidence.ResultJson}]")];

        switch (defect)
        {
            case "changed-history": after[0] = Row("user", "rewritten history"); break;
            case "changed-arguments": after[4] = Row("assistant", "[ToolCall: recall_memory({})]", "recall_memory"); break;
            case "changed-result": after[5] = Row("system", "[ToolResult: guessed]"); break;
            case "missing-result": after.RemoveAt(5); break;
            case "extra-write": after.Add(Row("system", "extra write")); break;
            case "duplicate-id":
                var duplicate = JsonNode.Parse(after[5].GetRawText())!.AsObject();
                duplicate["id"] = after[4].GetProperty("id").GetString();
                using (var parsed = JsonDocument.Parse(duplicate.ToJsonString())) after[5] = parsed.RootElement.Clone();
                break;
        }

        Assert.False(LocalOllamaRecallQualificationGuards.MatchesPersistedTurn(before, after, Session, "recall prompt", evidence));
    }

    private static LocalOllamaRecallQualificationGuards.Evidence Evidence()
    {
        var frames = Frames();

        return new("opaque:recall/call-17", Arguments, frames[1]["data"]!.GetValue<string>(),
            $"RECALLED_TOKEN={Token}; VERSION={Version:D}");
    }

    private static JsonElement Row(string role, string content, string? toolName = null)
    {
        var value = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("D"), ["sessionId"] = Session.ToString("D"),
            ["role"] = role, ["content"] = content, ["toolName"] = toolName,
        };

        using var document = JsonDocument.Parse(value.ToJsonString());

        return document.RootElement.Clone();
    }

    private static List<JsonObject> Frames(string arguments = Arguments)
    {
        IntelligenceToolCallEvent call = new("opaque:recall/call-17", "recall_memory", arguments);

        var page = new MemoryRecallPage([new(MemoryRecallScope.Covenant, Version.ToString("D"), Token, VersionId: Version)], null);

        string result = JsonSerializer.Serialize(page, ArcanumJsonContext.Default.MemoryRecallPage);

        string answer = $"RECALLED_TOKEN={Token}; VERSION={Version:D}";

        IntelligenceEvent proposed = Assert.Single(IntelligenceEventProjection.Map(new ToolCallProposed(
            new TurnEventCorrelation(Guid.NewGuid(), 1, DateTimeOffset.UtcNow), call.CallId, call.Name, arguments, ToolCallDisposition.ServerExecution)));

        return [
            Frame(proposed),
            Frame(new(IntelligenceEventType.ToolResult, "recall_memory", result, ToolCall: call)),
            Frame(new(IntelligenceEventType.Token, answer, answer)),
            Frame(new(IntelligenceEventType.Result, answer, "42", FinishReason: "stop")),
        ];
    }

    private static JsonObject Frame(IntelligenceEvent frame) =>
        JsonNode.Parse(JsonSerializer.Serialize(frame, ArcanumJsonContext.Default.IntelligenceEvent))!.AsObject();

    private static string Text(IEnumerable<JsonObject> frames) => string.Join('\n', frames.Select(static frame => frame.ToJsonString())) + "\n";
}
