using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace RetroDownfall.Arcanum.Tests.Logging;

[Collection("ProcessEnvironment")]
public sealed class LoggingBootstrapperTests
{

    [Fact]
    public void Infrastructure_registration_does_not_create_the_guarded_root_or_log_path()
    {

        string testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            Guid.NewGuid().ToString("N"));

        using TestingEnvironmentScope scope = new(testHome);

        Serilog.ILogger previousStaticLogger = Serilog.Log.Logger;

        try
        {

            string guardedRoot = ArcanumPaths.GrimoireDirectory;

            string logDirectory = ArcanumPaths.LogDirectory;

            ServiceCollection services = new();

            _ = services.AddArcanumInfrastructure(
                new ConfigurationBuilder().Build());

            Assert.False(Directory.Exists(guardedRoot));

            Assert.False(Directory.Exists(logDirectory));

        }
        finally
        {

            Serilog.Log.Logger = previousStaticLogger;

            if (Directory.Exists(testHome))
            {

                Directory.Delete(testHome, recursive: true);

            }

        }

    }

    [Fact]
    public void Production_logging_materialization_and_prestart_emit_do_not_create_the_guarded_root_or_log_path()
    {

        string testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            Guid.NewGuid().ToString("N"));

        using TestingEnvironmentScope scope = new(
            testHome,
            dotnetEnvironment: "Testing",
            aspNetCoreEnvironment: Environments.Production);

        Serilog.ILogger previousStaticLogger = Serilog.Log.Logger;

        try
        {

            string guardedRoot = ArcanumPaths.GrimoireDirectory;

            string logDirectory = ArcanumPaths.LogDirectory;

            ServiceCollection services = new();

            services.AddSingleton<IHostEnvironment>(new ProductionHostEnvironment());

            _ = services.AddArcanumInfrastructure(
                new ConfigurationBuilder().Build());

            using ServiceProvider provider = services.BuildServiceProvider();

            ILoggerFactory factory = provider.GetRequiredService<ILoggerFactory>();

            factory.CreateLogger("PreLockStartup")
                .LogInformation("Pre-lock startup diagnostic.");

            Assert.False(Directory.Exists(guardedRoot));

            Assert.False(Directory.Exists(logDirectory));

        }
        finally
        {

            Serilog.Log.Logger = previousStaticLogger;

            if (Directory.Exists(testHome))
            {

                Directory.Delete(testHome, recursive: true);

            }

        }

    }

    [Fact]
    public void Host_lock_file_sink_is_inert_until_activation_and_never_reopens_after_deactivation()
    {

        string retainedParent = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            Guid.NewGuid().ToString("N"));

        string guardedRoot = Path.Combine(retainedParent, "arcanum");

        string logDirectory = Path.Combine(guardedRoot, "logs");

        HostLockSerilogFileSink sink = new(
            guardedRoot,
            logDirectory,
            retainedFileCountLimit: 3,
            enabled: true);

        using Serilog.Core.Logger logger = new Serilog.LoggerConfiguration()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("Before the host lock is attached.");

        Assert.False(Directory.Exists(guardedRoot));

        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        Assert.False(Directory.Exists(guardedRoot));

        sink.Activate(held, guardedRoot);

        logger.Information("The host lock is attached and topology is converged.");

        Assert.NotEmpty(Directory.GetFiles(logDirectory, "*.json"));

        sink.Deactivate();

        Directory.Delete(guardedRoot, recursive: true);

        logger.Information("After host lock release.");

        Assert.False(Directory.Exists(guardedRoot));

        Assert.Throws<InvalidOperationException>(() =>
            sink.Activate(held, guardedRoot));

    }

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

        public TestingEnvironmentScope(
            string testHome,
            string dotnetEnvironment = "Testing",
            string aspNetCoreEnvironment = "Testing")
        {

            Set("ASPNETCORE_ENVIRONMENT", aspNetCoreEnvironment);

            Set("DOTNET_ENVIRONMENT", dotnetEnvironment);

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

    private sealed class ProductionHostEnvironment : IHostEnvironment
    {

        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Arcanum.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();

    }

}
