using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

[Collection("ProcessEnvironment")]
public sealed class ArcanumWebApplicationFactoryDisposalTests
{

    [SkippableFact]
    public void Dispose_StopsHostBeforeRestoringTestingIsolation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string parentHome = CreateTempHome("parent-sync");

        using TestingEnvironmentScope scope = new(parentHome);

        ShutdownPathProbe probe = new();

        ArcanumWebApplicationFactory factory = CreateStartedFactory(probe);

        string factoryHome = factory.TempHome;

        factory.Dispose();

        AssertShutdownUsedFactoryGrimoire(probe, factoryHome);

        Assert.Equal(parentHome, global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"));

        Assert.False(Directory.Exists(factoryHome));

    }

    [SkippableFact]
    public async Task DisposeAsync_StopsHostBeforeRestoringTestingIsolation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string parentHome = CreateTempHome("parent-async");

        using TestingEnvironmentScope scope = new(parentHome);

        ShutdownPathProbe probe = new(delayBeforeCapture: true);

        ArcanumWebApplicationFactory factory = CreateStartedFactory(probe);

        string factoryHome = factory.TempHome;

        await ((IAsyncDisposable)factory).DisposeAsync();

        AssertShutdownUsedFactoryGrimoire(probe, factoryHome);

        Assert.Equal(parentHome, global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"));

        Assert.False(Directory.Exists(factoryHome));

    }

    private static ArcanumWebApplicationFactory CreateStartedFactory(ShutdownPathProbe probe)
    {

        ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services => services.AddSingleton<IHostedService>(probe),
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        return factory;

    }

    private static void AssertShutdownUsedFactoryGrimoire(ShutdownPathProbe probe, string factoryHome)
    {

        string expectedRoot = Path.Combine(factoryHome, ".config", "arcanum");

        Assert.Equal(expectedRoot, probe.GrimoireDirectoryAtShutdown);

        Assert.Equal(Path.Combine(expectedRoot, "arcanum.db"), probe.DatabasePathAtShutdown);

        Assert.True(probe.DatabaseExistedAtShutdown);

    }

    private static string CreateTempHome(string name)
    {

        string path = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"{name}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        return path;

    }

    private sealed class ShutdownPathProbe : IHostedService
    {

        private readonly bool _delayBeforeCapture;

        public ShutdownPathProbe(bool delayBeforeCapture = false)
        {

            _delayBeforeCapture = delayBeforeCapture;

        }

        public string? GrimoireDirectoryAtShutdown { get; private set; }

        public string? DatabasePathAtShutdown { get; private set; }

        public bool DatabaseExistedAtShutdown { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {

            if (_delayBeforeCapture)
            {

                // Make asynchronous teardown observable. The factory must keep its isolated
                // process environment active until every hosted service finishes stopping.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);

            }

            GrimoireDirectoryAtShutdown = ArcanumPaths.GrimoireDirectory;

            DatabasePathAtShutdown = ArcanumPaths.GrimoireDatabaseFile;

            DatabaseExistedAtShutdown = File.Exists(DatabasePathAtShutdown);

        }

    }

    private sealed class TestingEnvironmentScope : IDisposable
    {

        private readonly string _home;

        private readonly Dictionary<string, string?> _original = new();

        public TestingEnvironmentScope(string home)
        {

            _home = home;

            Set("ASPNETCORE_ENVIRONMENT", "Testing");

            Set("DOTNET_ENVIRONMENT", "Testing");

            Set("ARCANUM_TEST_HOME", home);

        }

        public void Dispose()
        {

            foreach (KeyValuePair<string, string?> entry in _original)
            {

                global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

            }

            if (Directory.Exists(_home))
            {

                Directory.Delete(_home, recursive: true);

            }

        }

        private void Set(string name, string value)
        {

            _original[name] = global::System.Environment.GetEnvironmentVariable(name);

            global::System.Environment.SetEnvironmentVariable(name, value);

        }

    }

}
