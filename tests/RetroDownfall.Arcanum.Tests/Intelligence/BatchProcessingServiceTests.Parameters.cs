using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class BatchProcessingServiceTests
{
    [SkippableTheory]
    [InlineData("\"logprobs\":true", "logprobs")]
    [InlineData("\"top_logprobs\":0", "top_logprobs")]
    [InlineData("\"logprobs\":false,\"top_logprobs\":3", "top_logprobs")]
    public async Task ProcessBatchAsync_UnsupportedLogprobs_RecordsRefusalWithoutInference(string fields, string parameter)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeIntelligenceProvider intelligence = new();

        BatchProcessingService service = CreateService(intelligence);

        string line = """{"custom_id":"logprobs","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}],""" + fields + "}}";

        Guid inputFileId = await SeedInputFileAsync(line + "\n");

        BatchRecord batch = new(Guid.NewGuid(), inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        BatchRecord finished = Assert.IsType<BatchRecord>(await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.Completed, finished.Status);

        Assert.NotNull(finished.OutputFileId);

        Assert.Null(finished.ErrorFileId);

        Assert.Equal(0, intelligence.ExecutePromptCallCount);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId.Value);

        _createdFilePaths.Add(outputPath);

        using JsonDocument output = JsonDocument.Parse(await ReadArtifactTextAsync(outputPath));

        Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("response").ValueKind);

        JsonElement error = output.RootElement.GetProperty("error");

        Assert.Equal("unsupported_parameter", error.GetProperty("code").GetString());

        Assert.Contains(parameter, error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
