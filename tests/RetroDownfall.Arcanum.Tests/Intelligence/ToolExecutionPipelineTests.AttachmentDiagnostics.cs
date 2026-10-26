using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class ToolExecutionPipelineTests
{
    [Theory]
    [InlineData("refresh_session_file")]
    [InlineData("attach_session_file")]
    [InlineData("apply_patch")]
    public async Task AttachmentDiagnostics_Unadvertised_attachment_tool_never_runs_post_processing(string name)
    {
        int lookups = 0;
        var store = new RetroDownfall.Arcanum.Tests.Fixtures.NoOpSessionAttachmentStore(lookupFailure: () =>
        {
            lookups++;
            return new InvalidOperationException("private-side-effect-canary");
        });
        var result = await RunWithSessionAsync(() => CreatePipeline(store, new UnusedAttachmentSourceResolver()).ProcessSingleToolCallAsync(
            new FunctionCallContent("unadvertised", name, new Dictionary<string, object?> { ["logicalKey"] = "notes.txt", ["logicalName"] = "notes.txt" }),
            new PingRequest("hello"), new ChatOptions { Tools = [] }, null, null, new(), true, default));
        Assert.Equal(0, lookups);
        Assert.Contains("No callable tools are advertised", result.ResultText, StringComparison.Ordinal);
        Assert.True(result.Failed);
        Assert.Null(result.AttachmentRefresh);
        Assert.Null(result.AdditionalContextContents);
    }

    [Theory]
    [InlineData("att_3", false)]
    [InlineData("att_3", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("42", false)]
    public async Task AttachmentDiagnostics_Bad_refresh_id_names_field_without_echo_or_key_fallback(string id, bool keyAlso)
    {
        Dictionary<string, object?> arguments = new() { ["attachmentId"] = id };
        if (keyAlso) arguments["logicalKey"] = "private-file-canary";
        var result = await RunWithSessionAsync(() => CreatePipeline(attachmentSourceResolver: new UnusedAttachmentSourceResolver()).ProcessSingleToolCallAsync(
            new FunctionCallContent("invalid", "refresh_session_file", arguments), new PingRequest("refresh"), SessionToolOptions("refresh_session_file"), null, null, new(), false, default));
        using JsonDocument wire = JsonDocument.Parse(result.ResultText);
        Assert.Equal("invalid_selector", wire.RootElement.GetProperty("errorCode").GetString());
        string message = wire.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("attachmentId", message, StringComparison.Ordinal);
        Assert.Contains("valid non-empty GUID", message, StringComparison.Ordinal);
        Assert.DoesNotContain("att_3", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-file-canary", message, StringComparison.Ordinal);
        Assert.Null(result.AdditionalContextContents);
        Assert.Null(result.AttachmentRefresh);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(100)]
    public async Task AttachmentDiagnostics_Unknown_tool_lists_only_bounded_advertised_callable_names(int count)
    {
        ChatOptions options = new() { Tools = Enumerable.Range(0, count).Select(index => (AITool)AIFunctionFactory.Create(() => "unused", $"available_{index:D3}")).ToList() };
        var result = await CreatePipeline().ProcessSingleToolCallAsync(new FunctionCallContent("unknown", "private-unadvertised-canary", new Dictionary<string, object?>()), new PingRequest("hello"), options, null, null, new(), true, default);
        Assert.Contains(count == 0 ? "No callable tools are advertised" : "available_000", result.ResultText, StringComparison.Ordinal);
        if (count > 0) Assert.Contains("available_001", result.ResultText, StringComparison.Ordinal);
        if (count > 32) Assert.Contains("more", result.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-unadvertised-canary", result.ResultText, StringComparison.Ordinal);
        Assert.True(result.ResultText.Length <= 4096);
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentDiagnostics_Truncated_tool_result_reports_actual_retained_characters_and_next_step(bool unicode)
    {
        string unit = unicode ? "😀z" : "x";
        string raw = string.Concat(Enumerable.Repeat(unit, 250_000));
        ChatOptions options = new() { Tools = [AIFunctionFactory.Create(() => raw, "read_many")] };
        var result = await CreatePipeline(materializer: new ToolResultMaterializer()).ProcessSingleToolCallAsync(new FunctionCallContent("large", "read_many", new Dictionary<string, object?>()), new PingRequest("hello"), options, null, null, new(), false, default);
        Assert.Contains("Narrow the request or use pagination", result.ResultText, StringComparison.Ordinal);
        Match match = Regex.Match(result.ResultText, @"showing (\d+) of (\d+) characters");
        Assert.True(match.Success, result.ResultText[^Math.Min(200, result.ResultText.Length)..]);
        Assert.Equal(raw.Length, int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        int markerAt = result.ResultText.IndexOf("\n[truncated", StringComparison.Ordinal);
        string retained = result.ResultText[..markerAt].Replace("\n…\n", "", StringComparison.Ordinal);
        Assert.Equal(retained.Length, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Encoding.UTF8.GetByteCount(result.ResultText) <= 8192);
        Assert.True(result.ResultText.Length <= 8192);
        Assert.Equal(result.ResultText, new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(result.ResultText)));
    }
}
