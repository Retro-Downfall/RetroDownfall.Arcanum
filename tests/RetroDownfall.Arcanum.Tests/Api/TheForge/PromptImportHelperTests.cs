using System.Text.Json;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

public sealed class PromptImportHelperTests
{

    [Fact]
    public async Task ImportAsync_NewPrompt_PersistsCompleteMappedPrompt()
    {

        FakePromptRepository repository = new();
        Guid targetCampaignId = Guid.NewGuid();
        Guid payloadCampaignId = Guid.NewGuid();
        using JsonDocument parameterSchema = JsonDocument.Parse(
            """{"type":"object","properties":{"topic":{"type":"string"}}}""");
        using JsonDocument defaults = JsonDocument.Parse("""{"topic":"history"}""");
        PromptExportDto payload = new(
            Name: "Summarize",
            Version: "1.2.3",
            Description: "Summarizes a topic",
            Tags: ["utility", "writing"],
            Template: "Summarize {{topic}}",
            ParameterSchema: parameterSchema,
            DefaultParameters: defaults,
            Model: "model-a",
            Provider: "provider-a",
            Temperature: 0.25,
            TopP: 0.9,
            MaxOutputTokens: 512,
            CampaignId: payloadCampaignId);
        using CancellationTokenSource cancellation = new();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        Result<PromptSummaryDto> result = await PromptImportHelper.ImportAsync(
            repository,
            new PromptImportRequest(payload, targetCampaignId),
            cancellation.Token);

        DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
        Assert.True(result.IsSuccess);
        Assert.Equal(Error.None, result.Error);
        Assert.Equal("Summarize", repository.LookupName);
        Assert.Equal("1.2.3", repository.LookupVersion);
        Assert.Equal(targetCampaignId, repository.LookupCampaignId);
        Assert.Equal(cancellation.Token, repository.LookupCancellationToken);

        Prompt added = Assert.IsType<Prompt>(repository.Added);
        Assert.NotEqual(Guid.Empty, added.Id);
        Assert.Equal(targetCampaignId, added.CampaignId);
        Assert.Equal("Summarize", added.Name);
        Assert.Equal("1.2.3", added.Version);
        Assert.Equal("Summarizes a topic", added.Description);
        Assert.Equal("""["utility","writing"]""", added.Tags);
        Assert.Equal("Summarize {{topic}}", added.Template);
        Assert.Equal(parameterSchema.RootElement.GetRawText(), added.ParameterSchema);
        Assert.Equal(defaults.RootElement.GetRawText(), added.DefaultParameters);
        Assert.Equal("model-a", added.Model);
        Assert.Equal("provider-a", added.Provider);
        Assert.Equal(0.25, added.Temperature);
        Assert.Equal(0.9, added.TopP);
        Assert.Equal(512, added.MaxOutputTokens);
        Assert.InRange(added.CreatedAt, startedAt, finishedAt);
        Assert.Equal(added.CreatedAt, added.UpdatedAt);
        Assert.Equal(cancellation.Token, repository.AddCancellationToken);

        PromptSummaryDto summary = result.Value;
        Assert.Equal(added.Id, summary.Id);
        Assert.Equal(targetCampaignId, summary.CampaignId);
        Assert.Equal("Summarize", summary.Name);
        Assert.Equal("1.2.3", summary.Version);
        Assert.Equal("Summarizes a topic", summary.Description);
        Assert.Equal(["utility", "writing"], summary.Tags);
        Assert.Equal(added.UpdatedAt, summary.UpdatedAt);

    }

    [Fact]
    public async Task ImportAsync_DuplicateVersion_ReturnsFailureWithoutPersisting()
    {

        FakePromptRepository repository = new()
        {
            Existing = new Prompt
            {
                Id = Guid.NewGuid(),
                Name = "Existing",
                Version = "2.0.0",
            },
        };
        PromptExportDto payload = new(
            Name: "Existing",
            Version: "2.0.0",
            Description: null,
            Tags: [],
            Template: "existing",
            ParameterSchema: null,
            DefaultParameters: null,
            Model: null,
            Provider: null,
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            CampaignId: null);
        using CancellationTokenSource cancellation = new();

        Result<PromptSummaryDto> result = await PromptImportHelper.ImportAsync(
            repository,
            new PromptImportRequest(payload, CampaignId: null),
            cancellation.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Prompt.DuplicateVersion, result.Error.Code);
        Assert.Equal(
            "A prompt with this name and version already exists in the target scope.",
            result.Error.Message);
        Assert.Null(repository.Added);
        Assert.Equal(cancellation.Token, repository.LookupCancellationToken);

    }

    private sealed class FakePromptRepository : IPromptRepository
    {

        public Prompt? Existing { get; init; }

        public Prompt? Added { get; private set; }

        public string? LookupName { get; private set; }

        public string? LookupVersion { get; private set; }

        public Guid? LookupCampaignId { get; private set; }

        public CancellationToken LookupCancellationToken { get; private set; }

        public CancellationToken AddCancellationToken { get; private set; }

        public Task<Prompt?> GetByNameAndVersionAsync(
            string name,
            string version,
            Guid? campaignId,
            CancellationToken cancellationToken = default)
        {

            LookupName = name;
            LookupVersion = version;
            LookupCampaignId = campaignId;
            LookupCancellationToken = cancellationToken;
            return Task.FromResult(Existing);

        }

        public Task<Prompt> AddAsync(Prompt prompt, CancellationToken cancellationToken = default)
        {

            Added = prompt;
            AddCancellationToken = cancellationToken;
            return Task.FromResult(prompt);

        }

        public Task<Prompt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Prompt>> ListVersionsAsync(
            string name,
            Guid? campaignId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ListPageResult<Prompt>> ListAsync(
            Guid? campaignId,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Prompt> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

}
