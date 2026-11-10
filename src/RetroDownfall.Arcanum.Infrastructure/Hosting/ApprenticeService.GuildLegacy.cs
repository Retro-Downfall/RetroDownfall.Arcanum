using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ApprenticeService
{
    private async Task<Result> RequireLegacyAdmissionAsync(IApprenticeRepository repository, Guid apprenticeId, CancellationToken ct)
    {
        if (optionsMonitor.CurrentValue.Features.Guild) return GuildLegacyUnavailable;

        Result<bool> bound = await repository.IsGuildBoundAsync(apprenticeId, ct).ConfigureAwait(false);

        if (bound.IsFailure) return bound.Error;

        if (bound.Value || optionsMonitor.CurrentValue.Features.Guild) return GuildLegacyUnavailable;

        Apprentice? apprentice = await repository.GetByIdAsync(apprenticeId, ct).ConfigureAwait(false);

        return apprentice is null ? Result.Success() : await RequireCurrentToolPlanAsync(repository, apprentice, ct).ConfigureAwait(false);
    }

    private async Task<Result> RequireCurrentToolPlanAsync(IApprenticeRepository repository, Apprentice apprentice, CancellationToken ct)
    {
        if (apprentice.Status is "Completed" or "Cancelled" or "Failed") return Result.Success();

        Error? legacy = ApprenticeLegacyToolPolicy.Diagnose(ApprenticeRepository.DeserializePlan(apprentice.Plan), apprentice.CurrentStep);

        if (legacy is null) return Result.Success();

        bool parked = await repository.TryParkLegacyToolPlanAsync(apprentice.Id, apprentice.Plan, apprentice.CurrentStep,
            apprentice.Status, legacy.Value.Message, ct).ConfigureAwait(false);

        if (parked)
        {
            Publish(apprentice.Id, new ApprenticeEvent
            {
                Type = ApprenticeEventType.ApprenticeEscalated,
                ApprenticeId = apprentice.Id,
                Timestamp = DateTimeOffset.UtcNow,
                Error = legacy.Value.Message,
            });
        }

        if (GuildEngine.Value is { } frame && frame.Binding.ApprenticeId == apprentice.Id) frame.Refuse(legacy.Value);

        return legacy.Value;
    }

    private static Error GuildLegacyUnavailable => new(ErrorCodes.Guild.LegacyRunUnavailable,
        "This Apprentice requires Guild Quest admission; legacy execution and mutation are unavailable.");
}
