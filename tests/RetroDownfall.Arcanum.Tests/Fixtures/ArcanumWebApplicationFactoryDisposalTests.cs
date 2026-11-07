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

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposeAsync_waits_for_entrypoint_owned_async_disposal_before_restoring_environment(bool delegated, bool synchronous)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string parentHome = CreateTempHome("parent-entrypoint-disposal");

        using TestingEnvironmentScope scope = new(parentHome);

        EntryPointDisposalBarrier barrier = new();

        ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services => services.AddSingleton<IHostedService>(_ => barrier),
        };

        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> active = delegated
            ? factory.WithWebHostBuilder(_ => { })
            : factory;

        _ = active.Services;

        string factoryHome = factory.TempHome;

        Task? disposal = null;

        try
        {
            // Establish ownership before calling factory disposal: the entrypoint must already be
            // inside its blocked asynchronous DI disposal. Stop callback arrival order is not ownership.
            active.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

            await barrier.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // WebApplicationFactory.DisposeAsync can call IHost.Dispose synchronously before its
            // first await. Keep either API off the driver so it can release the barrier, and use a
            // dedicated caller rather than occupying a pooled worker needed by shutdown continuations.
            disposal = Task.Factory.StartNew(async () =>
            {
                if (synchronous)
                {
                    active.Dispose();
                }
                else
                {
                    await active.DisposeAsync().ConfigureAwait(false);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

            await Assert.ThrowsAsync<TimeoutException>(() => disposal.WaitAsync(TimeSpan.FromMilliseconds(250)));

            Assert.Equal(factoryHome, global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"));

            Assert.True(Directory.Exists(factoryHome));
        }
        finally
        {
            barrier.ReleaseDisposal.TrySetResult();

            try
            {
                if (disposal is not null)
                {
                    await disposal.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
            finally
            {
                await factory.DisposeAsync();
            }
        }

        Assert.True(barrier.DisposalFinished.Task.IsCompletedSuccessfully);

        Assert.Equal(parentHome, global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"));

        await using ArcanumWebApplicationFactory next = new();

        _ = next.Services;

        Assert.True(next.Services.GetRequiredService<RetroDownfall.Arcanum.Core.Storage.IGrimoireDbReadiness>().IsReady);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Entrypoint_failure_finishes_cleanup_and_releases_factory_isolation(bool failStartup)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string parentHome = CreateTempHome("parent-entrypoint-failure");

        using TestingEnvironmentScope scope = new(parentHome);

        EntrypointFailureProbe probe = new(failStartup);

        ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services => services.AddSingleton<IHostedService>(_ => probe),
        };

        string factoryHome = factory.TempHome;

        if (failStartup)
        {
            Exception failure = Assert.ThrowsAny<Exception>(() => _ = factory.Services);

            Assert.Contains("expected entrypoint startup failure", failure.ToString(), StringComparison.Ordinal);

            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        }
        else
        {
            _ = factory.Services;

            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Contains("expected entrypoint disposal failure", failure.ToString(), StringComparison.Ordinal);
        }

        Assert.True(probe.Disposed);

        Assert.Equal(parentHome, global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"));

        Assert.False(Directory.Exists(factoryHome));
    }

    private sealed class EntrypointFailureProbe(bool failStartup) : IHostedService, IAsyncDisposable
    {
        internal bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => failStartup
            ? Task.FromException(new InvalidOperationException("expected entrypoint startup failure"))
            : Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return failStartup ? ValueTask.CompletedTask
                : ValueTask.FromException(new InvalidOperationException("expected entrypoint disposal failure"));
        }
    }

    private sealed class EntryPointDisposalBarrier : IHostedService, IAsyncDisposable
    {
        private int _disposals;

        internal TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposalFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposals) != 1)
            {
                return;
            }

            DisposalStarted.TrySetResult();

            await ReleaseDisposal.Task;

            DisposalFinished.TrySetResult();
        }
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

                // Stop is idempotent at the service boundary even though the framework and entrypoint
                // can both request it. Every pass now finishes before the factory releases isolation.
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
