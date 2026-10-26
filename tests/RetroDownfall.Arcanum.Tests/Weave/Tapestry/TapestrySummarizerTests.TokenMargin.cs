using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestrySummarizerTests
{
    [Fact]
    public async Task RealEstimator_MarginedPromptFitsExactlyWithOutputAndReasoningReserve()
    {
        var settings = Settings(summaryModel: "summary", contextWindowLimit: 100000);

        FakeIntelligenceProvider provider = new();

        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

        TapestrySummarizer summarizer = new(provider, estimator, new TestOptionsMonitor<Core.Configuration.ArcanumSettings>(settings), NullLogger<TapestrySummarizer>.Instance);

        var request = Request("Unicode café 👩🏽‍💻 with hostile ``` delimiters", "A second complete source excerpt");

        Assert.True((await summarizer.SummarizeAsync(request, CancellationToken.None)).IsSuccess);

        var ping = Assert.Single(provider.Requests);

        TokenEstimate prompt = estimator.EstimateText(settings.Providers[0], "summary", string.Concat(ping.StatelessMessages!.Select(static message => message.Content)));

        Assert.True(prompt.SafetyMarginTokens > 0);

        Assert.Equal(2 * MaxSummaryTokens, ping.MaxOutputTokens);

        int required = checked(prompt.TokenCount + ping.MaxOutputTokens!.Value);

        settings.Providers[0].ContextWindowLimit = required;

        Assert.True(summarizer.FitsOneRequest(request));

        settings.Providers[0].ContextWindowLimit = required - 1;

        Assert.False(summarizer.FitsOneRequest(request));

        Assert.Single(provider.Requests);
    }
}
