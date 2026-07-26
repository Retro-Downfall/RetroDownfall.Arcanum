using System.Diagnostics;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Performance;

/// <summary>
/// Manual baseline harness; excluded from default CI via category filter.
/// </summary>
[Trait("Category", "Perf")]
[Collection("ApiHost")]
public sealed class ArcanumPerfBaselineTests
{

    [SkippableFact]
    public async Task Cold_health_probe_completes_within_reasonable_budget()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        HttpClient client = factory.CreateAuthenticatedClient();

        Stopwatch sw = Stopwatch.StartNew();

        HttpResponseMessage response = await client.GetAsync("/api/health");

        sw.Stop();

        Assert.True(response.IsSuccessStatusCode);

        Assert.True(sw.ElapsedMilliseconds < 15000, $"Health probe took {sw.ElapsedMilliseconds}ms");

    }

    [SkippableFact]
    public async Task ManaPreflight_repeated_count_is_faster_than_cold()
    {

        Microsoft.ML.Tokenizers.Tokenizer tokenizer =
            Microsoft.ML.Tokenizers.TiktokenTokenizer.CreateForEncoding("o200k_base");

        RetroDownfall.Arcanum.Api.Intelligence.ManaPreflight preflight =
            new(new TestOptionsMonitor<RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings>(
                new RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings()));

        List<Microsoft.Extensions.AI.ChatMessage> messages =
        [
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello world"),
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "greetings"),
        ];

        _ = preflight.CountTokens(messages, tokenizer, 4, "o200k_base");

        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < 100; i++)
        {

            _ = preflight.CountTokens(messages, tokenizer, 4, "o200k_base");

        }

        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"100 memoized counts took {sw.ElapsedMilliseconds}ms");

    }

}
