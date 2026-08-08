using System.Text.Json;

namespace RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

/// <summary>
/// Serializes the command map. Output is source-generated, indentation-stable, and contains no
/// host, user, or version-specific value, so regenerating it on any machine produces identical
/// bytes and the committed copy can be diffed as a contract.
/// </summary>
internal static class CliSurfaceWriter
{

    public static string ToJson(CliSurfaceMap map)
    {

        ArgumentNullException.ThrowIfNull(map);

        return JsonSerializer.Serialize(map, CliSurfaceJsonContext.Default.CliSurfaceMap) + "\n";

    }

}
