using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

internal interface IConfigurationCommandService
{

    string ConfigurationPath { get; }

    Task<Result<ConfigurationCommandSnapshot>> ReadAsync(
        CancellationToken cancellationToken);

    Task<Result> ValidateAsync(
        ConfigurationCommandSnapshot snapshot,
        ArcanumSettings settings,
        CancellationToken cancellationToken);

    Task<Result> WriteAsync(
        ConfigurationCommandSnapshot snapshot,
        ArcanumSettings settings,
        CancellationToken cancellationToken);

}

internal interface IConfigurationCommandExclusiveWriter
{

    Task<Result> WriteUnderExclusiveAsync(
        ConfigurationCommandSnapshot snapshot,
        ArcanumSettings settings,
        CancellationToken cancellationToken);

}

internal sealed record ConfigurationCommandSnapshot(
    ArcanumSettings Settings,
    ConfigurationAccessMode AccessMode,
    IReadOnlyList<string> EnvironmentOverrides)
{

    public ArcanumSettings EffectiveSettings(ArcanumSettings? candidate = null) =>
        AccessMode == ConfigurationAccessMode.HostApi
            ? candidate ?? Settings
            : ConfigurationEnvironmentOverrides.Apply(candidate ?? Settings);

}

internal enum ConfigurationAccessMode
{

    HostApi,

    LocalBootstrap,

}

internal sealed class ConfigurationCommandService(
    ArcanumApiClient apiClient,
    ConfigurationValidator validator,
    ConfigurationWriter writer,
    IGrimoireCliInitialization initialization) :
    IConfigurationCommandService,
    IConfigurationCommandExclusiveWriter
{

    public string ConfigurationPath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

    public async Task<Result<ConfigurationCommandSnapshot>> ReadAsync(
        CancellationToken cancellationToken)
    {

        Result<ArcanumSettings> remote = await apiClient
            .GetConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);

        if (remote.IsSuccess)
        {

            return Result<ConfigurationCommandSnapshot>.Success(
                new ConfigurationCommandSnapshot(
                    remote.Value,
                    ConfigurationAccessMode.HostApi,
                    ConfigurationEnvironmentOverrides.Inspect(remote.Value)));

        }

        if (!CanUseLocalBootstrap(remote.Error))
        {

            return Result<ConfigurationCommandSnapshot>.Failure(remote.Error);

        }

        try
        {

            ArcanumSettings local = ConfigurationBootstrapper
                .LoadPersistedArcanumSettings();

            return Result<ConfigurationCommandSnapshot>.Success(
                new ConfigurationCommandSnapshot(
                    local,
                    ConfigurationAccessMode.LocalBootstrap,
                    ConfigurationEnvironmentOverrides.Inspect(local)));

        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {

            return Result<ConfigurationCommandSnapshot>.Failure(
                new Error("Configuration.ReadFailed", exception.Message));

        }

    }

    public async Task<Result> ValidateAsync(
        ConfigurationCommandSnapshot snapshot,
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {

        if (snapshot.AccessMode == ConfigurationAccessMode.HostApi)
        {

            Result<bool> remote = await apiClient
                .ValidateConfigurationAsync(settings, cancellationToken)
                .ConfigureAwait(false);

            return remote.IsSuccess
                ? Result.Success()
                : Result.Failure(remote.Error);

        }

        Result outbound = await OutboundUrlGuard
            .ValidateArcanumSettingsAsync(settings, cancellationToken)
            .ConfigureAwait(false);

        return outbound.IsFailure ? outbound : validator.Validate(settings);

    }

    public Task<Result> WriteAsync(
        ConfigurationCommandSnapshot snapshot,
        ArcanumSettings settings,
        CancellationToken cancellationToken) =>
        snapshot.AccessMode == ConfigurationAccessMode.HostApi
            ? WriteCoreAsync(snapshot, settings, cancellationToken)
            : initialization.RunExclusiveAsync(
                (_, token) => WriteCoreAsync(snapshot, settings, token),
                cancellationToken);

    public Task<Result> WriteUnderExclusiveAsync(
        ConfigurationCommandSnapshot snapshot,
        ArcanumSettings settings,
        CancellationToken cancellationToken) =>
        WriteCoreAsync(snapshot, settings, cancellationToken);

    private async Task<Result> WriteCoreAsync(
        ConfigurationCommandSnapshot snapshot,
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {

        Result validation = await ValidateAsync(
                snapshot,
                settings,
                cancellationToken)
            .ConfigureAwait(false);

        if (validation.IsFailure)
        {

            return validation;

        }

        if (snapshot.AccessMode == ConfigurationAccessMode.HostApi)
        {

            Result<bool> remote = await apiClient
                .UpdateConfigurationAsync(settings, cancellationToken)
                .ConfigureAwait(false);

            return remote.IsSuccess
                ? Result.Success()
                : Result.Failure(remote.Error);

        }

        string expectedHash = ConfigurationPresetHash.ComputeSettings(snapshot.Settings);

        Result<ArcanumSettings> local = await writer.UpdateAsync(
                snapshot.Settings,
                current => string.Equals(
                    ConfigurationPresetHash.ComputeSettings(current),
                    expectedHash,
                    StringComparison.Ordinal)
                    ? Result<ArcanumSettings>.Success(settings)
                    : Result<ArcanumSettings>.Failure(
                        new Error(
                            "Configuration.Changed",
                            "arcanum.json changed after it was loaded. Reload the configuration and retry the edit.")),
                cancellationToken)
            .ConfigureAwait(false);

        return local.IsSuccess ? Result.Success() : Result.Failure(local.Error);

    }

    private static bool CanUseLocalBootstrap(Error error) =>
        string.Equals(error.Code, ErrorCodes.Connection.Unreachable, StringComparison.Ordinal)
        || string.Equals(error.Code, ErrorCodes.Connection.Timeout, StringComparison.Ordinal)
        || string.Equals(error.Code, ErrorCodes.Security.MissingApiKey, StringComparison.Ordinal);

}

internal static class ConfigurationEnvironmentOverrides
{

    public static IReadOnlyList<string> Inspect(ArcanumSettings settings)
    {

        ConfigurationEnvironmentSnapshot snapshot =
            ConfigurationEnvironmentResolver.Resolve(settings);

        return snapshot.Overrides
            .Where(item => ConfigurationPathAccessor.Exists(settings, item.Path))
            .Select(static item => $"{item.Path} <- {item.VariableName}")
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    }

    public static ArcanumSettings Apply(ArcanumSettings settings) =>
        ConfigurationEnvironmentResolver.Resolve(settings).EffectiveSettings;

}
