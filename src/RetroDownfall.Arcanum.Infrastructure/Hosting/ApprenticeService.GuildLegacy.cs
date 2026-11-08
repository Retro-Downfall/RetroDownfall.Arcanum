using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ApprenticeService
{
    private async Task<Result> RequireLegacyAdmissionAsync(IApprenticeRepository repository, Guid apprenticeId, CancellationToken ct)
    {
        if (optionsMonitor.CurrentValue.Features.Guild) return GuildLegacyUnavailable;

        Result<bool> bound = await repository.IsGuildBoundAsync(apprenticeId, ct).ConfigureAwait(false);

        if (bound.IsFailure) return bound.Error;

        return bound.Value || optionsMonitor.CurrentValue.Features.Guild ? GuildLegacyUnavailable : Result.Success();
    }

    private static Error GuildLegacyUnavailable => new(ErrorCodes.Guild.LegacyRunUnavailable,
        "This Apprentice requires Guild Quest admission; legacy execution and mutation are unavailable.");
}
