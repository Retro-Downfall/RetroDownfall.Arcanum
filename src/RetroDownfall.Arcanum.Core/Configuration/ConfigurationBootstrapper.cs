using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Configuration;

public static class ConfigurationBootstrapper
{

    public static IConfigurationBuilder AddArcanumConfiguration(this IConfigurationBuilder builder)
    {

        string configPath = ArcanumPaths.GrimoireDirectory;

        Directory.CreateDirectory(configPath);

        string jsonPath = Path.Combine(configPath, "arcanum.json");

        ValidateArcanumConfigurationFile(jsonPath);

        builder.AddJsonFile(jsonPath, optional: true, reloadOnChange: true);

        builder.AddEnvironmentVariables(prefix: "ARCANUM_");

        return builder;

    }

    public static void ValidateArcanumConfigurationFile(string jsonPath)
    {

        if (!File.Exists(jsonPath))
        {

            return;

        }

        try
        {

            byte[] raw = File.ReadAllBytes(jsonPath);

            _ = JsonSerializer.Deserialize(raw, ConfigurationJsonContext.Default.ArcanumConfigurationFile)
                ?? throw new JsonException("Root value must be a JSON object.");

        }
        catch (JsonException ex)
        {

            throw new InvalidOperationException($"arcanum.json is invalid: {ex.Message} ({jsonPath})", ex);

        }

    }

}
