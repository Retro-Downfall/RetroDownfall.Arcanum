using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Infrastructure.Logging;

namespace RetroDownfall.Arcanum.Tests.Logging;

/// <summary>
/// Serilog's static <c>Log.Logger</c> is used by the pre-DI bootstrap diagnostics (Grimoire open
/// failures, master-key absence, HTTPS misconfiguration). These tests pin that those writes actually
/// reach the configured sinks instead of Serilog's silent default.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class StaticSerilogBridgeTests
{

    /// <summary>
    /// The master-key bootstrap runs against its own throwaway container before host
    /// <c>Build()</c> — the point at which <c>AddSerilog</c> assigns <see cref="Serilog.Log.Logger"/>
    /// — so its Fatal/Warning diagnostics used to land on Serilog's silent default and vanish.
    /// </summary>
    [Fact]
    public void Static_log_write_before_the_host_is_built_reaches_a_sink()
    {

        string testHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        using TestingEnvironmentScope scope = new(testHome);

        Serilog.ILogger previousStaticLogger = Serilog.Log.Logger;

        TextWriter previousError = Console.Error;

        StringWriter capturedError = new();

        try
        {

            Console.SetError(capturedError);

            LoggingBootstrapper.ResetBootstrapStaticLoggerForTests();

            ServiceCollection services = new();

            services.AddSingleton<ILogRingBuffer>(new RecordingRingBuffer());

            services.AddSingleton<SerilogLogRingBufferSink>();

            services.AddOptions<ArcanumSettings>();

            // No provider is built: this is the pre-Build window the bootstrap logger exists for.
            _ = services.AddArcanumSerilog();

            string marker = Guid.NewGuid().ToString("N");

            Serilog.Log.Fatal("Master API key store is corrupt: {Marker}", marker);

            Serilog.Log.CloseAndFlush();

            Assert.Contains(marker, capturedError.ToString(), StringComparison.Ordinal);

        }
        finally
        {

            Console.SetError(previousError);

            Serilog.Log.Logger = previousStaticLogger;

        }

    }

    /// <summary>
    /// Once the DI logging graph is materialized, <c>AddSerilog</c> owns <see cref="Serilog.Log.Logger"/>
    /// and static writes must reach the configured sinks — the ring buffer behind <c>GET /api/logs</c>
    /// included. Pins the behaviour the post-<c>Build()</c> bootstrap diagnostics depend on.
    /// </summary>
    [Fact]
    public void Static_log_write_after_the_host_is_built_reaches_the_ring_buffer_sink()
    {

        string testHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        using TestingEnvironmentScope scope = new(testHome);

        Serilog.ILogger previousStaticLogger = Serilog.Log.Logger;

        try
        {

            RecordingRingBuffer buffer = new();

            ServiceCollection services = new();

            services.AddSingleton<ILogRingBuffer>(buffer);

            services.AddSingleton<SerilogLogRingBufferSink>();

            services.AddOptions<ArcanumSettings>();

            _ = services.AddArcanumSerilog();

            using ServiceProvider provider = services.BuildServiceProvider();

            // The generic host resolves ILoggerFactory while building; mirror that here so the DI
            // logging graph is materialized exactly as it is in the real host.
            _ = provider.GetRequiredService<ILoggerFactory>();

            string marker = Guid.NewGuid().ToString("N");

            Serilog.Log.Error("Grimoire startup aborted: {Marker}", marker);

            Assert.Contains(buffer.Entries, entry => entry.Message.Contains(marker, StringComparison.Ordinal));

        }
        finally
        {

            Serilog.Log.Logger = previousStaticLogger;

        }

    }

    private sealed class RecordingRingBuffer : ILogRingBuffer
    {

        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {

                lock (_entries)
                {

                    return [.. _entries];

                }

            }
        }

        public void Write(LogEntry entry)
        {

            lock (_entries)
            {

                _entries.Add(entry);

            }

        }

        public IReadOnlyList<LogEntry> GetSnapshot() => Entries;

        public async IAsyncEnumerable<LogEntry> StreamAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {

            await Task.CompletedTask.ConfigureAwait(false);

            foreach (LogEntry entry in Entries)
            {

                ct.ThrowIfCancellationRequested();

                yield return entry;

            }

        }

    }

    private sealed class TestingEnvironmentScope : IDisposable
    {

        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        public TestingEnvironmentScope(string testHome)
        {

            Set("ASPNETCORE_ENVIRONMENT", "Testing");

            Set("DOTNET_ENVIRONMENT", "Testing");

            Set("ARCANUM_TEST_HOME", testHome);

        }

        public void Dispose()
        {

            foreach (KeyValuePair<string, string?> entry in _original)
            {

                global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

            }

        }

        private void Set(string name, string value)
        {

            _original[name] = global::System.Environment.GetEnvironmentVariable(name);

            global::System.Environment.SetEnvironmentVariable(name, value);

        }

    }

}
