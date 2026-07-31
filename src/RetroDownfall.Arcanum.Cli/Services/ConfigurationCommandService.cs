using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

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
    ConfigurationWriter writer) : IConfigurationCommandService
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

            ArcanumSettings local = ConfigurationBootstrapper.LoadArcanumSettings();

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

    public async Task<Result> WriteAsync(
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

        return await writer.WriteAsync(settings, cancellationToken).ConfigureAwait(false);

    }

    private static bool CanUseLocalBootstrap(Error error) =>
        string.Equals(error.Code, ErrorCodes.Connection.Unreachable, StringComparison.Ordinal)
        || string.Equals(error.Code, ErrorCodes.Connection.Timeout, StringComparison.Ordinal)
        || string.Equals(error.Code, ErrorCodes.Security.MissingApiKey, StringComparison.Ordinal);

}

internal static class ConfigurationEnvironmentOverrides
{

    private const string GeneralPrefix = "ARCANUM_Arcanum__";

    public static IReadOnlyList<string> Inspect(ArcanumSettings settings)
    {

        List<string> overrides = [];

        AddSpecial(overrides, "edition", "ARCANUM_EDITION");

        AddSpecial(overrides, "host.listenAny", "ARCANUM_HOST_ANY");

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {

            string? name = entry.Key as string;

            if (name is null
                || !name.StartsWith(GeneralPrefix, StringComparison.OrdinalIgnoreCase))
            {

                continue;

            }

            string path = NormalizePath(
                name[GeneralPrefix.Length..].Replace("__", ".", StringComparison.Ordinal));

            if (ConfigurationPathAccessor.Exists(settings, path))
            {

                overrides.Add($"{path} <- {name}");

            }

        }

        overrides.Sort(StringComparer.OrdinalIgnoreCase);

        return overrides;

    }

    public static ArcanumSettings Apply(ArcanumSettings settings)
    {

        ArcanumSettings effective = settings;

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {

            string? name = entry.Key as string;

            string? value = entry.Value as string;

            if (name is null
                || value is null
                || !name.StartsWith(GeneralPrefix, StringComparison.OrdinalIgnoreCase))
            {

                continue;

            }

            string path = name[GeneralPrefix.Length..].Replace("__", ".", StringComparison.Ordinal);

            ConfigurationPathUpdate update = ConfigurationPathAccessor.Set(
                effective,
                path,
                value);

            if (update.IsSuccess)
            {

                effective = update.Settings!;

            }

        }

        string? edition = Environment.GetEnvironmentVariable("ARCANUM_EDITION");

        if (!string.IsNullOrWhiteSpace(edition))
        {

            ConfigurationPathUpdate update = ConfigurationPathAccessor.Set(
                effective,
                "edition",
                edition);

            if (update.IsSuccess)
            {

                effective = update.Settings!;

            }

        }

        string? hostAny = Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        if (!string.IsNullOrWhiteSpace(hostAny))
        {

            ConfigurationPathUpdate update = ConfigurationPathAccessor.Set(
                effective,
                "host.listenAny",
                hostAny);

            if (update.IsSuccess)
            {

                effective = update.Settings!;

            }

        }

        return effective;

    }

    private static void AddSpecial(List<string> overrides, string path, string variable)
    {

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {

            overrides.Add($"{path} <- {variable}");

        }

    }

    private static string NormalizePath(string path) =>
        string.Join(
            '.',
            path.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(static segment =>
                char.ToLowerInvariant(segment[0]) + segment[1..]));

}
