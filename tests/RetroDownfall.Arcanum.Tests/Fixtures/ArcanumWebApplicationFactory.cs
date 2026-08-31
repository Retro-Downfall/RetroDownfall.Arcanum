using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class ArcanumWebApplicationFactory : WebApplicationFactory<Program>
{

    public const string TestApiKey = GrimoireFixture.TestApiKey;

    private readonly string _tempHome;

    private readonly GrimoireFixture? _grimoireFixture;

    private readonly FakeIntelligenceProvider _fakeIntelligence = new();

    private readonly TestApiKeySecretStore _secretStore = new(TestApiKey);

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    private IHost? _applicationHost;

    private int _applicationHostStopped;

    private int _isolatedResourcesDisposed;

    public ArcanumWebApplicationFactory()
    {

        _tempHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"api-host-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempHome);

        CaptureEnvironment();

        // Program.cs reads environment before WebApplicationFactory's UseEnvironment can apply.
        // Set early so CreateSlimBuilder and master-key skip see Testing.
        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        ApplyIsolatedUserProfile();

        if (GrimoireFixture.SqlCipherAvailable)
        {

            _grimoireFixture = new GrimoireFixture();

        }

    }

    private void CaptureEnvironment()
    {

        _originalEnvironment["HOME"] = global::System.Environment.GetEnvironmentVariable("HOME");
        _originalEnvironment["APPDATA"] = global::System.Environment.GetEnvironmentVariable("APPDATA");
        _originalEnvironment["USERPROFILE"] = global::System.Environment.GetEnvironmentVariable("USERPROFILE");
        _originalEnvironment["XDG_DATA_HOME"] = global::System.Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        _originalEnvironment["ARCANUM_TEST_HOME"] = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");
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

    public string TempHome => _tempHome;

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

            services.RemoveAll<IArcanumIntelligenceProvider>();

            services.AddScoped<IArcanumIntelligenceProvider>(_ => _fakeIntelligence);

            services.RemoveAll<IContextPreviewService>();

            services.AddScoped<IContextPreviewService>(_ => _fakeIntelligence);

            services.RemoveAll<IDbContextOptionsConfiguration<ArcanumDbContext>>();

            services.RemoveAll<DbContextOptions<ArcanumDbContext>>();

            services.RemoveAll<ArcanumDbContext>();

            if (_grimoireFixture is { } grimoireFixture)
            {
                string databasePath = Path.Combine(
                    _tempHome,
                    ".config",
                    "arcanum",
                    "arcanum.db");
                string connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Password = grimoireFixture.Passphrase,
                    Pooling = true,
                }.ToString();

                // The enrolment interceptor is registered here for the same reason
                // ArcanumDbContextOptionsConfigurator registers it in the host: this branch replaces
                // the host's own options, and a suite whose connections were exempt from the Covenant
                // drain could not observe an erasure failing on a handle nothing closed.
                services.AddDbContext<ArcanumDbContext>((sp, options) =>
                    options
                        .UseSqlite(connectionString)
                        .UseModel(ArcanumDbContextModel.Instance)
                        .AddInterceptors(
                            new CovenantConnectionEnrolmentInterceptor(
                                sp.GetRequiredService<IGrimoireConnectionAdmissionGate>(),
                                sp.GetRequiredService<ICovenantConnectionDrain>(),
                                sp.GetRequiredService<ICovenantSqliteConnectionInitializer>())));
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
                        SpellWorkspaceRoots = [_tempHome],
                        CampaignRoots = [_tempHome],
                        PerceptionWorkspaceRoots = [_tempHome],
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
                        DefaultRoot = _tempHome,
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

        });

    }

    protected override IHost CreateHost(IHostBuilder builder)
    {

        SeedGrimoireDatabaseIfAvailable();

        global::System.Environment.SetEnvironmentVariable("ARCANUM_SKIP_KEY_BOOTSTRAP", "1");

        IHost host = base.CreateHost(builder);

        _applicationHost = host;

        return host;

    }

    private void ApplyIsolatedUserProfile()
    {

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _tempHome);

        global::System.Environment.SetEnvironmentVariable("HOME", _tempHome);

        if (OperatingSystem.IsWindows())
        {

            string appData = Path.Combine(_tempHome, "AppData", "Roaming");

            Directory.CreateDirectory(appData);

            global::System.Environment.SetEnvironmentVariable("APPDATA", appData);

            global::System.Environment.SetEnvironmentVariable("USERPROFILE", _tempHome);

        }
        else if (OperatingSystem.IsLinux())
        {

            string xdgData = Path.Combine(_tempHome, ".local", "share");

            Directory.CreateDirectory(xdgData);

            global::System.Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgData);

        }
        else
        {

            Directory.CreateDirectory(Path.Combine(_tempHome, "Library", "Application Support"));

        }

        Directory.CreateDirectory(Path.Combine(_tempHome, ".config", "arcanum"));

    }

    private void SeedGrimoireDatabaseIfAvailable()
    {

        if (_grimoireFixture is null)
        {

            return;

        }

        string databasePath = ArcanumPaths.GrimoireDatabaseFile;

        string? directory = Path.GetDirectoryName(databasePath);

        if (!string.IsNullOrEmpty(directory))
        {

            Directory.CreateDirectory(directory);

        }

        string templateDatabasePath = _grimoireFixture.CopyDatabase();

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

            _grimoireFixture?.Dispose();

            // The application host uses the normal pooled SQLite connection string. Once the host
            // and Grimoire checkpoint have stopped, release those test-process pools before deleting
            // this factory's isolated database tree on Windows.
            SqliteConnection.ClearAllPools();

            try
            {

                if (Directory.Exists(_tempHome))
                {

                    Directory.Delete(_tempHome, recursive: true);

                }

            }
            catch
            {

            }

        }
        finally
        {

            RestoreEnvironment();

        }

    }

}
