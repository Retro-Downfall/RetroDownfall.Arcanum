using System.Globalization;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class CampaignMaintenancePayloadFingerprint
{
    public static string Compute(CampaignMaintenancePayload payload) =>
        ModelCallPayloadFingerprint.ComputeText(
            "campaign-maintenance-v1|"
            + payload.SystemPrompt.Length.ToString(CultureInfo.InvariantCulture) + ":" + payload.SystemPrompt
            + payload.UserPrompt.Length.ToString(CultureInfo.InvariantCulture) + ":" + payload.UserPrompt
            + CampaignMaintenancePayload.SchemaJson
            + CampaignMaintenancePayload.SchemaName
            + CampaignMaintenancePayload.SchemaDescription
            + CampaignMaintenancePayload.MaxOutputTokens.ToString(CultureInfo.InvariantCulture));
}
