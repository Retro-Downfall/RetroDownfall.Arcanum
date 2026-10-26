using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WeaveServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TapestryEmbedding_UsesFullInput_WithoutChangingOrdinaryChunking(bool tapestry)
    {
        var settings = EnabledSettings();

        int ordinaryLimit = settings.ResolveEmbeddings().ChunkSizeChars;

        string text = new('x', ordinaryLimit + 1000);

        FakeEmbeddingGeneratorFactory factory = new() { Provenance = new("local", "nomic-embed-text") };

        using IDisposable? observation = tapestry ? TapestryBuildContext.Enter(new(
            new(new(TapestryScopeKind.Workspace, "/repo"), 1, 0, new(1, "source", false)),
            "local", "summary", "local", "nomic-embed-text")) : null;

        var result = await CreateService(settings, factory, tapestryValidator: new TapestrySendValidator(false)).EmbedAsync(text, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(tapestry ? text : text[..ordinaryLimit], Assert.Single(factory.Generator.ReceivedTexts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TapestryEmbedding_RefusesDirectGeneratorWithoutCurrentSourceProof(bool missingValidator)
    {
        FakeEmbeddingGeneratorFactory factory = new() { Provenance = new("local", "nomic-embed-text") };

        using IDisposable observation = TapestryBuildContext.Enter(new(
            new(new(TapestryScopeKind.Workspace, "/repo"), 1, 0, new(1, "source", false)),
            "local", "summary", "local", "nomic-embed-text"));

        var result = await CreateService(EnabledSettings(), factory,
            tapestryValidator: missingValidator ? null : new TapestrySendValidator(true)).EmbedAsync("full source", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Empty(factory.Generator.ReceivedTexts);
    }

    private sealed class TapestrySendValidator(bool refuse) : ITapestryProviderValidator
    {
        public Task<Result> ValidateAsync(string provider, string model, bool embedding, CancellationToken cancellationToken)
        {
            Assert.Equal("local", provider);

            Assert.Equal("nomic-embed-text", model);

            Assert.True(embedding);

            return Task.FromResult(refuse ? Result.Failure(new Error(ErrorCodes.Hub.InvalidAdmission, "Source changed.")) : Result.Success());
        }
    }
}
