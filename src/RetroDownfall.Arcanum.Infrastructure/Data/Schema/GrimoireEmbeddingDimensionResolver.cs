using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The one place the install-time embedding width is read from configuration.
/// </summary>
/// <remarks>
/// Two callers need it - the bootstrap that installs at startup, and the background pass that
/// converges a tier after a sweep drains - and a value read two ways is a value that eventually
/// disagrees with itself. The width reaches templated schema objects, so a second reading that
/// clamped differently would install a different column shape than the first.
///
/// <para>Resolution failure is a warning and the documented default rather than a throw. A narrow
/// container that installs the schema without composing settings is a legitimate caller, and taking
/// startup down for it would be worse than installing at the width the settings type itself
/// declares.</para>
/// </remarks>
internal static class GrimoireEmbeddingDimensionResolver
{

    internal static int Resolve(IServiceProvider services)
    {

        ArgumentNullException.ThrowIfNull(services);

        try
        {

            IOptionsMonitor<ArcanumSettings> optionsMonitor =
                services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

            return ArcanumSettingClamps.EmbeddingsDimensions(
                optionsMonitor.CurrentValue.ResolveEmbeddings().Dimensions);

        }
        catch (Exception ex)
        {

            Log.Warning(
                ex,
                "Embedding settings could not be resolved for schema installation; installing with the default dimension.");

            return new EmbeddingSettings().Dimensions;

        }

    }

}
