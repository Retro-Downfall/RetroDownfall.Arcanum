using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Tests.Telemetry;

[Collection("ProcessEnvironment")]
public sealed class TelemetryServiceWebResearchTests
{
    [Fact]
    public void Aggregates_web_research_outcomes_usage_cost_and_latency()
    {
        using TelemetryService telemetry = new();

        TagList successTags = new()
        {
            { "provider", "test-provider" },
            { "operation", "search" },
            { "outcome", "success" },
        };
        TagList failureTags = new()
        {
            { "provider", "test-provider" },
            { "operation", "read_url" },
            { "outcome", "timeout" },
        };
        TagList promptTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
            { "kind", "prompt" },
        };
        TagList completionTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
            { "kind", "completion" },
        };
        TagList reasoningTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
            { "kind", "reasoning" },
        };
        TagList totalTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
            { "kind", "total" },
        };
        TagList citationTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
            { "kind", "citation" },
        };
        TagList providerModelTags = new()
        {
            { "provider", "test-provider" },
            { "model", "test-model" },
        };

        ArcanumMetrics.WebResearchRequestsTotal.Add(1, successTags);
        ArcanumMetrics.WebResearchRequestsTotal.Add(1, failureTags);
        ArcanumMetrics.WebResearchDuration.Record(0.25, successTags);
        ArcanumMetrics.WebResearchDuration.Record(0.75, failureTags);
        ArcanumMetrics.WebResearchTokensTotal.Add(11, promptTags);
        ArcanumMetrics.WebResearchTokensTotal.Add(7, completionTags);
        ArcanumMetrics.WebResearchTokensTotal.Add(18, totalTags);
        ArcanumMetrics.WebResearchTokensTotal.Add(3, reasoningTags);
        ArcanumMetrics.WebResearchTokensTotal.Add(2, citationTags);
        ArcanumMetrics.WebResearchSearchQueriesTotal.Add(4, providerModelTags);
        ArcanumMetrics.WebResearchCostUsdTotal.Add(0.0125, providerModelTags);

        WebResearchTelemetrySnapshot snapshot = telemetry.GetSnapshot().WebResearch;

        Assert.Equal(2, snapshot.Requests);
        Assert.Equal(1, snapshot.SuccessfulRequests);
        Assert.Equal(1, snapshot.FailedRequests);
        Assert.Equal(11, snapshot.PromptTokens);
        Assert.Equal(7, snapshot.CompletionTokens);
        Assert.Equal(18, snapshot.TotalTokens);
        Assert.Equal(3, snapshot.ReasoningTokens);
        Assert.Equal(2, snapshot.CitationTokens);
        Assert.Equal(4, snapshot.SearchQueries);
        Assert.Equal(0.0125m, snapshot.CostUsd);
        Assert.Equal(TimeSpan.FromSeconds(1), snapshot.CumulativeLatency);
    }

    [Fact]
    public void Start_is_idempotent_and_dispose_prevents_restart()
    {
        TelemetryService telemetry = new();

        telemetry.Start();
        telemetry.Start();
        telemetry.Dispose();

        Assert.Throws<ObjectDisposedException>(telemetry.Start);
    }
}
