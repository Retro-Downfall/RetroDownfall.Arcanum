using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("ProcessEnvironment")]

public sealed class ConfigurationCommandServiceTests : IAsyncLifetime
{

    private const string PortVariable = "ARCANUM_Arcanum__Host__Port";

    private TempWorkspace _workspace = null!;

    private string? _originalPort;

    private string? _originalTestHome;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _originalPort = global::System.Environment.GetEnvironmentVariable(PortVariable);

        _originalTestHome = global::System.Environment.GetEnvironmentVariable(
            "ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _workspace.Root);

    }

    public async Task DisposeAsync()
    {

        global::System.Environment.SetEnvironmentVariable(PortVariable, _originalPort);

        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _originalTestHome);

        await _workspace.DisposeAsync();

    }

    [Fact]

    public async Task Local_read_keeps_persisted_and_environment_effective_values_distinct()
    {

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllTextAsync(
            ArcanumPaths.ConfigurationFile,
            """{"Arcanum":{"host":{"port":5001}}}""");

        global::System.Environment.SetEnvironmentVariable(PortVariable, "6124");

        ArcanumApiClient apiClient = new(
            new UnreachableHttpClientFactory(),
            new FakeSecretStore());

        ConfigurationCommandService service = new(
            apiClient,
            new ConfigurationValidator(),
            new ConfigurationWriter(NullLogger<ConfigurationWriter>.Instance),
            new RecordingGrimoireCliInitialization());

        Result<ConfigurationCommandSnapshot> result = await service.ReadAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(ConfigurationAccessMode.LocalBootstrap, result.Value.AccessMode);

        Assert.Equal(5001, result.Value.Settings.Host.Port);

        Assert.Equal(6124, result.Value.EffectiveSettings().Host.Port);

    }

    [Fact]

    public async Task Local_write_rejects_a_change_committed_after_the_snapshot_read()
    {

        global::System.Environment.SetEnvironmentVariable(PortVariable, null);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllTextAsync(
            ArcanumPaths.ConfigurationFile,
            """{"Arcanum":{"host":{"port":5001}}}""");

        ConfigurationWriter writer = new(NullLogger<ConfigurationWriter>.Instance);

        ConfigurationCommandService service = new(
            new ArcanumApiClient(
                new UnreachableHttpClientFactory(),
                new FakeSecretStore()),
            new ConfigurationValidator(),
            writer,
            new RecordingGrimoireCliInitialization());

        Result<ConfigurationCommandSnapshot> read = await service.ReadAsync(
            CancellationToken.None);

        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : null);

        ArcanumSettings competing = read.Value.Settings with
        {

            Host = read.Value.Settings.Host with { Port = 6124 },

        };

        Assert.True((await writer.WriteAsync(
            competing,
            CancellationToken.None)).IsSuccess);

        ArcanumSettings staleEdit = read.Value.Settings with
        {

            Host = read.Value.Settings.Host with { Port = 7333 },

        };

        Result write = await service.WriteAsync(
            read.Value,
            staleEdit,
            CancellationToken.None);

        Assert.True(write.IsFailure);

        Assert.Equal("Configuration.Changed", write.Error.Code);

        Assert.Equal(
            6124,
            ConfigurationBootstrapper.LoadPersistedArcanumSettings().Host.Port);

    }

    [Theory]
    [InlineData("A running host owns the maintenance lock.")]
    [InlineData("The maintenance lock topology is unsafe.")]
    [InlineData("An installation factory reset is active.")]
    public async Task Local_write_refusal_preserves_configuration_bytes(string refusal)
    {

        global::System.Environment.SetEnvironmentVariable(PortVariable, null);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        const string original = """{"Arcanum":{"host":{"port":5001}}}""";

        await File.WriteAllTextAsync(ArcanumPaths.ConfigurationFile, original);

        RecordingGrimoireCliInitialization initialization = new(refusal);

        ServiceCollection services = new();

        services.AddSingleton(
            new ArcanumApiClient(
                new UnreachableHttpClientFactory(),
                new FakeSecretStore()));

        services.AddSingleton(new ConfigurationValidator());

        services.AddSingleton(
            new ConfigurationWriter(NullLogger<ConfigurationWriter>.Instance));

        services.AddSingleton<IGrimoireCliInitialization>(initialization);

        await using ServiceProvider provider = services.BuildServiceProvider();

        ConfigurationCommandService service =
            ActivatorUtilities.CreateInstance<ConfigurationCommandService>(provider);

        ArcanumSettings persisted = ConfigurationBootstrapper.LoadPersistedArcanumSettings();

        ConfigurationCommandSnapshot snapshot = new(
            persisted,
            ConfigurationAccessMode.LocalBootstrap,
            []);

        ArcanumSettings candidate = persisted with
        {

            Host = persisted.Host with { Port = 7333 },

        };

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.WriteAsync(snapshot, candidate, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllTextAsync(ArcanumPaths.ConfigurationFile));

        Assert.Equal(1, initialization.ExclusiveCalls);

        Assert.Equal(0, initialization.BootstrapCalls);

    }

    private sealed class UnreachableHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(new UnreachableHandler())
            {

                BaseAddress = new Uri("http://127.0.0.1:5001/"),

            };

    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Connection refused."));

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

}
