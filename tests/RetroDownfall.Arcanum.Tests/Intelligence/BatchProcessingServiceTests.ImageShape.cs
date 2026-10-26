using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class BatchProcessingServiceTests
{
    [SkippableTheory]
    [InlineData("""{"type":"image_url"}""")]
    [InlineData("""{"type":"image_url","image_url":null}""")]
    [InlineData("""{"type":"image_url","image_url":{}}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":null}}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":""}}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":" \t\r\n"}}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":"private-image-canary.png"}}""")]
    [InlineData("""{"type":"image_url","image_url":{"url":"data:image/png,private-image-canary"}}""")]
    public async Task ProcessBatchAsync_ImageShape_RecordsOriginalWireFieldWithoutInference(string imagePart)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeIntelligenceProvider intelligence = new();

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Scrying = true },
            Providers =
            [
                new ProviderSettings
                {
                    Name = "test",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://localhost",
                    Models = [new ModelEntry("m", SupportsVision: true)],
                },
            ],
        };

        BatchProcessingService service = CreateService(BuildServiceProvider(intelligence), settings);

        string line = $$$"""
            {"custom_id":"image-shape","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"system","content":"private-input-canary"},{"role":"user","content":[{"type":"text","text":"private-input-canary"},null,{{{imagePart}}}]}]}}
            """;

        Guid inputFileId = await SeedInputFileAsync(line + "\n");

        BatchRecord batch = new(Guid.NewGuid(), inputFileId, "/v1/chat/completions", BatchStatuses.Validating, DateTimeOffset.UtcNow, null, null, null);

        await _batches!.CreateAsync(batch, CancellationToken.None);

        await service.ProcessBatchAsync(batch, CancellationToken.None);

        BatchRecord finished = Assert.IsType<BatchRecord>(await _batches.GetByIdAsync(batch.Id, CancellationToken.None));

        Assert.Equal(BatchStatuses.Completed, finished.Status);

        Assert.NotNull(finished.OutputFileId);

        Assert.Null(finished.ErrorFileId);

        Assert.Equal(0, intelligence.ExecutePromptCallCount);

        Assert.Equal(0, finished.CompletedRequestCount);

        Assert.Equal(1, finished.FailedRequestCount);

        string outputPath = UploadedFileStorage.ResolvePath(finished.OutputFileId.Value);

        _createdFilePaths.Add(outputPath);

        string content = await ReadArtifactTextAsync(outputPath);

        using JsonDocument output = JsonDocument.Parse(content);

        Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("response").ValueKind);

        JsonElement error = output.RootElement.GetProperty("error");

        Assert.Equal("invalid_image_data", error.GetProperty("code").GetString());

        Assert.Contains("messages[1].content[2].image_url.url", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.DoesNotContain("private-input-canary", content, StringComparison.Ordinal);

        Assert.DoesNotContain("private-image-canary", content, StringComparison.Ordinal);
    }
}
