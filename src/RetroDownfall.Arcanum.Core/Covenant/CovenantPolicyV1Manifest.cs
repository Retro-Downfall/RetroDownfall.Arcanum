using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.Covenant;

public enum CovenantDomainTag : byte
{
    Authored = 1,
    Fragment = 2,
    Section = 3,
    Request = 4,
    PreflightBody = 5,
    Authorization = 6,
    Mutation = 7,
    Snapshot = 8,
    Plan = 9,
    Materialization = 10,
    Sensitivity = 11,
    ArtifactLabel = 12,
    SessionTurnRequest = 13,
    SessionTurnExecution = 14,
    ProviderOptions = 15,
    ProviderCall = 16,
    Admission = 17,
    AttemptChain = 18,
    BranchChain = 19,
    WardEvidence = 20,
    ProviderDispatchEffect = 21,
    MaintenanceDispatchEffect = 22,
    ToolEgressEffect = 23,
    ManagedFileEffect = 24,
    BackupDisclosureEffect = 25,
    ExternalDisclosure = 26,
    DisclosureChain = 27,
    ExternalDisclosureState = 28,
    CampaignPathApplyRequest = 29,
    SessionCampaignBindingApplyRequest = 30,
    FamilyReinitializeApplyRequest = 31,
    Receipt = 32,
    TurnAggregate = 33,
    CursorFilter = 34,
    DependentHeadVector = 35,
    CurationRequest = 36,
    CurationDependentHeads = 37,
    CurationEffect = 38
}

public static class CovenantPolicyV1Manifest
{
    private static readonly IReadOnlyList<CovenantDomainTag> PolicyDomainTags = Array.AsReadOnly<CovenantDomainTag>(
    [
        CovenantDomainTag.Authored,
        CovenantDomainTag.Fragment,
        CovenantDomainTag.Section,
        CovenantDomainTag.Request,
        CovenantDomainTag.PreflightBody,
        CovenantDomainTag.Authorization,
        CovenantDomainTag.Mutation,
        CovenantDomainTag.Snapshot,
        CovenantDomainTag.Plan,
        CovenantDomainTag.Materialization,
        CovenantDomainTag.Sensitivity,
        CovenantDomainTag.ArtifactLabel,
        CovenantDomainTag.SessionTurnRequest,
        CovenantDomainTag.SessionTurnExecution,
        CovenantDomainTag.ProviderOptions,
        CovenantDomainTag.ProviderCall,
        CovenantDomainTag.Admission,
        CovenantDomainTag.AttemptChain,
        CovenantDomainTag.BranchChain,
        CovenantDomainTag.WardEvidence,
        CovenantDomainTag.ProviderDispatchEffect,
        CovenantDomainTag.MaintenanceDispatchEffect,
        CovenantDomainTag.ToolEgressEffect,
        CovenantDomainTag.ManagedFileEffect,
        CovenantDomainTag.BackupDisclosureEffect,
        CovenantDomainTag.ExternalDisclosure,
        CovenantDomainTag.DisclosureChain,
        CovenantDomainTag.ExternalDisclosureState,
        CovenantDomainTag.CampaignPathApplyRequest,
        CovenantDomainTag.SessionCampaignBindingApplyRequest,
        CovenantDomainTag.FamilyReinitializeApplyRequest,
        CovenantDomainTag.Receipt,
        CovenantDomainTag.TurnAggregate,
        CovenantDomainTag.CursorFilter,
        CovenantDomainTag.DependentHeadVector,
        CovenantDomainTag.CurationRequest,
        CovenantDomainTag.CurationDependentHeads,
        CovenantDomainTag.CurationEffect
    ]);

    public static IReadOnlyList<CovenantDomainTag> DomainTags => PolicyDomainTags;

    public static string GetDomainTag(CovenantDomainTag domainTag) =>
        domainTag switch
        {
            CovenantDomainTag.Authored => "Arcanum.Covenant.Authored.v1",
            CovenantDomainTag.Fragment => "Arcanum.Covenant.Fragment.v1",
            CovenantDomainTag.Section => "Arcanum.Covenant.Section.v1",
            CovenantDomainTag.Request => "Arcanum.Covenant.Request.v1",
            CovenantDomainTag.PreflightBody => "Arcanum.Covenant.PreflightBody.v1",
            CovenantDomainTag.Authorization => "Arcanum.Covenant.Authorization.v1",
            CovenantDomainTag.Mutation => "Arcanum.Covenant.Mutation.v1",
            CovenantDomainTag.Snapshot => "Arcanum.Covenant.Snapshot.v1",
            CovenantDomainTag.Plan => "Arcanum.Covenant.Plan.v1",
            CovenantDomainTag.Materialization => "Arcanum.Covenant.Materialization.v1",
            CovenantDomainTag.Sensitivity => "Arcanum.Covenant.Sensitivity.v1",
            CovenantDomainTag.ArtifactLabel => "Arcanum.Covenant.ArtifactLabel.v1",
            CovenantDomainTag.SessionTurnRequest => "Arcanum.Covenant.SessionTurnRequest.v1",
            CovenantDomainTag.SessionTurnExecution => "Arcanum.Covenant.SessionTurnExecution.v1",
            CovenantDomainTag.ProviderOptions => "Arcanum.Covenant.ProviderOptions.v1",
            CovenantDomainTag.ProviderCall => "Arcanum.Covenant.ProviderCall.v1",
            CovenantDomainTag.Admission => "Arcanum.Covenant.Admission.v1",
            CovenantDomainTag.AttemptChain => "Arcanum.Covenant.AttemptChain.v1",
            CovenantDomainTag.BranchChain => "Arcanum.Covenant.BranchChain.v1",
            CovenantDomainTag.WardEvidence => "Arcanum.Covenant.WardEvidence.v1",
            CovenantDomainTag.ProviderDispatchEffect => "Arcanum.Covenant.ProviderDispatchEffect.v1",
            CovenantDomainTag.MaintenanceDispatchEffect => "Arcanum.Covenant.MaintenanceDispatchEffect.v1",
            CovenantDomainTag.ToolEgressEffect => "Arcanum.Covenant.ToolEgressEffect.v1",
            CovenantDomainTag.ManagedFileEffect => "Arcanum.Covenant.ManagedFileEffect.v1",
            CovenantDomainTag.BackupDisclosureEffect => "Arcanum.Covenant.BackupDisclosureEffect.v1",
            CovenantDomainTag.ExternalDisclosure => "Arcanum.Covenant.ExternalDisclosure.v1",
            CovenantDomainTag.DisclosureChain => "Arcanum.Covenant.DisclosureChain.v1",
            CovenantDomainTag.ExternalDisclosureState => "Arcanum.Covenant.ExternalDisclosureState.v1",
            CovenantDomainTag.CampaignPathApplyRequest => "Arcanum.Campaign.PathApplyRequest.v1",
            CovenantDomainTag.SessionCampaignBindingApplyRequest => "Arcanum.Session.CampaignBindingApplyRequest.v1",
            CovenantDomainTag.FamilyReinitializeApplyRequest => "Arcanum.Covenant.FamilyReinitializeApplyRequest.v1",
            CovenantDomainTag.Receipt => "Arcanum.Covenant.Receipt.v1",
            CovenantDomainTag.TurnAggregate => "Arcanum.Covenant.TurnAggregate.v1",
            CovenantDomainTag.CursorFilter => "Arcanum.Covenant.CursorFilter.v1",
            CovenantDomainTag.DependentHeadVector => "Arcanum.Covenant.DependentHeadVector.v1",
            CovenantDomainTag.CurationRequest => "Arcanum.Covenant.CurationRequest.v1",
            CovenantDomainTag.CurationDependentHeads => "Arcanum.Covenant.CurationDependentHeads.v1",
            CovenantDomainTag.CurationEffect => "Arcanum.Covenant.CurationEffect.v1",
            _ => throw new ArgumentOutOfRangeException(nameof(domainTag))
        };

    public static uint GetCode<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        byte code = GetSupportedByte(value);

        if (AllowsZeroThroughOne<TEnum>())
        {
            return RequireRange(code, 0, 1, nameof(value));
        }

        if (AllowsZeroThroughTwo<TEnum>())
        {
            return RequireRange(code, 0, 2, nameof(value));
        }

        if (AllowsZeroThroughThree<TEnum>())
        {
            return RequireRange(code, 0, 3, nameof(value));
        }

        return RequirePositiveCode<TEnum>(code, nameof(value));
    }

    private static byte GetSupportedByte<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        if (Unsafe.SizeOf<TEnum>() != sizeof(byte)
            || !IsSupported<TEnum>())
        {
            throw new ArgumentException("The enum type is not part of the Covenant policy-v1 manifest.", nameof(value));
        }

        return Unsafe.As<TEnum, byte>(ref value);
    }

    private static bool IsSupported<TEnum>()
        where TEnum : struct, Enum =>
        AllowsZeroThroughOne<TEnum>()
        || AllowsZeroThroughTwo<TEnum>()
        || AllowsZeroThroughThree<TEnum>()
        || IsPositivePolicyEnum<TEnum>();

    private static bool AllowsZeroThroughOne<TEnum>()
        where TEnum : struct, Enum =>
        typeof(TEnum) == typeof(ContentSensitivity);

    private static bool AllowsZeroThroughTwo<TEnum>()
        where TEnum : struct, Enum =>
        typeof(TEnum) == typeof(CovenantTriStateBoolean);

    private static bool AllowsZeroThroughThree<TEnum>()
        where TEnum : struct, Enum =>
        typeof(TEnum) == typeof(CovenantAuthorizationMode);

    private static bool IsPositivePolicyEnum<TEnum>()
        where TEnum : struct, Enum =>
        typeof(TEnum) == typeof(CovenantScope)
        || typeof(TEnum) == typeof(SessionCampaignBindingKind)
        || typeof(TEnum) == typeof(CovenantLane)
        || typeof(TEnum) == typeof(CovenantOperation)
        || typeof(TEnum) == typeof(CovenantOrigin)
        || typeof(TEnum) == typeof(CovenantMutationKind)
        || typeof(TEnum) == typeof(CovenantCurationKind)
        || typeof(TEnum) == typeof(CovenantPlacement)
        || typeof(TEnum) == typeof(CovenantPlanDecision)
        || typeof(TEnum) == typeof(CovenantAdmissionDecision)
        || typeof(TEnum) == typeof(CovenantMutationOutcome)
        || typeof(TEnum) == typeof(CovenantFinalOutcome)
        || typeof(TEnum) == typeof(AssistantFinalizationOrigin)
        || typeof(TEnum) == typeof(CovenantWardDecision)
        || typeof(TEnum) == typeof(CovenantProviderRole)
        || typeof(TEnum) == typeof(CovenantProviderDispatchMode)
        || typeof(TEnum) == typeof(CovenantProviderContentPart)
        || typeof(TEnum) == typeof(CovenantToolRiskIdentity)
        || typeof(TEnum) == typeof(GenerationProvenanceMode)
        || typeof(TEnum) == typeof(SensitiveArtifactKind)
        || typeof(TEnum) == typeof(CovenantEgressDestination)
        || typeof(TEnum) == typeof(CovenantDisclosureSubjectKind)
        || typeof(TEnum) == typeof(CovenantDisclosureRevocability)
        || typeof(TEnum) == typeof(CovenantDisclosureCountKind)
        || typeof(TEnum) == typeof(SessionTurnSurface)
        || typeof(TEnum) == typeof(CovenantContextPolicy)
        || typeof(TEnum) == typeof(CovenantToolPolicyCode)
        || typeof(TEnum) == typeof(InvocationAttendance)
        || typeof(TEnum) == typeof(CovenantMaintenanceStep)
        || typeof(TEnum) == typeof(CovenantMaintenanceCheckpoint)
        || typeof(TEnum) == typeof(SessionTurnClaimState)
        || typeof(TEnum) == typeof(BackupDisclosurePhase)
        || typeof(TEnum) == typeof(CampaignPathIdentityOperation)
        || typeof(TEnum) == typeof(ProviderToolChoice)
        || typeof(TEnum) == typeof(ProviderResponseFormat)
        || typeof(TEnum) == typeof(CovenantReasoningEffort)
        || typeof(TEnum) == typeof(CovenantReasoningOutput)
        || typeof(TEnum) == typeof(CovenantReasoningWireDialect)
        || typeof(TEnum) == typeof(CovenantImageDetail)
        || typeof(TEnum) == typeof(CovenantPromptAttribution)
        || typeof(TEnum) == typeof(CovenantMaterializationContainer)
        || typeof(TEnum) == typeof(CovenantMaterializationOccurrence)
        || typeof(TEnum) == typeof(CovenantMaterializationSourceRange)
        || typeof(TEnum) == typeof(CovenantCursorEndpoint)
        || typeof(TEnum) == typeof(CovenantCursorScopeSelection)
        || typeof(TEnum) == typeof(CovenantLifecycle)
        || typeof(TEnum) == typeof(CovenantCursorSort);

    private static uint RequirePositiveCode<TEnum>(byte code, string parameterName)
        where TEnum : struct, Enum
    {
        byte maximum = GetMaximum<TEnum>();

        return RequireRange(code, 1, maximum, parameterName);
    }

    private static byte GetMaximum<TEnum>()
        where TEnum : struct, Enum
    {
        if (typeof(TEnum) == typeof(SensitiveArtifactKind))
        {
            return 13;
        }

        if (typeof(TEnum) == typeof(CovenantPromptAttribution))
        {
            return 9;
        }

        if (typeof(TEnum) == typeof(CovenantEgressDestination))
        {
            return 8;
        }

        if (typeof(TEnum) == typeof(CovenantProviderContentPart))
        {
            return 7;
        }

        if (typeof(TEnum) == typeof(CovenantPlanDecision))
        {
            return 7;
        }

        if (typeof(TEnum) == typeof(SessionTurnClaimState)
            || typeof(TEnum) == typeof(CovenantReasoningEffort))
        {
            return 6;
        }

        if (typeof(TEnum) == typeof(CampaignPathIdentityOperation))
        {
            return 5;
        }

        if (typeof(TEnum) == typeof(CovenantMutationKind)
            || typeof(TEnum) == typeof(CovenantCurationKind)
            || typeof(TEnum) == typeof(CovenantFinalOutcome)
            || typeof(TEnum) == typeof(AssistantFinalizationOrigin)
            || typeof(TEnum) == typeof(CovenantProviderRole)
            || typeof(TEnum) == typeof(CovenantToolRiskIdentity)
            || typeof(TEnum) == typeof(CovenantToolPolicyCode)
            || typeof(TEnum) == typeof(CovenantMaintenanceStep)
            || typeof(TEnum) == typeof(ProviderToolChoice)
            || typeof(TEnum) == typeof(CovenantReasoningWireDialect)
            || typeof(TEnum) == typeof(CovenantCursorEndpoint)
            || typeof(TEnum) == typeof(CovenantCursorSort))
        {
            return 4;
        }

        if (typeof(TEnum) == typeof(SessionCampaignBindingKind)
            || typeof(TEnum) == typeof(CovenantOrigin)
            || typeof(TEnum) == typeof(CovenantPlacement)
            || typeof(TEnum) == typeof(CovenantAdmissionDecision)
            || typeof(TEnum) == typeof(CovenantWardDecision)
            || typeof(TEnum) == typeof(SessionTurnSurface)
            || typeof(TEnum) == typeof(CovenantMaintenanceCheckpoint)
            || typeof(TEnum) == typeof(ProviderResponseFormat)
            || typeof(TEnum) == typeof(CovenantReasoningOutput)
            || typeof(TEnum) == typeof(CovenantImageDetail)
            || typeof(TEnum) == typeof(CovenantMaterializationSourceRange)
            || typeof(TEnum) == typeof(CovenantCursorScopeSelection)
            || typeof(TEnum) == typeof(CovenantLifecycle))
        {
            return 3;
        }

        return 2;
    }

    private static uint RequireRange(byte code, byte minimum, byte maximum, string parameterName) =>
        code >= minimum && code <= maximum
            ? code
            : throw new ArgumentOutOfRangeException(parameterName);
}
