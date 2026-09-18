using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

[Collection("ProcessEnvironment")]
public sealed class ArcanumWebApplicationFactoryDisposalTests
{

    private const string OwnedCredentialService = "arcanum-profile-disposal";

    private const string OwnedCredentialAccount = "owned-secret";

    [Fact]
    public async Task Profile_disposal_clears_owned_credentials_and_passphrase()
    {

        RestartableArcanumProfileFixture profile = new();

        SeedOwnedSecrets(profile);

        await profile.DisposeAsync();

        AssertOwnedSecretsCleared(profile);

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Profile_disposal_attempts_every_step_before_becoming_terminal(
        bool failPoolCleanup)
    {

        List<string> attempts = [];

        InvalidOperationException expected = new("expected cleanup failure");

        RestartableArcanumProfileFixture profile = new(
            clearPools: () =>
            {

                attempts.Add("pools");

                if (failPoolCleanup)
                {

                    throw expected;

                }

            },
            disposeGrimoire: () =>
            {

                attempts.Add("grimoire");

                if (!failPoolCleanup)
                {

                    throw expected;

                }

            });

        SeedOwnedSecrets(profile);

        string profileHome = profile.TempHome;

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => profile.DisposeAsync().AsTask());

        Assert.Same(expected, thrown);

        Assert.Equal(["pools", "grimoire"], attempts);

        Assert.False(Directory.Exists(profileHome));

        AssertOwnedSecretsCleared(profile);

        await profile.DisposeAsync();

        Assert.Equal(["pools", "grimoire"], attempts);

        AssertOwnedSecretsCleared(profile);

    }

    [SkippableFact]
    public async Task Host_disposal_never_deletes_an_externally_owned_restartable_profile()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string parentHome = CreateTempHome("parent-restartable");

        using TestingEnvironmentScope scope = new(parentHome);

        RestartableArcanumProfileFixture profile = new();

        SeedOwnedSecrets(profile);

        string profileHome = profile.TempHome;

        string siblingSentinel = Path.Combine(parentHome, "survives-profile-cleanup.txt");

        File.WriteAllText(siblingSentinel, "outside restartable profile");

        try
        {

            ShutdownPathProbe probe = new();

            ArcanumWebApplicationFactory factory = profile.CreateFactory();

            factory.ServiceOverrides = services => services.AddSingleton<IHostedService>(probe);

            using HttpClient client = factory.CreateAuthenticatedClient();

            factory.Dispose();

            AssertShutdownUsedFactoryGrimoire(probe, profileHome);

            Assert.Equal(
                parentHome,
                global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"));

            Assert.True(Directory.Exists(profileHome));

            Assert.True(File.Exists(Path.Combine(profileHome, ".config", "arcanum", "arcanum.db")));

            Assert.True(File.Exists(Path.Combine(profileHome, ".config", "arcanum", "arcanum.db.kdf")));

            AssertOwnedSecretsRetained(profile);

            await profile.DisposeAsync();

            Assert.False(Directory.Exists(profileHome));

            AssertOwnedSecretsCleared(profile);

            Assert.Equal(
                "outside restartable profile",
                File.ReadAllText(siblingSentinel));

        }
        finally
        {

            await profile.DisposeAsync();

        }

    }

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

    private static void SeedOwnedSecrets(RestartableArcanumProfileFixture profile)
    {

        OsCredentialStoreResult stored = profile.CredentialStore.Set(
            OwnedCredentialService,
            OwnedCredentialAccount,
            Guid.NewGuid().ToString("N"));

        Assert.Equal(OsCredentialStoreStatus.Ok, stored.Status);

        stored = default;

        if (profile.Grimoire is null)
        {

            profile.PassphraseSource.SetPassphrase(Guid.NewGuid().ToString("N"));

        }

    }

    private static void AssertOwnedSecretsRetained(RestartableArcanumProfileFixture profile)
    {

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            profile.CredentialStore.TryGet(
                OwnedCredentialService,
                OwnedCredentialAccount).Status);

        Assert.True(profile.PassphraseSource.Passphrase.Length > 0);

    }

    private static void AssertOwnedSecretsCleared(RestartableArcanumProfileFixture profile)
    {

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            profile.CredentialStore.TryGet(
                OwnedCredentialService,
                OwnedCredentialAccount).Status);

        _ = Assert.Throws<InvalidOperationException>(() =>
            _ = profile.PassphraseSource.Passphrase);

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

        private int _captured;

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

            if (Interlocked.Exchange(ref _captured, 1) != 0)
            {

                // Shutdown runs this hosted service more than once. The factory stops the host, which
                // releases the DevHost entry point parked in WaitForShutdownAsync — that stops the host
                // a second time, and Program's finally stops it a third. Only the first pass is awaited
                // inside the factory's isolated environment; the later ones can still be delaying when
                // the environment is restored, so they must not overwrite what the first pass observed.
                return;

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
