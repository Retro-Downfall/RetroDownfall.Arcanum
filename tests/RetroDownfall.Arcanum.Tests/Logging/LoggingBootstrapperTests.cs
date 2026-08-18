using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace RetroDownfall.Arcanum.Tests.Logging;

[Collection("ProcessEnvironment")]
public sealed class LoggingBootstrapperTests
{

    [Fact]
    public void ResolveLogDirectory_UsesTestingIsolatedRoot()
    {

        string testHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        using TestingEnvironmentScope scope = new(testHome);

        string expected = Path.Combine(testHome, ".config", "arcanum", "logs");

        Assert.Equal(expected, LoggingBootstrapper.ResolveLogDirectory());

    }

    /// <summary>
    /// <c>IHttpClientFactory</c>'s default handlers write <c>"Start processing HTTP request {HttpMethod} {Uri}"</c>
    /// at Information under <c>System.Net.Http.HttpClient.*</c>. .NET redacts only the query, so a
    /// credential in a path segment would survive into the rolling log. Each named client suppresses
    /// its own loggers; this pins the pipeline-wide floor that catches any client that forgets to.
    /// </summary>
    [Fact]
    public void System_net_http_diagnostics_are_below_the_pipeline_floor()
    {

        string testHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        using TestingEnvironmentScope scope = new(testHome);

        Serilog.ILogger previousStaticLogger = Serilog.Log.Logger;

        try
        {

            ServiceCollection services = new();

            services.AddSingleton<ILogRingBuffer, InMemoryLogRingBuffer>();

            services.AddSingleton<SerilogLogRingBufferSink>();

            services.AddOptions<ArcanumSettings>();

            _ = services.AddArcanumSerilog();

            using ServiceProvider provider = services.BuildServiceProvider();

            ILoggerFactory factory = provider.GetRequiredService<ILoggerFactory>();

            ILogger httpClientLogger =
                factory.CreateLogger("System.Net.Http.HttpClient.McpHttp.LogicalHandler");

            Assert.False(httpClientLogger.IsEnabled(LogLevel.Information));

            Assert.True(httpClientLogger.IsEnabled(LogLevel.Warning));

            // The floor is scoped to the HTTP client categories; ordinary Arcanum diagnostics
            // must keep flowing at Information.
            Assert.True(factory.CreateLogger("McpConnectionManager").IsEnabled(LogLevel.Information));

        }
        finally
        {

            Serilog.Log.Logger = previousStaticLogger;

        }

    }

    private sealed class TestingEnvironmentScope : IDisposable
    {

        private readonly Dictionary<string, string?> _original = new();

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
