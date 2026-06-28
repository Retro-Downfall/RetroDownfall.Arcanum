using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ChatClientFactoryOverCapWarningTests
{

    // W3.3 Fix 3: the endpoint HttpClient cache is a soft cap. EvictExcessEndpointClients
    // only removes RefCount == 0 entries; when every entry is leased (RefCount > 0) the
    // cache stays over MaxCachedEndpointClients. The minimum-viable operator signal is a
    // warning naming the remaining over-cap count (same shape as the W2.5b LRU-over-cap
    // warning). We must NOT block or refuse new endpoint keys — that would break inference.
    [Fact]
    public async Task EvictExcessEndpointClients_WhenAllEntriesLeased_LogsOverCapWarning()
    {

        ArcanumSettings settings = new()
        {

            DefaultModel = "m0",

            Providers = BuildManyOllamaProviders(33),

        };

        CapturingLogger<ChatClientFactory> logger = new();

        ChatClientFactory factory = CreateFactory(settings, logger);

        // Hold every lease so every cache entry has RefCount > 0 (none evictable).
        List<IDisposable> leases = [];

        try
        {

            for (int i = 0; i < 33; i++)
            {

                leases.Add(await factory.ResolveClientAsync($"m{i}", CancellationToken.None));

            }

            int countBefore = GetEndpointClientsDictionary(factory).Count;

            Assert.True(countBefore > MaxCachedEndpointClients, $"Precondition: cache should be over cap ({countBefore} > {MaxCachedEndpointClients}).");

            ForceEvictExcessEndpointClients(factory);

            // All entries leased → nothing evicted.
            Assert.Equal(countBefore, GetEndpointClientsDictionary(factory).Count);

            LogEntry? warning = logger.Entries.FirstOrDefault(
                e => e.Level == LogLevel.Warning
                    && e.Message.Contains("over cap", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(warning);

        }
        finally
        {

            foreach (IDisposable lease in leases)
            {

                lease.Dispose();

            }

        }

    }

    private const int MaxCachedEndpointClients = 32;

    private static IDictionary GetEndpointClientsDictionary(ChatClientFactory factory)
    {

        FieldInfo? field = typeof(ChatClientFactory).GetField(
            "_endpointHttpClients",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);

        return (IDictionary)field!.GetValue(factory)!;

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

    private static ChatClientFactory CreateFactory(ArcanumSettings settings, ILogger<ChatClientFactory> logger)
    {

        IDataProtectionProvider protection = DataProtectionProvider.Create("Arcanum.Tests.OverCapWarning");

        ConfigurationSecretProtector secretProtector = new(protection);

        return new ChatClientFactory(
            new FakeHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            new UnsupportedLlamaServerManager(),
            secretProtector,
            logger);

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

    private sealed class CapturingLogger<TCategory> : ILogger<TCategory>
    {

        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {

            get
            {

                lock (_entries)
                {

                    return _entries.ToList();

                }

            }

        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {

            string message = formatter(state, exception);

            lock (_entries)
            {

                _entries.Add(new LogEntry(logLevel, message, exception));

            }

        }

        private sealed class NoopDisposable : IDisposable
        {

            public static readonly NoopDisposable Instance = new();

            public void Dispose()
            {
            }

        }

    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

}
