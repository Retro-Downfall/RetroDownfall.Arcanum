using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;

namespace RetroDownfall.Arcanum.Tests.Resilience;

public sealed class ProviderResolverCandidatesTests
{

    private static ProviderSettings MakeProvider(string name, params string[] models) => new()
    {

        Name = name,

        Type = AiProviderKind.Ollama,

        Endpoint = "http://localhost:11434",

        Models = models,

    };

    [Fact]
    public void ResolveCandidates_returns_single_when_health_null()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [
                MakeProvider("first", "llama3"),
                MakeProvider("second", "llama3"),
            ],

        };

        IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> candidates =
            ProviderResolver.ResolveCandidates(settings, "llama3", health: null);

        (ProviderSettings provider, string _) = Assert.Single(candidates);

        Assert.Equal("first", provider.Name);

    }

    [Fact]
    public void ResolveCandidates_returns_all_matching_providers()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [
                MakeProvider("first", "llama3"),
                MakeProvider("second", "llama3"),
                MakeProvider("third", "mistral"),
            ],

        };

        FakeProviderHealthTracker health = new();

        IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> candidates =
            ProviderResolver.ResolveCandidates(settings, "llama3", health);

        Assert.Equal(2, candidates.Count);

        Assert.Equal("first", candidates[0].Provider.Name);

        Assert.Equal("second", candidates[1].Provider.Name);

    }

    [Fact]
    public void ResolveCandidates_skips_unhealthy()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [
                MakeProvider("first", "llama3"),
                MakeProvider("second", "llama3"),
            ],

        };

        FakeProviderHealthTracker health = new();

        health.Unhealthy.Add("first");

        IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> candidates =
            ProviderResolver.ResolveCandidates(settings, "llama3", health);

        (ProviderSettings provider, string _) = Assert.Single(candidates);

        Assert.Equal("second", provider.Name);

    }

    [Fact]
    public void ResolveCandidates_returns_first_when_all_unhealthy()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [
                MakeProvider("first", "llama3"),
                MakeProvider("second", "llama3"),
            ],

        };

        FakeProviderHealthTracker health = new();

        health.Unhealthy.Add("first");

        health.Unhealthy.Add("second");

        IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> candidates =
            ProviderResolver.ResolveCandidates(settings, "llama3", health);

        (ProviderSettings provider, string _) = Assert.Single(candidates);

        Assert.Equal("first", provider.Name);

    }

    private sealed class FakeProviderHealthTracker : IProviderHealthTracker
    {

        public HashSet<string> Unhealthy { get; } = new(StringComparer.Ordinal);

        public event Action<ProviderHealthStatus>? HealthChanged;

        public bool IsHealthy(string providerName) => !Unhealthy.Contains(providerName);

        public void MarkFailed(string providerName) => Unhealthy.Add(providerName);

        public void MarkHealthy(string providerName)
        {

            Unhealthy.Remove(providerName);

            HealthChanged?.Invoke(new ProviderHealthStatus(providerName, true, DateTimeOffset.UtcNow, 0));

        }

        public IReadOnlyList<ProviderHealthStatus> GetAllStatuses() =>
            Unhealthy.Select(name => new ProviderHealthStatus(name, false, DateTimeOffset.UtcNow, 1)).ToList();

    }

}
