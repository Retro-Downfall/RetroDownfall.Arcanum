using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class ArcanumWebApplicationFactory : WebApplicationFactory<Program>
{

    public const string TestApiKey = GrimoireFixture.TestApiKey;

    private const string InMemoryCredentialOptInVariable =
        "ARCANUM_TEST_IN_MEMORY_CREDENTIALS";

    private readonly RestartableArcanumProfileFixture _profile;

    private readonly bool _ownsProfile;

    private readonly FakeIntelligenceProvider _fakeIntelligence = new();

    private readonly TestApiKeySecretStore _secretStore = new(TestApiKey);

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    private IHost? _applicationHost;

    private int _applicationHostStopped;

    private int _isolatedResourcesDisposed;

    public ArcanumWebApplicationFactory()
        : this(new RestartableArcanumProfileFixture(), ownsProfile: true)
    {

    }

    internal ArcanumWebApplicationFactory(RestartableArcanumProfileFixture profile)
        : this(profile, ownsProfile: false)
    {

    }

    private ArcanumWebApplicationFactory(
        RestartableArcanumProfileFixture profile,
        bool ownsProfile)
    {

        _profile = profile;

        _ownsProfile = ownsProfile;

        CaptureEnvironment();

        // Program.cs reads environment before WebApplicationFactory's UseEnvironment can apply.
        // Set early so CreateSlimBuilder and master-key skip see Testing.
        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        ApplyIsolatedUserProfile();

    }

    private void CaptureEnvironment()
    {

        _originalEnvironment["HOME"] = global::System.Environment.GetEnvironmentVariable("HOME");
        _originalEnvironment["APPDATA"] = global::System.Environment.GetEnvironmentVariable("APPDATA");
        _originalEnvironment["USERPROFILE"] = global::System.Environment.GetEnvironmentVariable("USERPROFILE");
        _originalEnvironment["XDG_DATA_HOME"] = global::System.Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        _originalEnvironment["ARCANUM_TEST_HOME"] = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");
        _originalEnvironment[InMemoryCredentialOptInVariable] =
            global::System.Environment.GetEnvironmentVariable(InMemoryCredentialOptInVariable);
        _originalEnvironment["ARCANUM_SKIP_KEY_BOOTSTRAP"] = global::System.Environment.GetEnvironmentVariable("ARCANUM_SKIP_KEY_BOOTSTRAP");
        _originalEnvironment["ASPNETCORE_ENVIRONMENT"] = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        _originalEnvironment["DOTNET_ENVIRONMENT"] = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

    }

    private void RestoreEnvironment()
    {

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

    }

    public FakeIntelligenceProvider FakeIntelligence => _fakeIntelligence;

    public string TempHome => _profile.TempHome;

    /// <summary>
    /// Optional hook applied to the patched <see cref="ArcanumSettings"/> before the host starts, letting a
    /// single test flip an individual setting (e.g. <c>Workspaces.EnableFileWrite</c>) without needing a second
    /// derived host — building two hosts from the same instance both try to seed the same Grimoire database
    /// file, which collides. Must be set before the first client/host access.
    /// </summary>
    public Func<ArcanumSettings, ArcanumSettings>? SettingsOverride { get; set; }

    /// <summary>
    /// Optional hook to replace/add DI registrations (e.g. swap <c>IWeaveService</c> for a hand-written
    /// fake in RAG Phase 2/3 tests) on a dedicated, independently-constructed factory. Same
    /// "must be set before the first client/host access" rule as <see cref="SettingsOverride"/>.
    /// </summary>
    public Action<IServiceCollection>? ServiceOverrides { get; set; }

    /// <summary>Pre-created test interceptors appended after production connection enrolment.</summary>
    public IReadOnlyList<IInterceptor> AdditionalDbContextInterceptors { get; set; } = [];

    /// <summary>Test-only invocation hook around the real endpoint, inside its production middleware.</summary>
    public Func<HttpContext, RequestDelegate, Task>? BeforeEndpoint { get; set; }

    public HttpClient CreateAuthenticatedClient()
    {

        HttpClient client = CreateClient();

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, TestApiKey);

        return client;

    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {

        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            ServiceDescriptor? pidFileService = services.FirstOrDefault(static descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(PidFileService));

            if (pidFileService is not null)
            {
                _ = services.Remove(pidFileService);
            }

            services.RemoveAll<ISecretStore>();

            services.AddSingleton<ISecretStore>(_secretStore);

            services.RemoveAll<IOsCredentialStore>();

            services.AddSingleton(_profile.CredentialStore);

            services.RemoveAll<IGrimoireDbPassphraseSource>();

            services.AddSingleton(_profile.PassphraseSource);

            services.RemoveAll<IArcanumIntelligenceProvider>();

            services.AddScoped<IArcanumIntelligenceProvider>(_ => _fakeIntelligence);

            services.RemoveAll<IContextPreviewService>();

            services.AddScoped<IContextPreviewService>(_ => _fakeIntelligence);

            services.RemoveAll<IDbContextOptionsConfiguration<ArcanumDbContext>>();

            services.RemoveAll<DbContextOptions<ArcanumDbContext>>();

            services.RemoveAll<ArcanumDbContext>();

            if (_profile.Grimoire is not null)
            {
                string databasePath = Path.Combine(
                    _profile.TempHome,
                    ".config",
                    "arcanum",
                    "arcanum.db");
                string connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Password = _profile.PassphraseSource.Passphrase,
                    Pooling = true,
                }.ToString();

                // The enrolment interceptor is registered here for the same reason
                // ArcanumDbContextOptionsConfigurator registers it in the host: this branch replaces
                // the host's own options, and a suite whose connections were exempt from the Covenant
                // drain could not observe an erasure failing on a handle nothing closed.
                //
                // Pooled, like the host (ServiceCollectionExtensions AddDbContextPool), and not
                // because the suite needs the pool. AddDbContext gives scoped options, so this
                // lambda mints a fresh interceptor for every scope: one lock and one lifecycle table
                // per request, and no context or connection object ever reused. That is a real
                // composition - it is the CLI's - but it is not the host shape this suite stands in
                // for, and it cannot produce the state the interceptor's own contract is about, a
                // weak lifecycle entry following one physical connection across pooled reopens.
                services.AddDbContextPool<ArcanumDbContext>(
                    (sp, options) =>
                        ArcanumDbContextOptionsConfigurator.ConfigureServingEnrolment(
                            options.UseSqlite(connectionString).UseModel(ArcanumDbContextModel.Instance),
                            sp.GetRequiredService<IGrimoireOrdinaryConnectionLifecycle>(),
                            sp.GetRequiredService<ICovenantConnectionDrain>(),
                            sp.GetRequiredService<ICovenantSqliteConnectionInitializer>())
                            .AddInterceptors(AdditionalDbContextInterceptors),
                    poolSize: 32);
            }
            else
            {
                services.AddDbContext<ArcanumDbContext>();
            }

            services.RemoveAll<IOptions<ArcanumSettings>>();

            services.RemoveAll<IOptionsSnapshot<ArcanumSettings>>();

            services.RemoveAll<IOptionsMonitor<ArcanumSettings>>();

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(sp =>
            {

                ArcanumSettings built = sp.GetRequiredService<IOptionsFactory<ArcanumSettings>>().Create(Options.DefaultName);

                ArcanumSettings patched = built with
                {
                    DefaultModel = string.IsNullOrWhiteSpace(built.DefaultModel) ? "mistral:latest" : built.DefaultModel,
                    Providers = built.Providers is { Length: > 0 }
                        ? built.Providers
                        :
                        [
                            new ProviderSettings
                            {
                                Name = "test",
                                Type = AiProviderKind.OpenAICompatible,
                                Endpoint = "https://example.test/v1",
                                Models = ["mistral:latest"],
                            },
                        ],
                    Security = built.Security with
                    {
                        SpellWorkspaceRoots = [_profile.TempHome],
                        CampaignRoots = [_profile.TempHome],
                        PerceptionWorkspaceRoots = [_profile.TempHome],
                    },
                    Integrations = built.Integrations with
                    {
                        Embeddings = built.Integrations.Embeddings with
                        {
                            Dimensions = 64,
                        },
                    },
                    Execution = built.Execution with
                    {
                        MaxSseConnections = 1,
                    },
                    Workspaces = built.Workspaces with
                    {
                        DefaultRoot = _profile.TempHome,
                        EnableFileWrite = true,
                    },
                };

                if (SettingsOverride is not null)
                {
                    patched = SettingsOverride(patched);
                }

                return new TestOptionsMonitor<ArcanumSettings>(patched);

            });

            services.AddSingleton<IOptions<ArcanumSettings>>(sp =>
                Options.Create(sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue));

            services.AddSingleton<IOptionsSnapshot<ArcanumSettings>>(sp =>
                new TestOptionsSnapshot<ArcanumSettings>(
                    sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue));

            ServiceOverrides?.Invoke(services);

            if (BeforeEndpoint is not null)
            {
                services.AddSingleton<IStartupFilter>(new EndpointHookStartupFilter(BeforeEndpoint));
            }

        });

    }

    private sealed class EndpointHookStartupFilter(
        Func<HttpContext, RequestDelegate, Task> hook) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);

            app.UseEndpoints(endpoints =>
            {
                EndpointDataSource[] sources = endpoints.DataSources.ToArray();

                endpoints.DataSources.Clear();

                foreach (EndpointDataSource source in sources)
                {
                    endpoints.DataSources.Add(new HookedEndpointDataSource(source, hook));
                }
            });
        };
    }

    private sealed class HookedEndpointDataSource(
        EndpointDataSource inner,
        Func<HttpContext, RequestDelegate, Task> hook) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints => inner.Endpoints.Select(endpoint =>
        {
            if (endpoint is not RouteEndpoint route || route.RequestDelegate is not { } execute)
            {
                return endpoint;
            }

            return new RouteEndpoint(
                context => hook(context, execute), route.RoutePattern, route.Order, route.Metadata, route.DisplayName);
        }).ToArray();

        public override IChangeToken GetChangeToken() => inner.GetChangeToken();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {

        bool initialSeedClaimed = _profile.ClaimInitialSeed();

        if (initialSeedClaimed)
        {

            SeedGrimoireDatabaseIfAvailable();

        }

        global::System.Environment.SetEnvironmentVariable("ARCANUM_SKIP_KEY_BOOTSTRAP", "1");

        IHost host = base.CreateHost(builder);

        _applicationHost = host;

        if (initialSeedClaimed && _profile.Grimoire is not null)
        {

            SeedExternalInstallationIdentity(host);

        }

        return host;

    }

    private void SeedExternalInstallationIdentity(IHost host)
    {

        string grimoireDirectory = Path.Combine(
            _profile.TempHome,
            ".config",
            "arcanum");

        using IServiceScope scope = host.Services.CreateScope();

        Result<Guid> databaseIdentity = scope.ServiceProvider
            .GetRequiredService<IInstallationResetDatabaseIdentityReader>()
            .ReadAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Result<BackupRestoreProfileNamespace> profileNamespace =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(grimoireDirectory);

        Result<ArcanumMaintenanceLock> heldInstallationLock = host.Services
            .GetRequiredService<InstallationResetMaintenanceLockAccessor>()
            .BorrowHeldLock(grimoireDirectory);

        if (databaseIdentity.IsFailure
            || profileNamespace.IsFailure
            || heldInstallationLock.IsFailure)
        {

            throw new InvalidOperationException(
                "The restartable test profile's external installation identity could not be prepared.");

        }

        Result<Guid> seeded = new BackupRestoreJournalInstallationIdentityProvider(
            _profile.CredentialStore)
            .SeedFromDatabase(
                heldInstallationLock.Value,
                grimoireDirectory,
                profileNamespace.Value,
                databaseIdentity.Value);

        if (seeded.IsFailure)
        {

            throw new InvalidOperationException(
                "The restartable test profile's external installation identity could not be prepared.");

        }

    }

    private void ApplyIsolatedUserProfile()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _profile.TempHome);

        global::System.Environment.SetEnvironmentVariable(InMemoryCredentialOptInVariable, "1");

        global::System.Environment.SetEnvironmentVariable("HOME", _profile.TempHome);

        if (OperatingSystem.IsWindows())
        {

            string appData = Path.Combine(_profile.TempHome, "AppData", "Roaming");

            Directory.CreateDirectory(appData);

            global::System.Environment.SetEnvironmentVariable("APPDATA", appData);

            global::System.Environment.SetEnvironmentVariable("USERPROFILE", _profile.TempHome);

        }
        else if (OperatingSystem.IsLinux())
        {

            string xdgData = Path.Combine(_profile.TempHome, ".local", "share");

            Directory.CreateDirectory(xdgData);

            global::System.Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgData);

        }
        else
        {

            Directory.CreateDirectory(Path.Combine(_profile.TempHome, "Library", "Application Support"));

        }

        Directory.CreateDirectory(Path.Combine(_profile.TempHome, ".config", "arcanum"));

    }

    private void SeedGrimoireDatabaseIfAvailable()
    {

        if (_profile.Grimoire is not { } grimoire)
        {

            return;

        }

        string databasePath = Path.Combine(
            _profile.TempHome,
            ".config",
            "arcanum",
            "arcanum.db");

        string? directory = Path.GetDirectoryName(databasePath);

        if (!string.IsNullOrEmpty(directory))
        {

            Directory.CreateDirectory(directory);

        }

        string templateDatabasePath = grimoire.CopyDatabase();

        string templateSidecarPath = templateDatabasePath + ".kdf";

        File.Copy(templateDatabasePath, databasePath, overwrite: true);

        File.Copy(templateSidecarPath, databasePath + ".kdf", overwrite: true);

    }

    public override async ValueTask DisposeAsync()
    {

        try
        {

            try
            {

                await StopApplicationHostAsync();

            }
            finally
            {

                await base.DisposeAsync();

            }

        }
        finally
        {

            DisposeIsolatedResources();

        }

    }

    private async ValueTask StopApplicationHostAsync()
    {

        IHost? host = _applicationHost;

        if (host is null || Interlocked.Exchange(ref _applicationHostStopped, 1) != 0)
        {

            return;

        }

        await host.StopAsync();

    }

    private void DisposeIsolatedResources()
    {

        if (Interlocked.Exchange(ref _isolatedResourcesDisposed, 1) != 0)
        {

            return;

        }

        try
        {

            // The application host uses the normal pooled SQLite connection string. Once the host
            // and Grimoire checkpoint have stopped, release those test-process pools before deleting
            // an owned profile's isolated database tree on Windows.
            SqliteConnection.ClearAllPools();

            if (_ownsProfile)
            {

                _profile.DisposeAsync().AsTask().GetAwaiter().GetResult();

            }

        }
        finally
        {

            RestoreEnvironment();

        }

    }

}
