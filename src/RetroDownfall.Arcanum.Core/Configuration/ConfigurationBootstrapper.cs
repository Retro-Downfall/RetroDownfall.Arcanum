using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Configuration;

public static class ConfigurationBootstrapper
{

    public const int MaxConfigurationBytes = 10 * 1024 * 1024;

    public static IConfigurationBuilder AddArcanumConfiguration(this IConfigurationBuilder builder)
    {

        string configPath = ArcanumPaths.GrimoireDirectory;

        Directory.CreateDirectory(configPath);

        string jsonPath = Path.Combine(configPath, "arcanum.json");

        ValidateArcanumConfigurationFile(jsonPath);

        builder.AddJsonFile(jsonPath, optional: true, reloadOnChange: false);

        return builder;

    }

    public static void ValidateArcanumConfigurationFile(string jsonPath)
    {
        _ = LoadArcanumSettingsFile(jsonPath);
    }

    public static ArcanumSettings LoadArcanumSettings(
        Func<ArcanumSettings>? fallbackFactory = null) =>
        LoadArcanumSettingsFile(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json"),
            fallbackFactory);

    public static void CopySettings(
        ArcanumSettings source,
        ArcanumSettings destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        destination.Edition = source.Edition;
        destination.Host = source.Host;
        destination.Providers = source.Providers;
        destination.DefaultModel = source.DefaultModel;
        destination.FastModel = source.FastModel;
        destination.Security = source.Security;
        destination.Workspaces = source.Workspaces;
        destination.Features = source.Features;
        destination.Integrations = source.Integrations;
        destination.Execution = source.Execution;
        destination.Cost = source.Cost;
        destination.Daemon = source.Daemon;
        destination.Retention = source.Retention;
        destination.Cli = source.Cli;
    }

    internal static ArcanumSettings LoadArcanumSettingsFile(
        string jsonPath,
        Func<ArcanumSettings>? fallbackFactory = null)
    {

        if (!File.Exists(jsonPath))
        {
            return fallbackFactory?.Invoke() ?? new ArcanumSettings();

        }

        try
        {

            using FileStream stream = new(
                jsonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            if (stream.Length > MaxConfigurationBytes)
            {

                throw new InvalidOperationException(
                    $"arcanum.json is invalid: configuration exceeds the {MaxConfigurationBytes}-byte limit ({jsonPath})");

            }

            byte[] raw = new byte[stream.Length];

            stream.ReadExactly(raw);

            using JsonDocument document = JsonDocument.Parse(raw);
            Result treeValidation = new ConfigurationValidator()
                .ValidateConfigurationFileJson(document.RootElement);

            if (treeValidation.IsFailure)
            {
                string details = treeValidation.Error.Details is { Count: > 0 } validationDetails
                    ? string.Join(
                        "; ",
                        validationDetails.Select(static detail =>
                            $"{detail.Pointer}: {detail.Detail}"))
                    : treeValidation.Error.Message;

                throw new JsonException(details);
            }

            ArcanumConfigurationFile configurationFile =
                JsonSerializer.Deserialize(
                    raw,
                    ConfigurationJsonContext.Default.ArcanumConfigurationFile)
                ?? throw new JsonException("Root value must be a JSON object.");

            return configurationFile.Arcanum;
        }
        catch (JsonException ex)
        {

            throw new InvalidOperationException($"arcanum.json is invalid: {ex.Message} ({jsonPath})", ex);

        }

    }

}
