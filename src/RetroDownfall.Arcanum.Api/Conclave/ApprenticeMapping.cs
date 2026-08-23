using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.Conclave;

internal static class ApprenticeMapping
{

    public static ApprenticeSummaryDto ToSummaryDto(Apprentice apprentice)
    {
        List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        return new ApprenticeSummaryDto(
            apprentice.Id,
            apprentice.CampaignId,
            apprentice.Name,
            apprentice.Goal,
            apprentice.Status,
            apprentice.CurrentStep,
            plan.Count,
            apprentice.CreatedAt,
            apprentice.UpdatedAt);
    }

    public static ApprenticeDetailDto ToDetailDto(Apprentice apprentice)
    {
        List<PlanStep> plan = ApprenticeRepository.DeserializePlan(apprentice.Plan);

        ApprenticeCheckpoint? checkpoint = ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData);

        return new ApprenticeDetailDto(
            apprentice.Id,
            apprentice.CampaignId,
            apprentice.ParentApprenticeId ?? checkpoint?.ParentApprenticeId,
            apprentice.Name,
            apprentice.Goal,
            plan,
            apprentice.CurrentStep,
            apprentice.Status,
            apprentice.SessionId,
            apprentice.WorkspacePath,
            checkpoint,
            apprentice.ErrorMessage,
            apprentice.CreatedAt,
            apprentice.UpdatedAt);
    }

}
