using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class TheReliquaryEvictionOverCapTests
{

    // W2.5 Fix 1: when every LRU eviction candidate is in use, the cache stays
    // over MaxCachedModels. EvictFromDirectoryAsync must NOT force-stop running
    // servers (destructive); instead it logs a warning naming the remaining
    // over-cap count and the reason (all candidates in use). The pull already
    // succeeded by the time eviction runs (the model is downloaded & cached),
    // so failing the pull would be semantically wrong — a warning is the
    // minimum-viable operator signal.

    [Fact]

    public async Task EvictFromDirectoryAsync_WhenAllCandidatesInUse_LogsOverCapWarningAndDeletesNothing()
    {

        string root = Path.Combine(Path.GetTempPath(), $"reliquary-evict-{Guid.NewGuid():N}");

        try
        {

            Directory.CreateDirectory(root);

            // maxCached=2, create 4 entries → over cap by 2. All in use → none evictable.

            for (int i = 0; i < 4; i++)
            {

                string dir = Path.Combine(root, $"model-{i}");

                Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(Path.Combine(dir, "model.gguf"), "x");

            }

            CapturingLogger<TheReliquary> logger = new();

            FakeLlamaServerManager manager = new(static _ => true);

            await TheReliquary.EvictFromDirectoryAsync(root, maxCached: 2, manager, logger, CancellationToken.None);

            // No directories deleted (all candidates in use).

            Assert.Equal(4, Directory.GetDirectories(root).Length);

            LogEntry? warning = logger.Entries.FirstOrDefault(
                e => e.Level == LogLevel.Warning
                    && e.Message.Contains("over cap", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(warning);

            // The remaining over-cap count (2) must appear in the message.

            Assert.Contains("2", warning!.Message);

        }

        finally
        {

            if (Directory.Exists(root))
            {

                Directory.Delete(root, recursive: true);

            }

        }

    }

    [Fact]

    public async Task EvictFromDirectoryAsync_WhenCandidatesNotInUse_EvictsDownToCapAndLogsNoOverCapWarning()
    {

        // Sanity: when candidates are NOT in use, normal LRU eviction occurs
        // and no over-cap warning is logged (the warning is specific to the
        // "all candidates in use" case).

        string root = Path.Combine(Path.GetTempPath(), $"reliquary-evict-{Guid.NewGuid():N}");

        try
        {

            Directory.CreateDirectory(root);

            for (int i = 0; i < 4; i++)
            {

                string dir = Path.Combine(root, $"model-{i}");

                Directory.CreateDirectory(dir);

                string modelPath = Path.Combine(dir, "model.gguf");

                await File.WriteAllTextAsync(modelPath, "x");

                // Distinct last-access times so LRU order is deterministic.

                File.SetLastAccessTimeUtc(modelPath, new DateTime(2024, 1, 1).AddDays(i));

            }

            CapturingLogger<TheReliquary> logger = new();

            FakeLlamaServerManager manager = new(static _ => false);

            await TheReliquary.EvictFromDirectoryAsync(root, maxCached: 2, manager, logger, CancellationToken.None);

            Assert.Equal(2, Directory.GetDirectories(root).Length);

            Assert.DoesNotContain(
                logger.Entries,
                e => e.Level == LogLevel.Warning
                    && e.Message.Contains("over cap", StringComparison.OrdinalIgnoreCase));

        }

        finally
        {

            if (Directory.Exists(root))
            {

                Directory.Delete(root, recursive: true);

            }

        }

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

    private sealed class FakeLlamaServerManager : ILlamaServerManager
    {

        private readonly Func<string, bool> _isModelInUse;

        public FakeLlamaServerManager(Func<string, bool> isModelInUse) => _isModelInUse = isModelInUse;

        public bool IsModelInUse(string cacheKey) => _isModelInUse(cacheKey);

        public Task<Result<LlamaServerInfo>> EnsureServerAsync(
            string cacheKey,
            string? sourceUrl,
            int? gpuLayersOverride,
            int? portOverride,
            CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<IDisposable> AcquireSlotAsync(string cacheKey, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public bool IsLlamaServerAvailable() => throw new NotImplementedException();

        public LlamaServerInfo? TryGetRunningServer(string cacheKey) => throw new NotImplementedException();

        public Task<Result> StopAsync(string cacheKey, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task StopAllAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

        public IReadOnlyList<LlamaServerInfo> ListServers() => throw new NotImplementedException();

    }

}
