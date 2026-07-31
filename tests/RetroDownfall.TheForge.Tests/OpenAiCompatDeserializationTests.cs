using System.Text;
using System.Text.Json;
using RetroDownfall.TheForge.Core.Models.OpenAi;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class OpenAiCompatDeserializationTests
{

    [Fact]
    public void OpenAiFileObject_DeserializesSnakeCaseWire()
    {

        const string json = """
            {"id":"file-abc","bytes":12,"created_at":1700000000,"filename":"input.jsonl","purpose":"batch","object":"file"}
            """;

        OpenAiFileObject? file = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.OpenAiFileObject);

        Assert.NotNull(file);

        Assert.Equal("file-abc", file.Id);

        Assert.Equal(12, file.Bytes);

        Assert.Equal(1700000000, file.CreatedAt);

        Assert.Equal("input.jsonl", file.Filename);

        Assert.Equal("batch", file.Purpose);

        Assert.Equal("file", file.ObjectKind);

    }

    [Fact]
    public void OpenAiBatchObject_DeserializesRequestCounts()
    {

        const string json = """
            {"id":"batch_abc","endpoint":"/v1/chat/completions","input_file_id":"file-in","completion_window":"24h","status":"completed","created_at":1,"request_counts":{"total":3,"completed":2,"failed":1},"output_file_id":"file-out","error_file_id":null,"completed_at":2,"object":"batch"}
            """;

        OpenAiBatchObject? batch = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.OpenAiBatchObject);

        Assert.NotNull(batch);

        Assert.Equal("batch_abc", batch.Id);

        Assert.Equal(3, batch.RequestCounts.Total);

        Assert.Equal(2, batch.RequestCounts.Completed);

        Assert.Equal(1, batch.RequestCounts.Failed);

        Assert.Equal("file-out", batch.OutputFileId);

    }

    [Fact]
    public void OpenAiErrorResponse_DeserializesMessageAndCode()
    {

        const string json = """
            {"error":{"message":"No such file.","type":"invalid_request_error","param":"file_id","code":"not_found"}}
            """;

        OpenAiErrorResponse? error = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(error);

        Assert.Equal("No such file.", error.Error.Message);

        Assert.Equal("invalid_request_error", error.Error.Type);

        Assert.Equal("file_id", error.Error.Param);

        Assert.Equal("not_found", error.Error.Code);

    }

    [Fact]
    public void OpenAiBatchRequest_SerializesSnakeCase()
    {

        OpenAiBatchRequest request = new("file-in", "/v1/chat/completions", "24h");

        string json = JsonSerializer.Serialize(request, TheForgeJsonContext.Default.OpenAiBatchRequest);

        Assert.Contains("\"input_file_id\"", json, StringComparison.Ordinal);

        Assert.Contains("\"completion_window\"", json, StringComparison.Ordinal);

        Assert.DoesNotContain("InputFileId", json, StringComparison.Ordinal);

    }

}

public class JsonlBoundedPreviewTests
{

    [Fact]
    public async Task ReadAsync_RespectsMaxLines()
    {

        string payload = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"{{\"line\":{i}}}"));

        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(payload));

        JsonlPreviewResult result = await JsonlBoundedPreview.ReadAsync(stream, maxLines: 5, maxBytes: 1024 * 1024);

        Assert.Equal(5, result.Lines.Count);

        Assert.True(result.Truncated);

        Assert.Equal("{\"line\":1}", result.Lines[0]);

        Assert.Equal("{\"line\":5}", result.Lines[4]);

    }

    [Fact]
    public async Task ReadAsync_RespectsMaxBytesWithoutLoadingWholeFile()
    {

        StringBuilder huge = new();

        for (int i = 0; i < 1000; i++)
        {

            huge.AppendLine(new string('x', 200));

        }

        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(huge.ToString()));

        JsonlPreviewResult result = await JsonlBoundedPreview.ReadAsync(stream, maxLines: 500, maxBytes: 500);

        Assert.True(result.Lines.Count >= 1);

        Assert.True(result.Truncated);

        Assert.True(result.BytesRead <= 500 + 200);

    }

    [Fact]
    public async Task ReadAsync_HugeSingleLine_stops_reading_at_byte_limit()
    {

        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 1_000_000));

        await using MemoryStream stream = new(payload);

        JsonlPreviewResult result = await JsonlBoundedPreview.ReadAsync(
            stream,
            maxLines: 50,
            maxBytes: 500);

        Assert.True(result.Truncated);

        Assert.InRange(result.BytesRead, 0, 500);

        Assert.InRange(stream.Position, 0, 501);

        Assert.All(result.Lines, line => Assert.InRange(Encoding.UTF8.GetByteCount(line), 0, 500));

    }

    [Fact]
    public async Task ReadAsync_SmallFile_NotTruncated()
    {

        const string payload = "{\"a\":1}\n{\"b\":2}\n";

        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(payload));

        JsonlPreviewResult result = await JsonlBoundedPreview.ReadAsync(stream, maxLines: 50, maxBytes: 10_000);

        Assert.Equal(2, result.Lines.Count);

        Assert.False(result.Truncated);

    }

}
