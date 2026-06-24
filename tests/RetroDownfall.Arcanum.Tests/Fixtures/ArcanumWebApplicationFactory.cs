using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class ArcanumWebApplicationFactory : WebApplicationFactory<Program>
{

    public const string TestApiKey = GrimoireFixture.TestApiKey;

    private readonly string _tempHome;

    private readonly GrimoireFixture? _grimoireFixture;

    private readonly FakeIntelligenceProvider _fakeIntelligence = new();

    private readonly TestApiKeySecretStore _secretStore = new(TestApiKey);

    public ArcanumWebApplicationFactory()
    {

        _tempHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"api-host-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempHome);

        ApplyIsolatedUserProfile();

        if (GrimoireFixture.SqlCipherAvailable)
        {

            _grimoireFixture = new GrimoireFixture();

        }

    }

    public FakeIntelligenceProvider FakeIntelligence => _fakeIntelligence;

    public string TempHome => _tempHome;

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

            services.RemoveAll<ISecretStore>();

            services.AddSingleton<ISecretStore>(_secretStore);

            services.RemoveAll<IArcanumIntelligenceProvider>();

            services.AddScoped<IArcanumIntelligenceProvider>(_ => _fakeIntelligence);

            services.RemoveAll<DbContextOptions<ArcanumDbContext>>();

            services.RemoveAll<ArcanumDbContext>();

            services.AddDbContext<ArcanumDbContext>();

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
                    Spells = built.Spells with
                    {
                        AllowedWorkspaceRoots = [_tempHome],
                    },
                    Campaigns = built.Campaigns with
                    {
                        AllowedRoots = [_tempHome],
                    },
                    Host = built.Host with
                    {
                        Workspace = _tempHome,
                    },
                    Perception = built.Perception with
                    {
                        AllowedWorkspaceRoots = [_tempHome],
                    },
                    EventBus = built.EventBus with
                    {
                        MaxSseConnections = 1,
                    },
                };

                return new TestOptionsMonitor<ArcanumSettings>(patched);

            });

            services.AddSingleton<IOptions<ArcanumSettings>>(sp =>
                Options.Create(sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue));

            services.AddSingleton<IOptionsSnapshot<ArcanumSettings>>(sp =>
                new TestOptionsSnapshot<ArcanumSettings>(
                    sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue));

        });

    }

    protected override IHost CreateHost(IHostBuilder builder)
    {

        SeedGrimoireDatabaseIfAvailable();

        global::System.Environment.SetEnvironmentVariable("ARCANUM_SKIP_KEY_BOOTSTRAP", "1");

        return base.CreateHost(builder);

    }

    private void ApplyIsolatedUserProfile()
    {

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

        File.Copy(_grimoireFixture.CopyDatabase(), databasePath, overwrite: true);

    }

    protected override void Dispose(bool disposing)
    {

        if (disposing)
        {

            global::System.Environment.SetEnvironmentVariable("ARCANUM_SKIP_KEY_BOOTSTRAP", null);

            _grimoireFixture?.Dispose();

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

        try
        {

            base.Dispose(disposing);

        }
        catch (ObjectDisposedException)
        {

        }

    }

}
