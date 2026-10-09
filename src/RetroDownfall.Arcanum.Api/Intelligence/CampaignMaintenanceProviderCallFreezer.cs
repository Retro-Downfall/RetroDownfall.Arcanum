using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Freezes the closed text and fixed options of one Campaign maintenance attempt.</summary>
public static class CampaignMaintenanceProviderCallFreezer
{
    private static readonly byte[] CanonicalSchema =
        ArcanumCanonicalJsonV1.Canonicalize(CampaignMaintenancePayload.SchemaUtf8.AsSpan());

    private static readonly CovenantDigest SchemaDigest = new(SHA256.HashData(CanonicalSchema));

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Result<ProviderCallEnvelope> TryFreeze(
        CampaignMaintenancePayload payload,
        string providerIdentity,
        string modelIdentity,
        ContextTokenBreakdown breakdown,
        ulong contextWindowIdentity,
        ProviderCallSensitivity sensitivity)
    {
        ArgumentNullException.ThrowIfNull(payload);

        ArgumentNullException.ThrowIfNull(breakdown);

        ArgumentNullException.ThrowIfNull(sensitivity);

        try
        {
            FrozenProviderOptions options = FrozenProviderOptions.Create(new(
                CampaignMaintenancePayload.MaxOutputTokens,
                null, null, null, null, null, null, [],
                ProviderToolChoice.None, null, CovenantTriStateBoolean.Absent,
                ProviderResponseFormat.JsonSchema,
                CampaignMaintenancePayload.SchemaName,
                CampaignMaintenancePayload.SchemaDescription,
                SchemaDigest, CovenantTriStateBoolean.Absent,
                null, null, null, CovenantReasoningWireDialect.Standard, default),
                CanonicalSchema);

            return Result<ProviderCallEnvelope>.Success(new(
                providerIdentity, modelIdentity, CovenantProviderDispatchMode.Buffered,
                breakdown.Profile.ProfileId, contextWindowIdentity, 0,
                sensitivity, options, StrictUtf8.GetBytes(payload.SystemPrompt), [], new(false, []),
                [
                    new(CovenantProviderRole.System, null, null, [ProviderContentPartEnvelope.Text(payload.SystemPrompt)]),
                    new(CovenantProviderRole.User, null, null, [ProviderContentPartEnvelope.Text(payload.UserPrompt)]),
                ],
                [], SchemaDigest, CanonicalSchema));
        }
        catch (ArgumentException)
        {
            return new Error(ErrorCodes.Covenant.InvalidContent, "The closed Campaign maintenance payload could not be frozen.");
        }
    }
}
