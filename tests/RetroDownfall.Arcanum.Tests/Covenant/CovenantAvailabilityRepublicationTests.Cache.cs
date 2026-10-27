using RetroDownfall.Arcanum.Api.Intelligence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed partial class CovenantAvailabilityRepublicationTests
{
    [SkippableFact]
    public async Task Idle_passes_preserve_the_exact_published_availability_snapshot()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using var host = Host(new InMemoryOsCredentialStore());

        using var client = host.CreateAuthenticatedClient();

        await PassAsync(host);

        CovenantAvailabilitySnapshot before = Live(host);

        Assert.Equal(CovenantFtsSynchronizationState.Synchronized, before.FtsSynchronization);

        for (int pass = 0; pass < 10; pass++) await PassAsync(host);

        Assert.Same(before, Live(host));
    }

    [SkippableFact]
    public async Task Idle_native_maintenance_preserves_prepared_cache_marker_key_and_dispatch_evidence()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using var host = Host(new InMemoryOsCredentialStore());

        using var client = host.CreateAuthenticatedClient();

        await PassAsync(host);

        using PromptCacheDispatchValidatorTests.Fixture cache = new(
            authorityProvider: host.Services.GetRequiredService<IPromptCacheAuthoritySnapshotProvider>());

        var prepared = (await cache.Prepare())!;

        List<ChatMessage> messages = [new(ChatRole.System, prepared.Document.Render()), new(ChatRole.User, "fixture")];

        ModelCallContext context = new(cache.Provider, "gpt-5", 0, 0,
            PromptCacheBinding: prepared.Bind(messages, cache.Options));

        string key = prepared.CreatePlan(cache.Options, cache.Estimator).CacheKey;

        await PassAsync(host);

        var after = (await cache.Prepare())!;

        Assert.Equal(prepared.ScopeMarkerDigest, after.ScopeMarkerDigest);

        Assert.Equal(key, after.CreatePlan(cache.Options, cache.Estimator).CacheKey);

        Assert.Equal(PromptCacheDispatchStatus.Current,
            (await cache.Validator.ValidateAsync(cache.Chat, messages, cache.Options, context, Token)).Value);
    }
}
