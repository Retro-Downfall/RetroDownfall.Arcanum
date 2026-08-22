using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Models.Comparisons;
using RetroDownfall.TheForge.Core.Models.DiagnosticMcp;
using RetroDownfall.TheForge.Core.Models.Traces;
using RetroDownfall.TheForge.Core.Models.Trials;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// Guards against DI registration traps that crash The Forge before MainWindow appears.
/// </summary>
[Collection(TheForgeProcessEnvironmentCollection.Name)]
public sealed class ServiceCollectionConfiguratorTests
{

    [Fact]
    public void Production_composition_builds_without_creating_an_absent_managed_root()
    {

        using TheForgeTestHomeScope home = new("forge-absent-root");

        string guardedRoot = ArcanumPaths.GrimoireDirectory;

        Assert.False(Directory.Exists(guardedRoot));

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        Assert.NotNull(services.GetRequiredService<ITheForgeSettingsStore>());

        Assert.False(Directory.Exists(guardedRoot));

    }

    [Fact]
    public void Production_managed_root_stores_share_one_mutation_runner()
    {

        using TheForgeTestHomeScope home = new("forge-runner-singleton");

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        ITheForgeLocalMutationRunner runner =
            services.GetRequiredService<ITheForgeLocalMutationRunner>();

        Assert.Same(
            runner,
            Assert.IsType<TheForgeSettingsStore>(
                services.GetRequiredService<ITheForgeSettingsStore>()).MutationRunner);

        Assert.Same(
            runner,
            Assert.IsType<TrialSuiteStore>(
                services.GetRequiredService<ITrialSuiteStore>()).MutationRunner);

        Assert.Same(
            runner,
            Assert.IsType<ComparisonRunStore>(
                services.GetRequiredService<IComparisonRunStore>()).MutationRunner);

        Assert.Same(
            runner,
            Assert.IsType<InferenceTraceStore>(
                services.GetRequiredService<IInferenceTraceStore>()).MutationRunner);

        Assert.Same(
            runner,
            Assert.IsType<DiagnosticMcpFixtureStore>(
                services.GetRequiredService<IDiagnosticMcpFixtureStore>()).MutationRunner);

    }

    [Theory]
    [InlineData("settings")]
    [InlineData("trial-suites")]
    [InlineData("comparisons")]
    [InlineData("inference-traces")]
    [InlineData("diagnostic-fixtures")]
    public async Task Production_local_store_refuses_a_contended_client_mutation_without_writing(
        string storeKind)
    {

        using TheForgeTestHomeScope home = new("forge-client-mutation");

        using FileStream held = TheForgeTestHomeScope.HoldClientMutationLock();

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        (string path, Func<Task> save) = storeKind switch
        {
            "settings" => (
                Path.Combine(ArcanumPaths.GrimoireDirectory, TheForgeSettingsStore.FileName),
                (Func<Task>)(() => services.GetRequiredService<ITheForgeSettingsStore>()
                    .SaveAsync(new TheForgeSettings()))),
            "trial-suites" => (
                Path.Combine(ArcanumPaths.GrimoireDirectory, "the-forge-trial-suites.json"),
                () => services.GetRequiredService<ITrialSuiteStore>()
                    .SaveAsync(new TrialSuiteStoreDocument(1, now, now, []))),
            "comparisons" => (
                Path.Combine(ArcanumPaths.GrimoireDirectory, "the-forge-comparisons.json"),
                () => services.GetRequiredService<IComparisonRunStore>()
                    .SaveAsync(new ComparisonStoreDocument(1, now, now, []))),
            "inference-traces" => (
                Path.Combine(ArcanumPaths.GrimoireDirectory, "the-forge-inference-traces.json"),
                () => services.GetRequiredService<IInferenceTraceStore>()
                    .SaveAsync(new InferenceTraceStoreDocument(1, now, now, []))),
            "diagnostic-fixtures" => (
                Path.Combine(ArcanumPaths.GrimoireDirectory, "the-forge-diagnostic-mcp-fixtures.json"),
                () => services.GetRequiredService<IDiagnosticMcpFixtureStore>()
                    .SaveAsync(new DiagnosticMcpFixtureStoreDocument(1, now, now, []))),
            _ => throw new ArgumentOutOfRangeException(nameof(storeKind)),
        };

        TheForgeLocalMutationRefusedException error =
            await Assert.ThrowsAsync<TheForgeLocalMutationRefusedException>(save);

        Assert.Contains(ErrorCodes.Data.FileLocked, error.Message, StringComparison.Ordinal);

        Assert.False(File.Exists(path));

        if (Directory.Exists(ArcanumPaths.GrimoireDirectory))
        {

            Assert.Empty(Directory.EnumerateFiles(
                ArcanumPaths.GrimoireDirectory,
                Path.GetFileName(path) + ".*.tmp"));

        }

    }

    [Fact]
    public async Task Production_settings_store_surfaces_unsafe_client_lock_topology_without_writing()
    {

        using TheForgeTestHomeScope home = new("forge-client-mutation-unsafe");

        string lockPath = TheForgeTestHomeScope.ClientMutationLockPath();

        Directory.CreateDirectory(lockPath);

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        ITheForgeSettingsStore store =
            services.GetRequiredService<ITheForgeSettingsStore>();

        TheForgeLocalMutationRefusedException error =
            await Assert.ThrowsAsync<TheForgeLocalMutationRefusedException>(
                () => store.SaveAsync(new TheForgeSettings()));

        Assert.Contains(
            ErrorCodes.Data.ControlPathUnavailable,
            error.Message,
            StringComparison.Ordinal);

        Assert.False(File.Exists(store.SettingsPath));

    }

    [Fact]
    public void Build_ResolvesApiKeyResolver_WithoutCircularDependency()
    {

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        IOsCredentialStore store = services.GetRequiredService<IOsCredentialStore>();

        ApiKeyResolver resolver = services.GetRequiredService<ApiKeyResolver>();

        Assert.NotNull(store);

        Assert.NotNull(resolver);

    }

}
