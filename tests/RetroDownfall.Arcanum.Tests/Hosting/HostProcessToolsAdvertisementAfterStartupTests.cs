using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;
using SysEnv = System.Environment;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// What the host advertises after the startup gate has classified this installation.
/// </summary>
/// <remarks>
/// The gate publishes one decision and every admission site is supposed to obey it. A Development
/// host started with the escape hatch against a provably clean installation is the case where the
/// published decision and the legacy edition-plus-environment rule disagree: the gate refuses, the
/// host degrades to a warning and starts, and nothing that advertises a tool may hand out
/// <c>execute_command</c> or <c>run_spell_script</c> afterwards (§10.12, §11.1).
///
/// <para>Both advertisement surfaces are the production ones. <c>execute_command</c> exists only
/// when <see cref="McpConnectionManager"/> builds the in-process tool server for a workspace, and
/// <c>run_spell_script</c> only when a spell preview reports the tools a cast would have; a test
/// that asked the policy predicate directly would prove nothing about either.</para>
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class HostProcessToolsAdvertisementAfterStartupTests : IAsyncLifetime
{

    private const string SpellName = "escape-hatch-preview";

    private readonly TempWorkspace _workspace = new();

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly RecordedSecretStore _secrets = new();

    private readonly GrimoireDbPassphraseSource _passphrase = new();

    private string _tempDir = string.Empty;

    private string _dbPath = string.Empty;

    public async Task InitializeAsync()
    {

        SqliteNativeRuntime.Instance.Initialize();

        await _workspace.InitializeAsync();

        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"host-tools-advert-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "grimoire.db");

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

        try
        {

            if (Directory.Exists(_tempDir))
            {

                Directory.Delete(_tempDir, recursive: true);

            }

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            // Best-effort cleanup of a temporary directory.

        }

    }

    [Fact]
    public async Task A_host_started_with_the_escape_hatch_and_no_transition_advertises_no_host_process_tools()
    {

        using HostProcessToolsEscapeHatchScope hatch = new();

        string? previousTestHome = SysEnv.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        string? previousDotnetEnvironment = SysEnv.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        string? previousAspNetCoreEnvironment =
            SysEnv.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        try
        {

            // Keeps the connection manager's configuration reads inside this test's temporary tree
            // instead of the operator's real profile, where a configured server would be started.
            SysEnv.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

            SysEnv.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

            SysEnv.SetEnvironmentVariable("ARCANUM_TEST_HOME", _tempDir);

            Assert.StartsWith(
                Path.GetFullPath(_tempDir),
                Path.GetFullPath(ArcanumPaths.GlobalMcpConfigFile),
                StringComparison.Ordinal);

            _secrets.SetApiKey("test-api-key");

            WriteSpellWithScript();

            IServiceScopeFactory scopes =
                GrimoireDatabaseBootstrapperTests.CreateCovenantAuthorityScopeFactory(
                    _credentials,
                    covenantEnabled: true);

            await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                _secrets,
                _passphrase,
                scopes,
                _dbPath,
                _tempDir,
                CancellationToken.None);

            using IServiceScope scope = scopes.CreateScope();

            HostProcessToolsRuntimePolicy policy = scope.ServiceProvider
                .GetRequiredService<HostProcessToolsRuntimePolicy>();

            // The premise of the scenario, asserted before the advertisement so a failure below is
            // about what the host handed out and not about a host that never reached this state.
            Assert.True(policy.IsPublished);

            Assert.Equal(
                HostProcessToolsStartupBlocker.EscapeHatchWithoutTransition,
                policy.Blocker);

            Assert.False(policy.HostProcessToolsPermitted);

            await using McpConnectionManager mcp = CreateConnectionManager();

            IReadOnlyList<AITool> advertised = await mcp.GetAvailableToolsAsync(_workspace.Root);

            string[] advertisedNames = [.. advertised.Select(static tool => tool.Name)];

            SpellCastPreviewService preview = new(
                mcp,
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
                NullLogger<SpellCastPreviewService>.Instance);

            Result<SpellCastResult> cast = await preview.CastAsync(
                SpellName,
                _workspace.Root,
                CancellationToken.None);

            Assert.True(cast.IsSuccess, cast.IsFailure ? cast.Error.Message : string.Empty);

            // The control: the preview only reaches the host-tool decision when the spell carries a
            // script, so an empty script set would make the assertion below pass for the wrong reason.
            Assert.NotEmpty(cast.Value.AvailableSpellScripts);

            Assert.DoesNotContain(
                HostProcessToolPolicy.ExecuteCommandToolName,
                advertisedNames,
                StringComparer.Ordinal);

            Assert.DoesNotContain(
                HostProcessToolPolicy.RunSpellScriptToolName,
                advertisedNames,
                StringComparer.Ordinal);

            Assert.DoesNotContain(
                HostProcessToolPolicy.ExecuteCommandToolName,
                cast.Value.AvailableTools,
                StringComparer.Ordinal);

            Assert.DoesNotContain(
                HostProcessToolPolicy.RunSpellScriptToolName,
                cast.Value.AvailableTools,
                StringComparer.Ordinal);

            // Handing the binding back restores the answer a process with no gate would give, which
            // is what keeps this test's refusal from following the rest of the run around.
            HostProcessToolPolicy.SetStartupDecisionForTests(null);

            Assert.True(HostProcessToolPolicy.AreAllowed(ArcanumEdition.Development));

        }
        finally
        {

            HostProcessToolPolicy.SetStartupDecisionForTests(null);

            SysEnv.SetEnvironmentVariable("ARCANUM_TEST_HOME", previousTestHome);

            SysEnv.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnetEnvironment);

            SysEnv.SetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT",
                previousAspNetCoreEnvironment);

        }

    }

    private void WriteSpellWithScript()
    {

        _workspace.WriteFile(
            $"spells/{SpellName}/SPELL.md",
            $"""
             ---
             name: {SpellName}
             description: Escape-hatch advertisement test spell
             ---
             body
             """);

        _workspace.WriteFile($"spells/{SpellName}/scripts/run.sh", "echo arcanum\n");

    }

    private static McpConnectionManager CreateConnectionManager()
    {

        ServiceCollection services = new();

        services.AddSingleton<ISanctumGuard, PermissiveSanctumGuard>();

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Security = new SecuritySettings { AllowUnsandboxedToolChildren = true },
                }));

        IServiceScopeFactory scopeFactory = services
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new SilentEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        return new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            new HumanPromptRegistry(),
            scopeFactory,
            pacer,
            new SilentEventBus(),
            new UntrustedWorkspaceStore(),
            new FakeHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    }

    private sealed class SilentEventBus : IEventBus
    {

        public void Publish<T>(T @event)
            where T : notnull
        {
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken)
            where T : notnull =>
            AsyncEnumerable.Empty<T>();

    }

    private sealed class UntrustedWorkspaceStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsApprovedDigestAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(default(TrustedMcpWorkspaceSnapshot));

        public Task TrustAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

    private sealed class PermissiveSanctumGuard : ISanctumGuard
    {

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            Core.Platform.ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

    private sealed class RecordedSecretStore : ISecretStore
    {

        private string? _apiKey;

        private string? _grimoireSecret;

        public void SetApiKey(string apiKey) =>
            _apiKey = apiKey;

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult(_apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(_apiKey is null
                ? SecretStoreReadResult.Missing()
                : SecretStoreReadResult.Ok(_apiKey));

        public Task SaveApiKeyAsync(string apiKey)
        {

            _apiKey = apiKey;

            return Task.CompletedTask;

        }

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult(_grimoireSecret);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {

            _grimoireSecret = encryptionSecret;

            return Task.CompletedTask;

        }

    }

}
