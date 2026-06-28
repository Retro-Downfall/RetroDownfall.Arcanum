using System.Collections;
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
            Providers = BuildManyOllamaProviders(33),
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

    private static ProviderSettings[] BuildManyOllamaProviders(int count)
    {

        ProviderSettings[] providers = new ProviderSettings[count];

        for (int i = 0; i < count; i++)
        {

            providers[i] = new ProviderSettings
            {
                Name = $"ollama-{i}",
                Type = AiProviderKind.Ollama,
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
            new UnsupportedLlamaServerManager(),
            secretProtector,
            NullLogger<ChatClientFactory>.Instance);

    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new();

    }

    private sealed class UnsupportedLlamaServerManager : ILlamaServerManager
    {

        public Task<Result<LlamaServerInfo>> EnsureServerAsync(
            string modelKey,
            string? sourceUrl,
            int? gpuLayersOverride,
            int? portOverride,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IDisposable> AcquireSlotAsync(string modelKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsModelInUse(string cacheKey) => false;

        public bool IsLlamaServerAvailable() => false;

        public LlamaServerInfo? TryGetRunningServer(string cacheKey) => null;

        public Task<Result> StopAsync(string cacheKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<LlamaServerInfo> ListServers() => [];

    }

}
