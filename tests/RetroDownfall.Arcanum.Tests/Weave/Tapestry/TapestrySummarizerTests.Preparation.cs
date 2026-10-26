using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestrySummarizerTests
{
    [Fact]
    public async Task Preparation_BindsActualOrderedPromptProviderModelAndOutputRecipe()
    {
        var settings = Settings(summaryModel: "summary");

        var provider = new FakeIntelligenceProvider();

        var summarizer = CreateSummarizer(settings, provider);

        var request = Request("alpha ``` fence", "beta");

        var prepared = Assert.IsType<TapestrySummaryPreparation>(summarizer.Prepare(request));

        Assert.Equal("local", prepared.Provider);

        Assert.Equal("summary", prepared.Model);

        Assert.NotEqual(prepared.InputFingerprint, summarizer.Prepare(Request("beta", "alpha ``` fence"))!.InputFingerprint);

        Assert.NotEqual(prepared.InputFingerprint, summarizer.Prepare(request with { Layer = 2 })!.InputFingerprint);

        Assert.NotEqual(prepared.InputFingerprint, summarizer.Prepare(request with { ScopeLabel = "other" })!.InputFingerprint);

        Assert.True((await summarizer.SummarizeAsync(request, CancellationToken.None)).IsSuccess);

        var ping = Assert.Single(provider.Requests);

        Assert.Equal(TapestryHash.OfParts(ping.StatelessMessages!.Select(message => message.Content)), prepared.InputFingerprint);

        Assert.Equal(TapestryHash.OfParts(["raptor-summary-prompt-v1", ping.StatelessMessages![0].Content,
            ping.MaxOutputTokens!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]), prepared.RecipeFingerprint);

        settings.Providers[0].Name = "other-provider";

        Assert.NotEqual(prepared.Provider, summarizer.Prepare(request)!.Provider);
    }

    [Theory]
    [InlineData(TapestryScopeKind.Workspace, "/Users/private/person/repository/", "workspace repository")]
    [InlineData(TapestryScopeKind.Workspace, "C:\\Users\\private\\repository\\", "workspace repository")]
    [InlineData(TapestryScopeKind.Workspace, "/", "workspace")]
    [InlineData(TapestryScopeKind.Workspace, "C:\\", "workspace")]
    [InlineData(TapestryScopeKind.Workspace, "", "workspace")]
    [InlineData(TapestryScopeKind.Workspace, "repository", "workspace repository")]
    [InlineData(TapestryScopeKind.Session, "session-label", "session history")]
    [InlineData(TapestryScopeKind.SessionAttachment, "attachment-label", "session attachments")]
    public async Task Preparation_ProviderReceivesOnlyTheModelSafeScopeLabel(TapestryScopeKind kind, string scope, string expected)
    {
        var provider = new FakeIntelligenceProvider();

        var summarizer = CreateSummarizer(Settings(summaryModel: "summary"), provider);

        var request = new TapestrySummaryRequest(kind, scope, 1, ["source alpha", "source beta"]);

        var receipt = summarizer.Prepare(request)!;

        Assert.True((await summarizer.SummarizeAsync(request, default)).IsSuccess);

        var ping = Assert.Single(provider.Requests);

        string input = ping.StatelessMessages![1].Content;

        Assert.Contains($"Scope: {expected}" + System.Environment.NewLine, input, StringComparison.Ordinal);

        Assert.DoesNotContain("private", input, StringComparison.Ordinal);

        Assert.DoesNotContain("session-label", input, StringComparison.Ordinal);

        Assert.DoesNotContain("attachment-label", input, StringComparison.Ordinal);

        Assert.Equal(TapestryHash.OfParts(ping.StatelessMessages.Select(message => message.Content)), receipt.InputFingerprint);
    }
}
