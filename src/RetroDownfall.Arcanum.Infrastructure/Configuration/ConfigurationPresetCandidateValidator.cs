using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Configuration;

internal sealed class ConfigurationPresetCandidateValidator(
    ConfigurationValidator validator) : IConfigurationPresetCandidateValidator
{

    public async Task<Result> ValidateAsync(
        ArcanumSettings candidate,
        CancellationToken cancellationToken = default)
    {

        Result outbound = await OutboundUrlGuard
            .ValidateArcanumSettingsAsync(candidate, cancellationToken)
            .ConfigureAwait(false);

        return outbound.IsFailure ? outbound : validator.Validate(candidate);

    }

}
