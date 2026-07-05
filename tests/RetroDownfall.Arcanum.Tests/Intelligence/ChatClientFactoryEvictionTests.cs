using System.Collections;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ChatClientFactoryEvictionTests
{

    [Fact]
    public async Task Eviction_SkipsInUseEndpointHttpClient()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "m0",
            Providers = BuildManyLlamaCppProviders(33),
        };

        ChatClientFactory factory = CreateFactory(settings);

        for (int i = 1; i < 33; i++)
        {

            using ChatClientLease idleLease = await factory.ResolveClientAsync($"m{i}", CancellationToken.None);

        }

        using ChatClientLease heldLease = await factory.ResolveClientAsync("m0", CancellationToken.None);

        string heldKey = NormalizeEndpointKey(heldLease.Provider.Endpoint);

        int heldRefCountBefore = GetRefCount(factory, heldKey);

        Assert.Equal(1, heldRefCountBefore);

        ForceEvictExcessEndpointClients(factory);

        Assert.Equal(1, GetRefCount(factory, heldKey));

        Assert.True(ContainsCacheKey(factory, heldKey));

    }

    [Fact]
    public async Task Eviction_IsTrueLru_RefreshedEntrySurvives_OldestIdleEntryEvicted()
    {

        ArcanumSettings settings = new()
        {
            DefaultModel = "m0",
            Providers = BuildManyLlamaCppProviders(33),
        };

        ChatClientFactory factory = CreateFactory(settings);

        // Populate m1..m32 (32 distinct endpoints — exactly at MaxCachedEndpointClients) in order,
        // so m1 is accessed first (oldest) and m32 last (newest) among these.
        for (int i = 1; i <= 32; i++)
        {

            using ChatClientLease lease = await factory.ResolveClientAsync($"m{i}", CancellationToken.None);

        }

        // Re-access m1 so it becomes the most-recently-used idle entry. An arbitrary (non-LRU)
        // eviction policy could still pick m1 here; true LRU must never evict it while m2..m32 are
        // idle and older.
        using (await factory.ResolveClientAsync("m1", CancellationToken.None))
        {
        }

        // Acquiring one more distinct endpoint (m0) pushes the cache to 33 entries — over the 32
        // cap — triggering eviction of exactly one idle entry.
        using ChatClientLease heldLease = await factory.ResolveClientAsync("m0", CancellationToken.None);

        string m1Key = NormalizeEndpointKey($"http://127.0.0.1:{11434 + 1}");

        string m2Key = NormalizeEndpointKey($"http://127.0.0.1:{11434 + 2}");

        Assert.True(ContainsCacheKey(factory, m1Key), "m1 was refreshed most-recently and must survive LRU eviction.");

        Assert.False(ContainsCacheKey(factory, m2Key), "m2 is now the true least-recently-used idle entry and should have been evicted.");

    }

    private static int GetRefCount(ChatClientFactory factory, string cacheKey)
    {

        object entry = GetCacheEntry(factory, cacheKey);

        FieldInfo? refCountField = entry.GetType().GetField("RefCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(refCountField);

        return (int)refCountField!.GetValue(entry)!;

    }

    private static bool ContainsCacheKey(ChatClientFactory factory, string cacheKey)
    {

        IDictionary dictionary = GetEndpointClientsDictionary(factory);

        return dictionary.Contains(cacheKey);

    }

    private static object GetCacheEntry(ChatClientFactory factory, string cacheKey)
    {

        IDictionary dictionary = GetEndpointClientsDictionary(factory);

        Assert.True(dictionary.Contains(cacheKey));

        return dictionary[cacheKey]!;

    }

    private static IDictionary GetEndpointClientsDictionary(ChatClientFactory factory)
    {

        FieldInfo? field = typeof(ChatClientFactory).GetField(
            "_endpointHttpClients",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);

        return (IDictionary)field!.GetValue(factory)!;

    }

    private static string NormalizeEndpointKey(string endpoint)
    {

        var uri = new Uri(endpoint, UriKind.Absolute);

        return uri.AbsoluteUri.TrimEnd('/');

    }

    private static void ForceEvictExcessEndpointClients(ChatClientFactory factory)
    {

        MethodInfo? method = typeof(ChatClientFactory).GetMethod(
            "EvictExcessEndpointClients",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method!.Invoke(factory, null);

    }

    private static ProviderSettings[] BuildManyLlamaCppProviders(int count)
    {

        ProviderSettings[] providers = new ProviderSettings[count];

        for (int i = 0; i < count; i++)
        {

            providers[i] = new ProviderSettings
            {
                Name = $"llama-{i}",
                Type = AiProviderKind.LlamaCppServer,
                Endpoint = $"http://127.0.0.1:{11434 + i}",
                Models = [$"m{i}"],
            };

        }

        return providers;

    }

    private static ChatClientFactory CreateFactory(ArcanumSettings settings)
    {

        IDataProtectionProvider protection = DataProtectionProvider.Create("Arcanum.Tests.Eviction");

        ConfigurationSecretProtector secretProtector = new(protection);

        return new ChatClientFactory(
            new FakeHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            new SequencedLlamaServerManager(),
            secretProtector,
            NullLogger<ChatClientFactory>.Instance);

    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new();

    }

    private sealed class NoopDisposable : IDisposable
    {

        public void Dispose()
        {
        }

    }

    /// <summary>
    /// Assigns each <c>"m{i}"</c> model key a distinct endpoint (port <c>11434 + i</c>) so the
    /// endpoint-HttpClient cache — now exercised only by LlamaCppServer leases — gets one cache entry
    /// per provider, matching the fixed <see cref="ProviderSettings.Endpoint"/> the test uses for its
    /// own cache-key bookkeeping.
    /// </summary>
    private sealed class SequencedLlamaServerManager : ILlamaServerManager
    {

        public Task<Result<LlamaServerInfo>> EnsureServerAsync(
            string modelKey,
            string? sourceUrl,
            int? gpuLayersOverride,
            int? portOverride,
            CancellationToken cancellationToken)
        {

            int index = int.Parse(modelKey.TrimStart('m'), CultureInfo.InvariantCulture);

            int port = 11434 + index;

            return Task.FromResult(Result<LlamaServerInfo>.Success(new LlamaServerInfo
            {
                Endpoint = $"http://127.0.0.1:{port}",
                Port = port,
            }));

        }

        public Task<IDisposable> AcquireSlotAsync(string modelKey, CancellationToken cancellationToken) =>
            Task.FromResult<IDisposable>(new NoopDisposable());

        public bool IsModelInUse(string cacheKey) => false;

        public bool IsLlamaServerAvailable() => true;

        public LlamaServerInfo? TryGetRunningServer(string cacheKey) => null;

        public Task<Result> StopAsync(string cacheKey, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<LlamaServerInfo> ListServers() => [];

    }

}
