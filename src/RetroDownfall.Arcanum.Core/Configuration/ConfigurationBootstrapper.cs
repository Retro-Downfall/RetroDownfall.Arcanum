using Microsoft.Extensions.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Configuration;

public static class ConfigurationBootstrapper
{
    public static IConfigurationBuilder AddArcanumConfiguration(this IConfigurationBuilder builder)
    {
        string configPath = ArcanumPaths.GrimoireDirectory;

        Directory.CreateDirectory(configPath);

        string jsonPath = Path.Combine(configPath, "arcanum.json");

        builder.AddJsonFile(jsonPath, optional: true, reloadOnChange: true);

        builder.AddEnvironmentVariables(prefix: "ARCANUM_");

        return builder;
    }
}
