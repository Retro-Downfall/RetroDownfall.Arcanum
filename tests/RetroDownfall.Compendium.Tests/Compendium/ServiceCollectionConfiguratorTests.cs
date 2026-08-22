using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Compendium.Ux;

using RetroDownfall.Compendium.Ux.Services;

using RetroDownfall.Compendium.Ux.Tests;

using Xunit;

namespace RetroDownfall.Compendium.Tests.Compendium;

[Collection("EnvVarSensitive")]

public sealed class ServiceCollectionConfiguratorTests
{

    [Fact]
    public void Production_composition_builds_without_creating_an_absent_managed_root()
    {

        using ArcanumTestHomeScope home = new("compendium-absent-root");

        string guardedRoot = ArcanumPaths.GrimoireDirectory;

        Assert.False(Directory.Exists(guardedRoot));

        using ServiceProvider provider = ServiceCollectionConfigurator.Build();

        Assert.NotNull(provider.GetRequiredService<IArcanumConfigurationStore>());

        Assert.NotNull(provider.GetRequiredService<LocalCertificateGenerator>());

        Assert.False(Directory.Exists(guardedRoot));

    }

    [Theory]
    [InlineData(false, ErrorCodes.Data.FileLocked)]
    [InlineData(true, ErrorCodes.Data.ControlPathUnavailable)]
    public async Task Production_configuration_save_surfaces_client_mutation_refusal_without_writing(
        bool unsafeLockTopology,
        string expectedCode)
    {

        using ArcanumTestHomeScope home = new("compendium-client-mutation");

        string guardedRoot = ArcanumPaths.GrimoireDirectory;

        string lockPath = ClientMutationLockPathFor(guardedRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        FileStream? held = null;

        if (unsafeLockTopology)
        {

            Directory.CreateDirectory(lockPath);

        }
        else
        {

            held = HoldClientMutationLock(lockPath);

        }

        try
        {

            using ServiceProvider provider = ServiceCollectionConfigurator.Build();

            IArcanumConfigurationStore store =
                provider.GetRequiredService<IArcanumConfigurationStore>();

            ConfigurationWriteResult result = await store.WriteAsync(
                new RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings());

            Assert.False(result.IsSuccess);

            Assert.Contains(expectedCode, result.ErrorMessage, StringComparison.Ordinal);

            Assert.False(File.Exists(store.ConfigurationFilePath));

            if (Directory.Exists(guardedRoot))
            {

                Assert.Empty(Directory.EnumerateFiles(
                    guardedRoot,
                    ".arcanum.*.tmp"));

            }

        }
        finally
        {

            held?.Dispose();

        }

    }

    [Fact]

    public void Production_composition_uses_the_shared_configuration_preset_service()
    {

        string testHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            Guid.NewGuid().ToString("N"));

        string? originalDotnetEnvironment =
            global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        string? originalAspNetCoreEnvironment =
            global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        string? originalTestHome =
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        try
        {

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

            global::System.Environment.SetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT",
                "Testing");

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", testHome);

            using ServiceProvider provider = ServiceCollectionConfigurator.Build();

            IConfigurationPresetService service =
                provider.GetRequiredService<IConfigurationPresetService>();

            Assert.Equal(6, service.List().Count);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(
                "DOTNET_ENVIRONMENT",
                originalDotnetEnvironment);

            global::System.Environment.SetEnvironmentVariable(
                "ASPNETCORE_ENVIRONMENT",
                originalAspNetCoreEnvironment);

            global::System.Environment.SetEnvironmentVariable(
                "ARCANUM_TEST_HOME",
                originalTestHome);

            if (Directory.Exists(testHome))
            {

                Directory.Delete(testHome, recursive: true);

            }

        }

    }

    [Fact]
    public async Task Production_resolved_preset_service_refuses_client_mutation_contention_without_writing()
    {

        using ArcanumTestHomeScope home = new("compendium-preset-client-mutation");

        string guardedRoot = ArcanumPaths.GrimoireDirectory;

        string lockPath = ClientMutationLockPathFor(guardedRoot);

        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        using FileStream held = HoldClientMutationLock(lockPath);

        using ServiceProvider provider = ServiceCollectionConfigurator.Build();

        IConfigurationPresetService service =
            provider.GetRequiredService<IConfigurationPresetService>();

        Result<ConfigurationPresetApplyResult> result = await service
            .ApplyAsync("general-assistant");

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.FileLocked, result.Error.Code);

        Assert.False(File.Exists(ArcanumPaths.ConfigurationFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetJournalFile));

        Assert.False(File.Exists(ArcanumPaths.ConfigurationPresetRollbackFile));

    }

    /// <summary>
    /// Registering <see cref="IFamiliarProbeClient"/> without the secret store it depends on reads as
    /// wired while every resolution throws, so the Re-probe button on a Familiar row would never get a
    /// probe client. The composition has to actually build one.
    /// </summary>
    [Fact]

    public void Production_composition_resolves_the_familiar_probe_client()
    {

        using ArcanumTestHomeScope home = new("compendium-probe-composition");

        using ServiceProvider provider = ServiceCollectionConfigurator.Build();

        Assert.NotNull(provider.GetRequiredService<IFamiliarProbeClient>());

    }

    private static string ClientMutationLockPathFor(string guardedRoot)
    {

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(guardedRoot));

        string parent = Path.GetDirectoryName(full)!;

        return Path.Combine(
            parent,
            $".arcanum-client-mutation-{Path.GetFileName(full)}.lock");

    }

    private static FileStream HoldClientMutationLock(string lockPath)
    {

        FileStream held = new(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                lockPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

        }

        return held;

    }

}
