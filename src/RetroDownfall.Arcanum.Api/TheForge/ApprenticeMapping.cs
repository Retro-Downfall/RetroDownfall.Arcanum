using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

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

        return new ApprenticeDetailDto(
            apprentice.Id,
            apprentice.CampaignId,
            apprentice.Name,
            apprentice.Goal,
            plan,
            apprentice.CurrentStep,
            apprentice.Status,
            apprentice.SessionId,
            apprentice.WorkspacePath,
            ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData),
            apprentice.ErrorMessage,
            apprentice.CreatedAt,
            apprentice.UpdatedAt);
    }

}
