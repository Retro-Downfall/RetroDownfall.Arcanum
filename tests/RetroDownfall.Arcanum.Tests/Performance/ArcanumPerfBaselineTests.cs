using System.Diagnostics;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Performance;

/// <summary>
/// Manual baseline harness. The wall-clock assertions are machine-load sensitive, so the CI
/// coverage gate excludes this category through <c>--filter "Category!=Perf"</c> in
/// <c>scripts/coverage.sh</c>; <see cref="PerfCategoryExclusionTests"/> guards that filter.
/// A plain <c>dotnet test</c> with no filter still runs them.
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
}
