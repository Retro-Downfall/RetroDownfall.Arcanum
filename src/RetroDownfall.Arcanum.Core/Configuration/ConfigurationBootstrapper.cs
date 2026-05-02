using Microsoft.Extensions.Configuration;

namespace RetroDownfall.Arcanum.Core.Configuration;

public static class ConfigurationBootstrapper
{
    public static IConfigurationBuilder AddArcanumConfiguration(this IConfigurationBuilder builder)
    {
        string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "arcanum");

        Directory.CreateDirectory(configPath);

        string jsonPath = Path.Combine(configPath, "arcanum.json");

        builder.AddJsonFile(jsonPath, optional: true, reloadOnChange: true);

        builder.AddEnvironmentVariables(prefix: "ARCANUM_");

        return builder;
    }
}
