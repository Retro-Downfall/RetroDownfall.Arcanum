using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Core.Covenant;

public static class CovenantDigests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CovenantDigest Section(SectionDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        uint placement = Code(input.Placement, nameof(input.Placement));
        RequireInitialized(input.Items, nameof(input.Items));
        RequireInitialized(input.RenderedBytes, nameof(input.RenderedBytes));
        int maximumEntries;
        int maximumBytes;

        switch (input.Placement)
        {
            case CovenantPlacement.GlobalConfirmed:
                maximumEntries = CovenantLimits.MaxGlobalConfirmedEntries;
                maximumBytes = CovenantLimits.MaxGlobalConfirmedRenderedBytes;
                break;
            case CovenantPlacement.CampaignConfirmed:
                maximumEntries = CovenantLimits.MaxCampaignConfirmedEntries;
                maximumBytes = CovenantLimits.MaxCampaignConfirmedRenderedBytes;
                break;
            case CovenantPlacement.CampaignProposed:
                maximumEntries = CovenantLimits.MaxCampaignProposedEntries;
                maximumBytes = CovenantLimits.MaxCampaignProposedRenderedBytes;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(input));
        }

        if (input.Items.Length > maximumEntries || input.RenderedBytes.Length > maximumBytes)
        {
            throw new ArgumentException("The Section exceeds its placement bound.", nameof(input));
        }

        if (input.Items.IsEmpty && !input.RenderedBytes.IsEmpty)
        {
            throw new ArgumentException("An empty Section must render zero bytes.", nameof(input));
        }

        SectionItemDigestInput[] items = input.Items.ToArray();

        foreach (SectionItemDigestInput item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            RequireKey(item.NormalizedKey, nameof(item.NormalizedKey));
            RequireGuid(item.EntryId, nameof(item.EntryId));
            RequireGuid(item.VersionId, nameof(item.VersionId));
            RequireDigest(item.FragmentDigest, nameof(item.FragmentDigest));
        }

        Array.Sort(items, SectionItemComparer.Instance);

        for (int index = 1; index < items.Length; index++)
        {
            if (SectionItemComparer.Instance.Compare(items[index - 1], items[index]) == 0)
            {
                throw new ArgumentException("Section items must have distinct canonical identities.", nameof(input));
            }
        }

        byte[] framedDigest = SHA256.HashData(input.RenderedBytes.AsSpan());

        return Hash(CovenantDomainTag.Section, writer =>
        {
            writer.WriteUInt32(placement);
            writer.WriteCount((ulong)items.Length);

            foreach (SectionItemDigestInput item in items)
            {
                writer.WriteGuid(item.EntryId);
                writer.WriteGuid(item.VersionId);
                writer.WriteUInt64(item.LaneRevision);
                WriteDigest(writer, item.FragmentDigest);
            }

            writer.WriteFixed32(framedDigest);
        });
    }

    public static CovenantDigest MutationRequest(MutationRequestDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        uint mutationKind = Code(input.MutationKind, nameof(input.MutationKind));
        uint scope = Code(input.Scope, nameof(input.Scope));
        uint lane = Code(input.Lane, nameof(input.Lane));
        uint operation = Code(input.Operation, nameof(input.Operation));
        uint origin = Code(input.Origin, nameof(input.Origin));
        RequireGuid(input.MutationId, nameof(input.MutationId));
        ValidateScopeCampaign(input.Scope, input.CampaignId, nameof(input));
        string key = RequireKey(input.NormalizedKey, nameof(input.NormalizedKey));
        RequireOptionalDigest(input.AuthoredDigest, nameof(input.AuthoredDigest));
        RequireOptionalDigest(input.FragmentDigest, nameof(input.FragmentDigest));
        RequireOptionalDigest(input.BasePlanDigest, nameof(input.BasePlanDigest));
        RequireOptionalDigest(input.AdmissionDigest, nameof(input.AdmissionDigest));
        RequireInitialized(input.ProvenanceDigests, nameof(input.ProvenanceDigests));
        ValidateDigests(input.ProvenanceDigests, nameof(input.ProvenanceDigests));

        return Hash(CovenantDomainTag.Request, writer =>
        {
            writer.WriteUInt32(mutationKind);
            writer.WriteGuid(input.MutationId);
            writer.WriteUInt32(scope);
            WriteOptionalGuid(writer, input.CampaignId);
            writer.WriteUtf8(key);
            writer.WriteUInt32(lane);
            writer.WriteUInt32(operation);
            writer.WriteUInt64(input.ExpectedRevision);
            writer.WriteByte(ToByte(input.Reactivation));
            writer.WriteUInt32(origin);
            WriteOptionalDigest(writer, input.AuthoredDigest);
            WriteOptionalDigest(writer, input.FragmentDigest);
            writer.WriteUInt32(input.CompilerPolicy);
            WriteOptionalDigest(writer, input.BasePlanDigest);
            WriteOptionalDigest(writer, input.AdmissionDigest);
            writer.WriteCount((ulong)input.ProvenanceDigests.Length);

            foreach (CovenantDigest digest in input.ProvenanceDigests)
            {
                WriteDigest(writer, digest);
            }
        });
    }

    public static CovenantDigest PreflightBody(PreflightBodyDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDigestFields(input.RequestDigest, input.DependentHeadVectorDigest, input.EffectDigest);
        RequireGuid(input.DatasetGeneration, nameof(input.DatasetGeneration));
        RequireOptionalDigest(input.CompiledArtifactDigest, nameof(input.CompiledArtifactDigest));

        return Hash(CovenantDomainTag.PreflightBody, writer =>
        {
            WriteDigest(writer, input.RequestDigest);
            writer.WriteUInt64(input.OperatorAuthorityEpoch);
            writer.WriteGuid(input.DatasetGeneration);
            writer.WriteUInt64(input.ExpectedTargetRevision);
            writer.WriteUInt64(input.NormalizedKeyDependencyEpoch);
            writer.WriteUInt64(input.KeyReclamationEpoch);
            WriteOptionalUInt64(writer, input.CampaignRegistryEpoch);
            WriteOptionalDigest(writer, input.CompiledArtifactDigest);
            WriteDigest(writer, input.DependentHeadVectorDigest);
            WriteDigest(writer, input.EffectDigest);
            writer.WriteInt64(input.IssuedAt);
            writer.WriteInt64(input.ExpiresAt);
        });
    }

    public static CovenantDigest Authorization(AuthorizationDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDigestFields(input.RequestDigest);
        RequireGuid(input.DatasetGeneration, nameof(input.DatasetGeneration));
        RequireOptionalDigest(input.PreflightBodyDigest, nameof(input.PreflightBodyDigest));
        RequireOptionalDigest(input.WardReceiptDigest, nameof(input.WardReceiptDigest));
        uint authorization = Code(input.Authorization, nameof(input.Authorization));

        return Hash(CovenantDomainTag.Authorization, writer =>
        {
            WriteDigest(writer, input.RequestDigest);
            writer.WriteGuid(input.DatasetGeneration);
            WriteOptionalUInt64(writer, input.OperatorAuthorityEpoch);
            WriteOptionalUInt64(writer, input.NormalizedKeyDependencyEpoch);
            WriteOptionalUInt64(writer, input.KeyReclamationEpoch);
            WriteOptionalUInt64(writer, input.CampaignRegistryEpoch);
            WriteOptionalDigest(writer, input.PreflightBodyDigest);
            WriteOptionalDigest(writer, input.WardReceiptDigest);
            writer.WriteUInt32(authorization);
        });
    }

    public static CovenantDigest Mutation(MutationDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDigestFields(input.RequestDigest, input.AuthorizationDigest);

        return Hash(CovenantDomainTag.Mutation, writer =>
        {
            WriteDigest(writer, input.RequestDigest);
            WriteDigest(writer, input.AuthorizationDigest);
        });
    }

    public static CovenantDigest Snapshot(SnapshotDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.DatasetGeneration, nameof(input.DatasetGeneration));
        RequireOptionalGuid(input.CanonicalCampaignId, nameof(input.CanonicalCampaignId));
        RequireInitialized(input.Candidates, nameof(input.Candidates));

        if (input.Candidates.Length > CovenantLimits.MaxActiveSnapshotRows)
        {
            throw new ArgumentException("The active snapshot exceeds its row bound.", nameof(input));
        }

        foreach (SnapshotCandidateDigestInput candidate in input.Candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            RequireGuid(candidate.EntryId, nameof(candidate.EntryId));
            RequireGuid(candidate.VersionId, nameof(candidate.VersionId));
            ValidateScopeCampaign(candidate.Scope, candidate.CampaignId, nameof(input));
            _ = Code(candidate.Lane, nameof(candidate.Lane));
            _ = Code(candidate.Operation, nameof(candidate.Operation));
            _ = Code(candidate.Origin, nameof(candidate.Origin));
            RequireOptionalGuid(candidate.PredecessorId, nameof(candidate.PredecessorId));
            ValidateDigestFields(candidate.AuthoredDigest, candidate.FragmentDigest, candidate.ProvenanceDigest);

            if (candidate.ProvenanceCount > CovenantLimits.MaxVersionSources)
            {
                throw new ArgumentOutOfRangeException(nameof(candidate.ProvenanceCount));
            }
        }

        return Hash(CovenantDomainTag.Snapshot, writer =>
        {
            writer.WriteGuid(input.DatasetGeneration);
            WriteOptionalGuid(writer, input.CanonicalCampaignId);
            writer.WriteUInt64(input.CanonicalSearchSequence);
            writer.WriteCount((ulong)input.Candidates.Length);

            foreach (SnapshotCandidateDigestInput candidate in input.Candidates)
            {
                writer.WriteUInt64(candidate.SearchDocumentId);
                writer.WriteGuid(candidate.EntryId);
                writer.WriteGuid(candidate.VersionId);
                WriteCode(writer, candidate.Scope);
                WriteOptionalGuid(writer, candidate.CampaignId);
                WriteCode(writer, candidate.Lane);
                WriteCode(writer, candidate.Operation);
                WriteCode(writer, candidate.Origin);
                writer.WriteUInt64(candidate.Revision);
                WriteOptionalGuid(writer, candidate.PredecessorId);
                writer.WriteUInt32(candidate.CompilerPolicy);
                writer.WriteUInt32(candidate.RendererPolicy);
                WriteDigest(writer, candidate.AuthoredDigest);
                WriteDigest(writer, candidate.FragmentDigest);
                writer.WriteUInt32(candidate.ProvenanceCount);
                WriteDigest(writer, candidate.ProvenanceDigest);
                writer.WriteUInt32(candidate.CompiledBytes);
            }
        });
    }

    public static CovenantDigest Plan(PlanDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDigestFields(input.SnapshotDigest, input.EligibleGlobalConfirmedSectionDigest, input.EligibleCampaignConfirmedSectionDigest, input.EligibleCampaignProposedSectionDigest);
        RequireInitialized(input.Decisions, nameof(input.Decisions));

        foreach (PlanDecisionDigestInput decision in input.Decisions)
        {
            ArgumentNullException.ThrowIfNull(decision);
            RequireGuid(decision.EntryId, nameof(decision.EntryId));
            RequireGuid(decision.VersionId, nameof(decision.VersionId));
            _ = Code(decision.Decision, nameof(decision.Decision));
            RequireOptionalGuid(decision.ShadowingVersionId, nameof(decision.ShadowingVersionId));
            _ = Code(decision.Placement, nameof(decision.Placement));
            RequireDigest(decision.FragmentDigest, nameof(decision.FragmentDigest));
        }

        return Hash(CovenantDomainTag.Plan, writer =>
        {
            WriteDigest(writer, input.SnapshotDigest);
            writer.WriteUInt32(input.LinkerPolicy);
            writer.WriteUInt32(input.PlacementPolicy);
            writer.WriteCount((ulong)input.Decisions.Length);

            foreach (PlanDecisionDigestInput decision in input.Decisions)
            {
                writer.WriteGuid(decision.EntryId);
                writer.WriteGuid(decision.VersionId);
                WriteCode(writer, decision.Decision);
                WriteOptionalGuid(writer, decision.ShadowingVersionId);
                WriteCode(writer, decision.Placement);
                WriteDigest(writer, decision.FragmentDigest);
                writer.WriteUInt32(decision.ByteCost);
            }

            WriteDigest(writer, input.EligibleGlobalConfirmedSectionDigest);
            WriteDigest(writer, input.EligibleCampaignConfirmedSectionDigest);
            WriteDigest(writer, input.EligibleCampaignProposedSectionDigest);
        });
    }

    public static CovenantDigest Materialization(MaterializationDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireInitialized(input.Sources, nameof(input.Sources));

        if (input.Sources.Length > CovenantLimits.MaxAttachmentSourcesPerAgentMutation)
        {
            throw new ArgumentException("The materialization source vector exceeds its bound.", nameof(input));
        }

        MaterializationSourceDigestInput[] sources = input.Sources.ToArray();

        for (int index = 0; index < sources.Length; index++)
        {
            sources[index] = ValidateAndSortSource(sources[index]);
        }

        Array.Sort(sources, MaterializationSourceComparer.Instance);

        for (int index = 1; index < sources.Length; index++)
        {
            if (MaterializationSourceComparer.Instance.Compare(sources[index - 1], sources[index]) == 0)
            {
                throw new ArgumentException("Materialization sources must have distinct canonical identities.", nameof(input));
            }
        }

        return Hash(CovenantDomainTag.Materialization, writer =>
        {
            writer.WriteByte(ToByte(input.Unprovenanced));
            writer.WriteCount((ulong)sources.Length);

            foreach (MaterializationSourceDigestInput source in sources)
            {
                writer.WriteGuid(source.AttachmentId);
                writer.WriteGuid(source.AttachmentVersionId);
                writer.WriteUtf8(source.LogicalKey);
                WriteDigest(writer, source.ContentDigest);
                WriteCode(writer, source.SourceRange);
                WriteOptionalUInt32(writer, source.SourceStart);
                WriteOptionalUInt32(writer, source.SourceEnd);
                writer.WriteCount((ulong)source.Occurrences.Length);

                foreach (MaterializationOccurrenceDigestInput occurrence in source.Occurrences)
                {
                    WriteCode(writer, occurrence.Container);
                    WriteOptionalUInt32(writer, occurrence.MessageIndex);
                    WriteOptionalUInt32(writer, occurrence.ContentPartIndex);
                    WriteCode(writer, occurrence.Occurrence);
                    WriteOptionalUInt32(writer, occurrence.Utf16Start);
                    writer.WriteUInt32(occurrence.Length);
                }
            }
        });
    }

    public static CovenantDigest Sensitivity(SensitivityDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        uint level = Code(input.Level, nameof(input.Level));
        uint mode = Code(input.ProvenanceMode, nameof(input.ProvenanceMode));
        Guid[] exact = [];

        if (input.ProvenanceMode == GenerationProvenanceMode.Exact)
        {
            RequireInitialized(input.ExactGenerationIds, nameof(input.ExactGenerationIds));

            if (!input.GenerationBloom.IsDefault && !input.GenerationBloom.IsEmpty)
            {
                throw new ArgumentException("Exact provenance cannot carry a Bloom.", nameof(input));
            }

            if (input.ExactGenerationIds.Length > CovenantLimits.MaxExactGenerationIds)
            {
                throw new ArgumentException("Exact provenance exceeds eight generations.", nameof(input));
            }

            exact = input.ExactGenerationIds.ToArray();

            foreach (Guid generation in exact)
            {
                RequireGuid(generation, nameof(input.ExactGenerationIds));
            }

            Array.Sort(exact, RawGuidComparer.Instance);

            for (int index = 1; index < exact.Length; index++)
            {
                if (RawGuidComparer.Instance.Compare(exact[index - 1], exact[index]) == 0)
                {
                    throw new ArgumentException("Exact generations must be distinct.", nameof(input));
                }
            }
        }
        else
        {
            if (!input.ExactGenerationIds.IsDefault && !input.ExactGenerationIds.IsEmpty)
            {
                throw new ArgumentException("Bloom provenance cannot carry exact generations.", nameof(input));
            }

            RequireFixed32(input.GenerationBloom, nameof(input.GenerationBloom));

            bool hasGenerationContribution = false;

            foreach (byte value in input.GenerationBloom)
            {
                if (value != 0)
                {
                    hasGenerationContribution = true;
                    break;
                }
            }

            if (!hasGenerationContribution)
            {
                throw new ArgumentException("Bloom provenance requires at least one generation contribution.", nameof(input.GenerationBloom));
            }
        }

        if (input.Level == ContentSensitivity.None && (input.ProvenanceMode != GenerationProvenanceMode.Exact || exact.Length != 0))
        {
            throw new ArgumentException("Nonsensitive content requires empty exact provenance.", nameof(input));
        }

        if (input.Level == ContentSensitivity.CovenantDerived && input.ProvenanceMode == GenerationProvenanceMode.Exact && exact.Length == 0)
        {
            throw new ArgumentException("Covenant-derived content requires generation provenance.", nameof(input));
        }

        return Hash(CovenantDomainTag.Sensitivity, writer =>
        {
            writer.WriteUInt32(level);
            writer.WriteUInt32(mode);

            if (input.ProvenanceMode == GenerationProvenanceMode.Exact)
            {
                writer.WriteCount((ulong)exact.Length);

                foreach (Guid generation in exact)
                {
                    writer.WriteGuid(generation);
                }
            }
            else
            {
                writer.WriteFixed32(input.GenerationBloom.AsSpan());
            }
        });
    }

    public static CovenantDigest ArtifactLabel(ArtifactLabelDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        uint kind = Code(input.ArtifactKind, nameof(input.ArtifactKind));
        RequireGuid(input.ArtifactId, nameof(input.ArtifactId));
        RequireOptionalGuid(input.SessionId, nameof(input.SessionId));
        RequireOptionalGuid(input.CampaignId, nameof(input.CampaignId));
        RequireOptionalGuid(input.TurnId, nameof(input.TurnId));
        ValidateDigestFields(input.ArtifactContentDigest, input.SensitivityDigest);
        RequireOptionalDigest(input.ProducingPlanDigest, nameof(input.ProducingPlanDigest));
        RequireOptionalDigest(input.ProducingAdmissionDigest, nameof(input.ProducingAdmissionDigest));
        RequireOptionalDigest(input.ProducingMaintenanceReceiptDigest, nameof(input.ProducingMaintenanceReceiptDigest));
        bool hasPlanPair = input.ProducingPlanDigest.HasValue && input.ProducingAdmissionDigest.HasValue;

        if (input.ProducingPlanDigest.HasValue != input.ProducingAdmissionDigest.HasValue
            || (hasPlanPair && input.ProducingMaintenanceReceiptDigest.HasValue))
        {
            throw new ArgumentException("Artifact production evidence has an invalid union shape.", nameof(input));
        }

        return Hash(CovenantDomainTag.ArtifactLabel, writer =>
        {
            writer.WriteUInt32(kind);
            writer.WriteGuid(input.ArtifactId);
            WriteOptionalGuid(writer, input.SessionId);
            WriteOptionalGuid(writer, input.CampaignId);
            WriteOptionalGuid(writer, input.TurnId);
            writer.WriteUInt64(input.ArtifactRevision);
            WriteDigest(writer, input.ArtifactContentDigest);
            WriteDigest(writer, input.SensitivityDigest);
            WriteOptionalDigest(writer, input.ProducingPlanDigest);
            WriteOptionalDigest(writer, input.ProducingAdmissionDigest);
            WriteOptionalDigest(writer, input.ProducingMaintenanceReceiptDigest);
        });
    }

    public static CovenantDigest SessionTurnRequest(SessionTurnRequestDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        uint surface = Code(input.Surface, nameof(input.Surface));
        uint dispatch = Code(input.DispatchMode, nameof(input.DispatchMode));
        uint context = Code(input.ContextPolicy, nameof(input.ContextPolicy));
        RequireGuid(input.ClientTurnId, nameof(input.ClientTurnId));
        RequireOptionalGuid(input.RequestedSessionId, nameof(input.RequestedSessionId));
        RequireOptionalGuid(input.ExplicitCampaignId, nameof(input.ExplicitCampaignId));
        ValidateDigestFields(input.AcceptedBodyDigest);
        RequireOptionalDigest(input.OpenedWorkingDirectoryIdentityDigest, nameof(input.OpenedWorkingDirectoryIdentityDigest));

        switch (input.Surface)
        {
            case SessionTurnSurface.Intelligence when input.Route is not null:
                throw new ArgumentException("Intelligence turns cannot carry a route.", nameof(input));
            case SessionTurnSurface.PromptExecute when input.Route?.PromptId is null || input.Route.SpellName is not null:
                throw new ArgumentException("Prompt execution requires only a Prompt route ID.", nameof(input));
            case SessionTurnSurface.PromptExecute:
                RequireGuid(input.Route!.PromptId!.Value, nameof(input.Route));
                break;
            case SessionTurnSurface.SpellExecute when input.Route?.SpellName is null || input.Route.PromptId is not null:
                throw new ArgumentException("Spell execution requires only a Spell route name.", nameof(input));
            case SessionTurnSurface.SpellExecute:
                RequireText(input.Route!.SpellName!, nameof(input.Route));
                break;
        }

        return Hash(CovenantDomainTag.SessionTurnRequest, writer =>
        {
            writer.WriteUInt32(surface);
            writer.WriteUInt32(dispatch);
            writer.WriteGuid(input.ClientTurnId);
            WriteOptionalGuid(writer, input.RequestedSessionId);
            writer.WriteUInt32(context);
            writer.WriteByte(input.Route is null ? (byte)0 : (byte)1);

            if (input.Route is not null)
            {
                if (input.Surface == SessionTurnSurface.PromptExecute)
                {
                    writer.WriteGuid(input.Route.PromptId!.Value);
                }
                else
                {
                    writer.WriteUtf8(input.Route.SpellName!);
                }
            }

            WriteDigest(writer, input.AcceptedBodyDigest);
            WriteOptionalGuid(writer, input.ExplicitCampaignId);
            WriteOptionalDigest(writer, input.OpenedWorkingDirectoryIdentityDigest);
        });
    }

    public static CovenantDigest SessionTurnExecution(SessionTurnExecutionDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDigestFields(input.SessionTurnRequestDigest, input.ProviderOptionsDigest);
        RequireGuid(input.ResolvedSessionId, nameof(input.ResolvedSessionId));
        uint binding = Code(input.BindingKind, nameof(input.BindingKind));

        if (input.BindingKind == SessionCampaignBindingKind.LegacyUnresolved)
        {
            throw new ArgumentException("Legacy unresolved bindings cannot produce execution digests.", nameof(input));
        }

        if ((input.BindingKind == SessionCampaignBindingKind.Campaign) != (input.CampaignContext is not null))
        {
            throw new ArgumentException("Campaign binding and Campaign context must agree.", nameof(input));
        }

        if (input.BindingKind != SessionCampaignBindingKind.Campaign && input.Path is not null)
        {
            throw new ArgumentException("Only a Campaign-bound execution can carry a Campaign path block.", nameof(input));
        }

        if (input.CampaignContext is { } campaign)
        {
            RequireGuid(campaign.CampaignId, nameof(input.CampaignContext));
            RequirePositive(campaign.AvailabilityGeneration, nameof(input.CampaignContext));
        }

        if (input.Path is { } path)
        {
            RequirePositive(path.PathRevision, nameof(input.Path));
            RequireDigest(path.RootIdentityDigest, nameof(input.Path));
        }

        RequirePositive(input.PreRequestHistoryWatermark, nameof(input.PreRequestHistoryWatermark));
        RequireOptionalGuid(input.RouteVersionId, nameof(input.RouteVersionId));
        RequirePositive(input.ProviderConfigurationGeneration, nameof(input.ProviderConfigurationGeneration));
        string provider = RequireText(input.ProviderIdentity, nameof(input.ProviderIdentity));
        string model = RequireText(input.ModelIdentity, nameof(input.ModelIdentity));
        RequireInitialized(input.ResolvedAttachmentVersionIds, nameof(input.ResolvedAttachmentVersionIds));
        Guid[] attachments = input.ResolvedAttachmentVersionIds.ToArray();

        foreach (Guid attachment in attachments)
        {
            RequireGuid(attachment, nameof(input.ResolvedAttachmentVersionIds));
        }

        Array.Sort(attachments, RawGuidComparer.Instance);

        for (int index = 1; index < attachments.Length; index++)
        {
            if (attachments[index - 1] == attachments[index])
            {
                throw new ArgumentException("Resolved attachment versions must be distinct.", nameof(input));
            }
        }

        uint toolPolicy = Code(input.ToolPolicy, nameof(input.ToolPolicy));
        uint attendance = Code(input.Attendance, nameof(input.Attendance));

        return Hash(CovenantDomainTag.SessionTurnExecution, writer =>
        {
            WriteDigest(writer, input.SessionTurnRequestDigest);
            writer.WriteGuid(input.ResolvedSessionId);
            writer.WriteUInt32(binding);
            writer.WriteByte(input.CampaignContext is null ? (byte)0 : (byte)1);

            if (input.CampaignContext is { } campaign)
            {
                writer.WriteGuid(campaign.CampaignId);
                writer.WriteInt64(campaign.AvailabilityGeneration);
            }

            writer.WriteByte(input.Path is null ? (byte)0 : (byte)1);

            if (input.Path is { } path)
            {
                writer.WriteInt64(path.PathRevision);
                WriteDigest(writer, path.RootIdentityDigest);
            }

            writer.WriteInt64(input.PreRequestHistoryWatermark);
            WriteOptionalGuid(writer, input.RouteVersionId);
            writer.WriteInt64(input.ProviderConfigurationGeneration);
            writer.WriteUtf8(provider);
            writer.WriteUtf8(model);
            WriteDigest(writer, input.ProviderOptionsDigest);
            writer.WriteCount((ulong)attachments.Length);

            foreach (Guid attachment in attachments)
            {
                writer.WriteGuid(attachment);
            }

            writer.WriteUInt32(toolPolicy);
            writer.WriteByte(ToByte(input.DisableMcpTools));
            writer.WriteByte(ToByte(input.DisableAllTools));
            writer.WriteUInt32(attendance);
        });
    }

    public static CovenantDigest ProviderOptions(ProviderOptionsDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateFinite(input.Temperature, nameof(input.Temperature));
        ValidateFinite(input.TopP, nameof(input.TopP));
        ValidateFinite(input.FrequencyPenalty, nameof(input.FrequencyPenalty));
        ValidateFinite(input.PresencePenalty, nameof(input.PresencePenalty));

        if (input.EndUserIdentity is { } endUser && StrictUtf8.GetByteCount(endUser) > CovenantLimits.MaxEndUserIdentityBytes)
        {
            throw new ArgumentException("The end-user identity exceeds its protocol bound.", nameof(input));
        }

        ValidateOptionalText(input.EndUserIdentity, nameof(input.EndUserIdentity), allowEmpty: true);
        RequireInitialized(input.Stop, nameof(input.Stop));

        foreach (string stop in input.Stop)
        {
            _ = RequireTextValue(stop, nameof(input.Stop), allowEmpty: true);
        }

        uint toolChoice = Code(input.ToolChoice, nameof(input.ToolChoice));
        uint parallel = Code(input.ParallelToolCalls, nameof(input.ParallelToolCalls));
        uint response = Code(input.ResponseFormat, nameof(input.ResponseFormat));
        uint strict = Code(input.JsonSchemaStrict, nameof(input.JsonSchemaStrict));
        uint dialect = Code(input.ReasoningWireDialect, nameof(input.ReasoningWireDialect));
        ValidateOptionalText(input.NamedTool, nameof(input.NamedTool), allowEmpty: false);
        ValidateOptionalText(input.JsonSchemaName, nameof(input.JsonSchemaName), allowEmpty: false);
        ValidateOptionalText(input.JsonSchemaDescription, nameof(input.JsonSchemaDescription), allowEmpty: true);
        RequireOptionalDigest(input.CanonicalJsonSchemaDigest, nameof(input.CanonicalJsonSchemaDigest));

        if ((input.ToolChoice == ProviderToolChoice.Named) != (input.NamedTool is not null))
        {
            throw new ArgumentException("Named tool choice requires exactly one nonempty named tool.", nameof(input));
        }

        if (input.ResponseFormat == ProviderResponseFormat.JsonSchema)
        {
            if (input.JsonSchemaName is null || input.CanonicalJsonSchemaDigest is null)
            {
                throw new ArgumentException("JSON schema format requires a name and canonical schema digest.", nameof(input));
            }
        }
        else if (input.JsonSchemaName is not null
            || input.JsonSchemaDescription is not null
            || input.CanonicalJsonSchemaDigest is not null
            || input.JsonSchemaStrict != CovenantTriStateBoolean.Absent)
        {
            throw new ArgumentException("Schema details require JSON schema response format.", nameof(input));
        }

        uint? effort = input.ReasoningEffort is { } effortValue ? Code(effortValue, nameof(input.ReasoningEffort)) : null;
        uint? output = input.ReasoningOutput is { } outputValue ? Code(outputValue, nameof(input.ReasoningOutput)) : null;
        FrozenLogitBias[]? biases = null;

        if (!input.LogitBias.IsDefault)
        {
            biases = input.LogitBias.ToArray();
            Array.Sort(biases, static (left, right) => left.TokenId.CompareTo(right.TokenId));

            for (int index = 0; index < biases.Length; index++)
            {
                ValidateFinite(biases[index].Bias, nameof(input.LogitBias));

                if (index > 0 && biases[index - 1].TokenId == biases[index].TokenId)
                {
                    throw new ArgumentException("Logit-bias token IDs must be distinct.", nameof(input));
                }
            }
        }

        return Hash(CovenantDomainTag.ProviderOptions, writer =>
        {
            WriteOptionalUInt64(writer, input.MaxOutputTokens);
            WriteOptionalBinary64(writer, input.Temperature);
            WriteOptionalBinary64(writer, input.TopP);
            WriteOptionalBinary64(writer, input.FrequencyPenalty);
            WriteOptionalBinary64(writer, input.PresencePenalty);
            WriteOptionalInt64(writer, input.Seed);
            WriteOptionalText(writer, input.EndUserIdentity);
            writer.WriteCount((ulong)input.Stop.Length);

            foreach (string stop in input.Stop)
            {
                writer.WriteUtf8(stop);
            }

            writer.WriteUInt32(toolChoice);
            WriteOptionalText(writer, input.NamedTool);
            writer.WriteByte((byte)parallel);
            writer.WriteUInt32(response);
            WriteOptionalText(writer, input.JsonSchemaName);
            WriteOptionalText(writer, input.JsonSchemaDescription);
            WriteOptionalDigest(writer, input.CanonicalJsonSchemaDigest);
            writer.WriteByte((byte)strict);
            WriteOptionalUInt32(writer, effort);
            WriteOptionalUInt32(writer, input.ReasoningBudget);
            WriteOptionalUInt32(writer, output);
            writer.WriteUInt32(dialect);
            writer.WriteByte(biases is null ? (byte)0 : (byte)1);

            if (biases is not null)
            {
                writer.WriteCount((ulong)biases.Length);

                foreach (FrozenLogitBias bias in biases)
                {
                    writer.WriteInt32(bias.TokenId);
                    writer.WriteBinary64(bias.Bias);
                }
            }
        });
    }

    public static CovenantDigest ProviderCall(ProviderCallDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string provider = RequireText(input.ProviderIdentity, nameof(input.ProviderIdentity));
        string model = RequireText(input.ModelIdentity, nameof(input.ModelIdentity));
        string tokenizer = RequireText(input.TokenizerProfile, nameof(input.TokenizerProfile));
        uint dispatch = Code(input.DispatchMode, nameof(input.DispatchMode));
        ValidateDigestFields(input.SensitivityDigest, input.ProviderOptionsDigest, input.MaterializationDigest);
        RequireInitialized(input.SystemPromptBytes, nameof(input.SystemPromptBytes));
        RequireInitialized(input.PromptSpans, nameof(input.PromptSpans));
        RequireInitialized(input.Messages, nameof(input.Messages));
        RequireInitialized(input.ToolDefinitions, nameof(input.ToolDefinitions));
        RequireOptionalDigest(input.StructuredOutputSchemaDigest, nameof(input.StructuredOutputSchemaDigest));
        int systemUtf16Length = StrictUtf8.GetCharCount(input.SystemPromptBytes.AsSpan());
        ulong priorEnd = 0;

        foreach (ProviderPromptSpanDigestInput span in input.PromptSpans)
        {
            ArgumentNullException.ThrowIfNull(span);
            _ = Code(span.Attribution, nameof(span.Attribution));
            RequireDigest(span.SegmentDigest, nameof(span.SegmentDigest));
            ulong end = checked((ulong)span.Utf16Start + span.Utf16Length);

            if (span.Utf16Start < priorEnd || end > (ulong)systemUtf16Length)
            {
                throw new ArgumentException("Prompt spans must be start-ordered, nonoverlapping, and within the system prompt.", nameof(input));
            }

            priorEnd = end;
        }

        foreach (ProviderMessageDigestInput message in input.Messages)
        {
            ValidateMessage(message);
        }

        foreach (ProviderToolDefinitionDigestInput tool in input.ToolDefinitions)
        {
            ArgumentNullException.ThrowIfNull(tool);
            _ = RequireText(tool.Name, nameof(tool.Name));
            ValidateDigestFields(tool.DescriptionDigest, tool.InputSchemaDigest);
            RequireOptionalDigest(tool.OutputSchemaDigest, nameof(tool.OutputSchemaDigest));
            _ = Code(tool.RiskIdentity, nameof(tool.RiskIdentity));
        }

        return Hash(CovenantDomainTag.ProviderCall, writer =>
        {
            writer.WriteUtf8(provider);
            writer.WriteUtf8(model);
            writer.WriteUInt32(dispatch);
            writer.WriteUtf8(tokenizer);
            writer.WriteUInt64(input.ContextWindowIdentity);
            writer.WriteUInt64(input.CompressionGeneration);
            WriteDigest(writer, input.SensitivityDigest);
            WriteDigest(writer, input.ProviderOptionsDigest);
            writer.WriteBytes(input.SystemPromptBytes.AsSpan());
            writer.WriteCount((ulong)input.PromptSpans.Length);

            foreach (ProviderPromptSpanDigestInput span in input.PromptSpans)
            {
                WriteCode(writer, span.Attribution);
                writer.WriteUInt32(span.Utf16Start);
                writer.WriteUInt32(span.Utf16Length);
                WriteDigest(writer, span.SegmentDigest);
            }

            WriteDigest(writer, input.MaterializationDigest);
            writer.WriteCount((ulong)input.Messages.Length);

            foreach (ProviderMessageDigestInput message in input.Messages)
            {
                WriteMessage(writer, message);
            }

            writer.WriteCount((ulong)input.ToolDefinitions.Length);

            foreach (ProviderToolDefinitionDigestInput tool in input.ToolDefinitions)
            {
                writer.WriteUtf8(tool.Name);
                WriteDigest(writer, tool.DescriptionDigest);
                WriteDigest(writer, tool.InputSchemaDigest);
                WriteOptionalDigest(writer, tool.OutputSchemaDigest);
                WriteCode(writer, tool.RiskIdentity);
            }

            WriteOptionalDigest(writer, input.StructuredOutputSchemaDigest);
        });
    }

    public static CovenantDigest Admission(AdmissionDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDigestFields(input.PlanDigest, input.ProviderCallDigest, input.MaterializationDigest, input.SensitivityDigest, input.AdmittedGlobalConfirmedSectionDigest, input.AdmittedCampaignConfirmedSectionDigest, input.AdmittedCampaignProposedSectionDigest);
        RequireGuid(input.BranchId, nameof(input.BranchId));
        RequireOptionalDigest(input.ParentAdmissionDigest, nameof(input.ParentAdmissionDigest));
        RequireInitialized(input.EligibleCandidates, nameof(input.EligibleCandidates));

        foreach (AdmissionCandidateDigestInput candidate in input.EligibleCandidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            RequireGuid(candidate.EntryId, nameof(candidate.EntryId));
            RequireGuid(candidate.VersionId, nameof(candidate.VersionId));
            _ = Code(candidate.Decision, nameof(candidate.Decision));
        }

        return Hash(CovenantDomainTag.Admission, writer =>
        {
            WriteDigest(writer, input.PlanDigest);
            writer.WriteUInt64(input.GlobalAttemptOrdinal);
            writer.WriteGuid(input.BranchId);
            writer.WriteUInt64(input.BranchOrdinal);
            WriteOptionalDigest(writer, input.ParentAdmissionDigest);
            WriteDigest(writer, input.ProviderCallDigest);
            WriteDigest(writer, input.MaterializationDigest);
            WriteDigest(writer, input.SensitivityDigest);
            writer.WriteUInt64(input.AvailableTokenBudget);
            writer.WriteCount((ulong)input.EligibleCandidates.Length);

            foreach (AdmissionCandidateDigestInput candidate in input.EligibleCandidates)
            {
                writer.WriteGuid(candidate.EntryId);
                writer.WriteGuid(candidate.VersionId);
                WriteCode(writer, candidate.Decision);
                writer.WriteUInt64(candidate.EstimatedTokens);
            }

            WriteDigest(writer, input.AdmittedGlobalConfirmedSectionDigest);
            WriteDigest(writer, input.AdmittedCampaignConfirmedSectionDigest);
            WriteDigest(writer, input.AdmittedCampaignProposedSectionDigest);
        });
    }

    public static CovenantDigest WardEvidence(WardEvidenceDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDigestFields(input.ToolNameDigest, input.FinalArgumentDigest, input.SensitivityDigest, input.OpaqueDestinationIdentityDigest);
        uint risk = Code(input.EffectiveRisk, nameof(input.EffectiveRisk));
        uint destination = Code(input.Destination, nameof(input.Destination));
        uint decision = Code(input.Decision, nameof(input.Decision));

        return Hash(CovenantDomainTag.WardEvidence, writer =>
        {
            WriteDigest(writer, input.ToolNameDigest);
            WriteDigest(writer, input.FinalArgumentDigest);
            writer.WriteUInt32(risk);
            WriteDigest(writer, input.SensitivityDigest);
            writer.WriteUInt32(destination);
            WriteDigest(writer, input.OpaqueDestinationIdentityDigest);
            writer.WriteUInt64(input.OperatorAuthorityEpoch);
            writer.WriteUInt32(decision);
        });
    }

    public static CovenantDigest ProviderDispatchEffect(ProviderDispatchEffectDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.TurnSubjectId, nameof(input.TurnSubjectId));
        ValidateDigestFields(input.AdmissionDigest, input.ProviderCallDigest, input.ProviderDestinationIdentityDigest);

        return Hash(CovenantDomainTag.ProviderDispatchEffect, writer =>
        {
            writer.WriteGuid(input.TurnSubjectId);
            writer.WriteUInt64(input.PhysicalProviderAttemptOrdinal);
            WriteDigest(writer, input.AdmissionDigest);
            WriteDigest(writer, input.ProviderCallDigest);
            WriteDigest(writer, input.ProviderDestinationIdentityDigest);
        });
    }

    public static CovenantDigest MaintenanceDispatchEffect(MaintenanceDispatchEffectDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.PendingClaimSubjectId, nameof(input.PendingClaimSubjectId));
        uint step = Code(input.MaintenanceStep, nameof(input.MaintenanceStep));
        ValidateDigestFields(input.ProviderCallDigest, input.ProviderDestinationIdentityDigest);

        return Hash(CovenantDomainTag.MaintenanceDispatchEffect, writer =>
        {
            writer.WriteGuid(input.PendingClaimSubjectId);
            writer.WriteUInt32(step);
            writer.WriteUInt64(input.PhysicalProviderAttemptOrdinal);
            WriteDigest(writer, input.ProviderCallDigest);
            WriteDigest(writer, input.ProviderDestinationIdentityDigest);
        });
    }

    public static CovenantDigest ToolEgressEffect(ToolEgressEffectDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.TurnSubjectId, nameof(input.TurnSubjectId));
        ValidateDigestFields(input.ProducingAdmissionDigest, input.CapabilityNonceDigest, input.FrozenToolNameDigest, input.CanonicalArgumentDigest, input.OpaqueDestinationIdentityDigest);
        string toolCall = RequireText(input.ToolCallId, nameof(input.ToolCallId));
        uint destination = Code(input.Destination, nameof(input.Destination));

        return Hash(CovenantDomainTag.ToolEgressEffect, writer =>
        {
            writer.WriteGuid(input.TurnSubjectId);
            writer.WriteUInt64(input.PhysicalEffectAttemptOrdinal);
            WriteDigest(writer, input.ProducingAdmissionDigest);
            WriteDigest(writer, input.CapabilityNonceDigest);
            writer.WriteUtf8(toolCall);
            WriteDigest(writer, input.FrozenToolNameDigest);
            WriteDigest(writer, input.CanonicalArgumentDigest);
            writer.WriteUInt32(destination);
            WriteDigest(writer, input.OpaqueDestinationIdentityDigest);
        });
    }

    public static CovenantDigest ManagedFileEffect(ManagedFileEffectDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.TurnSubjectId, nameof(input.TurnSubjectId));
        string toolCall = RequireText(input.ToolCallId, nameof(input.ToolCallId));
        ValidateDigestFields(input.NoFollowTargetCapabilityDigest, input.ContentDigest);

        return Hash(CovenantDomainTag.ManagedFileEffect, writer =>
        {
            writer.WriteGuid(input.TurnSubjectId);
            writer.WriteUInt64(input.PhysicalEffectAttemptOrdinal);
            writer.WriteUtf8(toolCall);
            WriteDigest(writer, input.NoFollowTargetCapabilityDigest);
            WriteDigest(writer, input.ContentDigest);
        });
    }

    public static CovenantDigest BackupDisclosureEffect(BackupDisclosureEffectDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.BackupOperationId, nameof(input.BackupOperationId));
        RequireGuid(input.BackupIdentity, nameof(input.BackupIdentity));
        RequireDigest(input.OpaqueDestinationIdentityDigest, nameof(input.OpaqueDestinationIdentityDigest));
        uint phase = Code(input.Phase, nameof(input.Phase));

        return Hash(CovenantDomainTag.BackupDisclosureEffect, writer =>
        {
            writer.WriteGuid(input.BackupOperationId);
            writer.WriteUInt64(input.PhysicalPhaseAttemptOrdinal);
            writer.WriteGuid(input.BackupIdentity);
            WriteDigest(writer, input.OpaqueDestinationIdentityDigest);
            writer.WriteUInt32(phase);
        });
    }

    public static CovenantDigest ExternalDisclosure(ExternalDisclosureDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.OriginInstallationId, nameof(input.OriginInstallationId));
        RequireGuid(input.SubjectId, nameof(input.SubjectId));
        ValidateDigestFields(input.EffectIdentityDigest, input.OpaqueDestinationIdentityDigest, input.SensitivityDigest);
        RequireOptionalDigest(input.WardEvidenceDigest, nameof(input.WardEvidenceDigest));
        RequireOptionalDigest(input.AdmissionDigest, nameof(input.AdmissionDigest));
        RequireOptionalDigest(input.BackupEvidenceDigest, nameof(input.BackupEvidenceDigest));
        uint subject = Code(input.SubjectKind, nameof(input.SubjectKind));
        uint destination = Code(input.Destination, nameof(input.Destination));
        uint revocability = Code(input.Revocability, nameof(input.Revocability));

        return Hash(CovenantDomainTag.ExternalDisclosure, writer =>
        {
            writer.WriteGuid(input.OriginInstallationId);
            writer.WriteUInt32(subject);
            writer.WriteGuid(input.SubjectId);
            WriteDigest(writer, input.EffectIdentityDigest);
            writer.WriteUInt64(input.AllocatedSubjectOrdinal);
            writer.WriteUInt32(destination);
            writer.WriteUInt32(revocability);
            WriteDigest(writer, input.OpaqueDestinationIdentityDigest);
            WriteDigest(writer, input.SensitivityDigest);
            WriteOptionalDigest(writer, input.WardEvidenceDigest);
            WriteOptionalDigest(writer, input.AdmissionDigest);
            WriteOptionalDigest(writer, input.BackupEvidenceDigest);
            writer.WriteInt64(input.Timestamp);
        });
    }

    public static CovenantDigest ExternalDisclosureState(ExternalDisclosureStateDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        uint destination = Code(input.Destination, nameof(input.Destination));
        uint revocability = Code(input.Revocability, nameof(input.Revocability));
        uint countKind = Code(input.CountKind, nameof(input.CountKind));
        RequireFixed32(input.EvidenceBloom, nameof(input.EvidenceBloom));
        ValidateDisclosureStateShape(input.CountKind, input.EverOccurred, input.Count, input.MaximumTimestamp, input.EvidenceBloom.AsSpan());

        return Hash(CovenantDomainTag.ExternalDisclosureState, writer =>
        {
            writer.WriteUInt32(destination);
            writer.WriteUInt32(revocability);
            writer.WriteUInt32(countKind);
            writer.WriteByte(ToByte(input.EverOccurred));
            writer.WriteUInt64(input.Count);
            writer.WriteInt64(input.MaximumTimestamp);
            writer.WriteFixed32(input.EvidenceBloom.AsSpan());
        });
    }

    public static CovenantDigest CampaignPathApplyRequest(CampaignPathApplyRequestDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.OperationId, nameof(input.OperationId));
        RequireGuid(input.CampaignId, nameof(input.CampaignId));
        RequireDigest(input.EffectDigest, nameof(input.EffectDigest));
        uint operation = Code(input.Operation, nameof(input.Operation));

        return Hash(CovenantDomainTag.CampaignPathApplyRequest, writer =>
        {
            writer.WriteGuid(input.OperationId);
            writer.WriteGuid(input.CampaignId);
            writer.WriteUInt32(operation);
            WriteDigest(writer, input.EffectDigest);
        });
    }

    public static CovenantDigest SessionBindingApplyRequest(SessionBindingApplyRequestDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.OperationId, nameof(input.OperationId));
        RequireGuid(input.SessionId, nameof(input.SessionId));
        uint binding = Code(input.BindingKind, nameof(input.BindingKind));
        RequireOptionalGuid(input.CampaignId, nameof(input.CampaignId));
        ValidateDigestFields(input.PriorBindingRowDigest, input.EffectDigest);

        if ((input.BindingKind == SessionCampaignBindingKind.Campaign) != input.CampaignId.HasValue)
        {
            throw new ArgumentException("Campaign binding and Campaign identity must be present together.", nameof(input));
        }

        return Hash(CovenantDomainTag.SessionCampaignBindingApplyRequest, writer =>
        {
            writer.WriteGuid(input.OperationId);
            writer.WriteGuid(input.SessionId);
            writer.WriteUInt32(binding);
            WriteOptionalGuid(writer, input.CampaignId);
            WriteDigest(writer, input.PriorBindingRowDigest);
            WriteDigest(writer, input.EffectDigest);
        });
    }

    public static CovenantDigest FamilyReinitializeApplyRequest(FamilyReinitializeApplyRequestDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireGuid(input.OperationId, nameof(input.OperationId));
        ValidateDigestFields(input.InspectedCatalogFingerprint, input.DatabaseFileIdentityDigest, input.EffectDigest);

        return Hash(CovenantDomainTag.FamilyReinitializeApplyRequest, writer =>
        {
            writer.WriteGuid(input.OperationId);
            WriteDigest(writer, input.InspectedCatalogFingerprint);
            WriteDigest(writer, input.DatabaseFileIdentityDigest);
            WriteDigest(writer, input.EffectDigest);
        });
    }

    public static CovenantDigest FinalReceipt(FinalReceiptDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDigestFields(input.SnapshotDigest, input.PlanDigest, input.AttemptChainDigest, input.CommittedLineageHeadDigest, input.CommittedBranchChainDigest, input.FinalSensitivityDigest, input.DisclosureChainDigest);
        RequireGuid(input.CommittedBranchId, nameof(input.CommittedBranchId));
        uint outcome = Code(input.FinalOutcome, nameof(input.FinalOutcome));

        if (input.MutationCount > CovenantLimits.MaxStagedMutationsPerTurn)
        {
            throw new ArgumentOutOfRangeException(nameof(input.MutationCount));
        }

        return Hash(CovenantDomainTag.Receipt, writer =>
        {
            WriteDigest(writer, input.SnapshotDigest);
            WriteDigest(writer, input.PlanDigest);
            writer.WriteUInt64(input.DispatchedAdmissionCount);
            WriteDigest(writer, input.AttemptChainDigest);
            writer.WriteGuid(input.CommittedBranchId);
            writer.WriteUInt64(input.CommittedBranchOrdinal);
            WriteDigest(writer, input.CommittedLineageHeadDigest);
            WriteDigest(writer, input.CommittedBranchChainDigest);
            WriteDigest(writer, input.FinalSensitivityDigest);
            writer.WriteUInt64(input.ExternalDisclosureCount);
            WriteDigest(writer, input.DisclosureChainDigest);
            writer.WriteUInt64(input.ConfirmedTokens);
            writer.WriteUInt64(input.ProposedTokens);
            writer.WriteUInt32(input.MutationCount);
            writer.WriteUInt32(outcome);
        });
    }

    public static CovenantDigest TurnAggregate(TurnAggregateDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDigestFields(input.FinalReceiptDigest, input.AttemptChainDigest, input.CommittedLineageHeadDigest, input.FinalSensitivityDigest, input.DisclosureChainDigest);
        RequireGuid(input.CommittedBranchId, nameof(input.CommittedBranchId));
        uint outcome = Code(input.FinalOutcome, nameof(input.FinalOutcome));

        if (input.MutationCount > CovenantLimits.MaxStagedMutationsPerTurn)
        {
            throw new ArgumentOutOfRangeException(nameof(input.MutationCount));
        }

        return Hash(CovenantDomainTag.TurnAggregate, writer =>
        {
            WriteDigest(writer, input.FinalReceiptDigest);
            writer.WriteUInt64(input.AttemptedAdmissionCount);
            WriteDigest(writer, input.AttemptChainDigest);
            writer.WriteGuid(input.CommittedBranchId);
            WriteDigest(writer, input.CommittedLineageHeadDigest);
            WriteDigest(writer, input.FinalSensitivityDigest);
            writer.WriteUInt64(input.ExternalDisclosureCount);
            WriteDigest(writer, input.DisclosureChainDigest);
            writer.WriteUInt64(input.ConfirmedTokens);
            writer.WriteUInt64(input.ProposedTokens);
            writer.WriteUInt32(input.MutationCount);
            writer.WriteUInt32(outcome);
        });
    }

    public static CovenantDigest CursorFilter(CursorFilterDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        uint endpoint = Code(input.Endpoint, nameof(input.Endpoint));
        uint scope = Code(input.ScopeSelection, nameof(input.ScopeSelection));
        uint lifecycle = Code(input.Lifecycle, nameof(input.Lifecycle));
        uint sort = Code(input.SortPolicy, nameof(input.SortPolicy));
        RequireOptionalGuid(input.CampaignId, nameof(input.CampaignId));
        RequireOptionalGuid(input.EvaluationCampaignId, nameof(input.EvaluationCampaignId));
        ValidateOptionalCode(input.Lane, nameof(input.Lane));
        RequireOptionalDigest(input.QueryDigest, nameof(input.QueryDigest));

        if (input.PageSize is < 1 or > CovenantLimits.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(input.PageSize));
        }

        return Hash(CovenantDomainTag.CursorFilter, writer =>
        {
            writer.WriteUInt32(endpoint);
            writer.WriteUInt32(scope);
            WriteOptionalGuid(writer, input.CampaignId);
            WriteOptionalGuid(writer, input.EvaluationCampaignId);
            WriteOptionalEnum(writer, input.Lane);
            writer.WriteUInt32(lifecycle);
            WriteOptionalDigest(writer, input.QueryDigest);
            writer.WriteUInt32(input.PageSize);
            writer.WriteUInt32(sort);
        });
    }

    /// <summary>
    /// The ordered dependent-head vector a mutation preflight binds.
    /// </summary>
    /// <remarks>
    /// Domain-separated from every other digest because it is the one value that summarizes state
    /// the caller did not name: the Campaign heads a Global mutation would affect. Reusing another
    /// tag here would let a vector digest and, say, a snapshot digest collide across purposes.
    ///
    /// <para>The caller supplies the vector already in canonical order. Sorting here would hide an
    /// unordered scan, and an unordered scan is exactly the bug this digest exists to catch.</para>
    /// </remarks>
    public static CovenantDigest DependentHeadVector(DependentHeadVectorDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        uint scope = Code(input.Scope, nameof(input.Scope));
        uint lane = Code(input.Lane, nameof(input.Lane));
        uint operation = Code(input.Operation, nameof(input.Operation));
        RequireOptionalGuid(input.CampaignId, nameof(input.CampaignId));
        RequireInitialized(input.Entries, nameof(input.Entries));

        return Hash(CovenantDomainTag.DependentHeadVector, writer =>
        {
            writer.WriteUInt32(scope);
            WriteOptionalGuid(writer, input.CampaignId);
            writer.WriteUtf8(input.NormalizedKey);
            writer.WriteUInt32(lane);
            writer.WriteUInt32(operation);
            writer.WriteUInt64(input.KeyEpoch);
            writer.WriteUInt64(input.KeyReclamationEpoch);
            writer.WriteUInt64(input.CampaignRegistryEpoch);
            writer.WriteCount((ulong)input.Entries.Length);

            foreach (DependentHeadDigestInput entry in input.Entries)
            {
                writer.WriteGuid(entry.CampaignId);
                writer.WriteByte(entry.HasConfirmedHead ? (byte)1 : (byte)0);
                writer.WriteByte(entry.HasProposedHead ? (byte)1 : (byte)0);
                writer.WriteUInt64(entry.ConfirmedLaneRevision);
                writer.WriteUInt64(entry.ProposedLaneRevision);
            }
        });
    }

    private static CovenantDigest Hash(CovenantDomainTag domainTag, Action<CovenantCanonicalHashWriter> write)
    {
        using CovenantCanonicalHashWriter writer = new();

        writer.WriteDomainTag(domainTag);
        write(writer);

        return writer.FinalizeDigest();
    }

    private static uint Code<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        try
        {
            return CovenantPolicyV1Manifest.GetCode(value);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(parameterName, exception);
        }
    }

    private static void WriteCode<TEnum>(CovenantCanonicalHashWriter writer, TEnum value)
        where TEnum : struct, Enum =>
        writer.WriteUInt32(Code(value, nameof(value)));

    private static byte ToByte(bool value) =>
        value ? (byte)1 : (byte)0;

    private static void RequireGuid(Guid value, string parameterName) =>
        CovenantValidation.RequireNonEmpty(value, parameterName);

    private static void RequireOptionalGuid(Guid? value, string parameterName)
    {
        if (value is { } present)
        {
            RequireGuid(present, parameterName);
        }
    }

    private static void RequirePositive(long value, string parameterName) =>
        CovenantValidation.RequirePositive(value, parameterName);

    private static CovenantDigest RequireDigest(CovenantDigest value, string parameterName) =>
        CovenantValidation.RequireDigest(value, parameterName);

    private static void RequireOptionalDigest(CovenantDigest? value, string parameterName)
    {
        if (value is { } present)
        {
            RequireDigest(present, parameterName);
        }
    }

    private static void ValidateDigestFields(params CovenantDigest[] values)
    {
        foreach (CovenantDigest value in values)
        {
            RequireDigest(value, nameof(values));
        }
    }

    private static void ValidateDigests(ImmutableArray<CovenantDigest> values, string parameterName)
    {
        foreach (CovenantDigest value in values)
        {
            RequireDigest(value, parameterName);
        }
    }

    private static void RequireInitialized<T>(ImmutableArray<T> values, string parameterName)
    {
        if (values.IsDefault)
        {
            throw new ArgumentException("The canonical vector must be initialized.", parameterName);
        }
    }

    private static void RequireFixed32(ImmutableArray<byte> value, string parameterName)
    {
        if (value.IsDefault || value.Length != CovenantLimits.DigestBytes)
        {
            throw new ArgumentException("The fixed Covenant value must contain exactly 32 bytes.", parameterName);
        }
    }

    private static string RequireKey(CovenantKey key, string parameterName)
    {
        try
        {
            return new CovenantKey(key.Value).Value;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException("The Covenant key is uninitialized or invalid.", parameterName, exception);
        }
    }

    private static string RequireText(string value, string parameterName) =>
        RequireTextValue(value, parameterName, allowEmpty: false);

    private static string RequireTextValue(string value, string parameterName, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (!allowEmpty && value.Length == 0)
        {
            throw new ArgumentException("The canonical text identity cannot be empty.", parameterName);
        }

        _ = StrictUtf8.GetByteCount(value);

        return value;
    }

    private static void ValidateOptionalText(string? value, string parameterName, bool allowEmpty)
    {
        if (value is not null)
        {
            _ = RequireTextValue(value, parameterName, allowEmpty);
        }
    }

    private static void ValidateOptionalCode<TEnum>(TEnum? value, string parameterName)
        where TEnum : struct, Enum
    {
        if (value is { } present)
        {
            _ = Code(present, parameterName);
        }
    }

    private static void ValidateFinite(double? value, string parameterName)
    {
        if (value is { } present)
        {
            ValidateFinite(present, parameterName);
        }
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateScopeCampaign(CovenantScope scope, Guid? campaignId, string parameterName)
    {
        _ = Code(scope, parameterName);

        if ((scope == CovenantScope.Campaign) != campaignId.HasValue)
        {
            throw new ArgumentException("Campaign scope and Campaign identity must be present together.", parameterName);
        }

        RequireOptionalGuid(campaignId, parameterName);
    }

    internal static void ValidateDisclosureStateShape(
        CovenantDisclosureCountKind countKind,
        bool everOccurred,
        ulong count,
        long maximumTimestamp,
        ReadOnlySpan<byte> bloom)
    {
        bool zeroBloom = true;

        foreach (byte value in bloom)
        {
            zeroBloom &= value == 0;
        }

        if (!everOccurred)
        {
            if (countKind != CovenantDisclosureCountKind.Exact || count != 0 || maximumTimestamp != 0 || !zeroBloom)
            {
                throw new ArgumentException("The sole empty disclosure state is Exact,false,0,0,zeroBloom.");
            }

            return;
        }

        if (count == 0 || maximumTimestamp <= 0 || zeroBloom)
        {
            throw new ArgumentException("A nonempty disclosure state requires count, a positive timestamp, and Bloom evidence.");
        }
    }

    private static MaterializationSourceDigestInput ValidateAndSortSource(MaterializationSourceDigestInput source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireGuid(source.AttachmentId, nameof(source.AttachmentId));
        RequireGuid(source.AttachmentVersionId, nameof(source.AttachmentVersionId));
        _ = RequireText(source.LogicalKey, nameof(source.LogicalKey));
        RequireDigest(source.ContentDigest, nameof(source.ContentDigest));
        _ = Code(source.SourceRange, nameof(source.SourceRange));

        if (source.SourceRange == CovenantMaterializationSourceRange.WholeSource)
        {
            if (source.SourceStart.HasValue || source.SourceEnd.HasValue)
            {
                throw new ArgumentException("Whole-source materialization cannot carry range bounds.", nameof(source));
            }
        }
        else if (!source.SourceStart.HasValue || !source.SourceEnd.HasValue || source.SourceEnd < source.SourceStart)
        {
            throw new ArgumentException("Ranged materialization requires an ordered start and end.", nameof(source));
        }

        RequireInitialized(source.Occurrences, nameof(source.Occurrences));
        MaterializationOccurrenceDigestInput[] occurrences = source.Occurrences.ToArray();

        foreach (MaterializationOccurrenceDigestInput occurrence in occurrences)
        {
            ArgumentNullException.ThrowIfNull(occurrence);
            _ = Code(occurrence.Container, nameof(occurrence.Container));
            _ = Code(occurrence.Occurrence, nameof(occurrence.Occurrence));

            if (occurrence.Length == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(occurrence.Length));
            }

            if (occurrence.Container == CovenantMaterializationContainer.SystemPrompt)
            {
                if (occurrence.MessageIndex.HasValue || occurrence.ContentPartIndex.HasValue)
                {
                    throw new ArgumentException("System-prompt occurrences cannot carry message coordinates.", nameof(source));
                }
            }
            else if (!occurrence.MessageIndex.HasValue || !occurrence.ContentPartIndex.HasValue)
            {
                throw new ArgumentException("Message-part occurrences require message and part coordinates.", nameof(source));
            }

            if ((occurrence.Occurrence == CovenantMaterializationOccurrence.Utf16TextRange) != occurrence.Utf16Start.HasValue)
            {
                throw new ArgumentException("UTF-16 occurrence kind and start offset must be present together.", nameof(source));
            }
        }

        Array.Sort(occurrences, MaterializationOccurrenceComparer.Instance);

        for (int index = 1; index < occurrences.Length; index++)
        {
            if (MaterializationOccurrenceComparer.Instance.Compare(occurrences[index - 1], occurrences[index]) == 0)
            {
                throw new ArgumentException("Materialization occurrence coordinates must be distinct.", nameof(source));
            }
        }

        return source with { Occurrences = [.. occurrences] };
    }

    private static void ValidateMessage(ProviderMessageDigestInput message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _ = Code(message.Role, nameof(message.Role));
        ValidateOptionalText(message.MessageId, nameof(message.MessageId), allowEmpty: false);
        ValidateOptionalText(message.Name, nameof(message.Name), allowEmpty: false);
        RequireInitialized(message.ContentParts, nameof(message.ContentParts));

        foreach (ProviderContentPartDigestInput part in message.ContentParts)
        {
            ArgumentNullException.ThrowIfNull(part);
            _ = Code(part.Kind, nameof(part.Kind));

            switch (part.Kind)
            {
                case CovenantProviderContentPart.Text:
                    _ = RequireTextValue(part.TextValue!, nameof(part.TextValue), allowEmpty: true);
                    break;
                case CovenantProviderContentPart.Binary:
                    _ = RequireText(part.MediaType!, nameof(part.MediaType));
                    ValidateOptionalText(part.Name, nameof(part.Name), allowEmpty: false);
                    ValidateOptionalCode(part.ImageDetail, nameof(part.ImageDetail));
                    RequireInitialized(part.Bytes, nameof(part.Bytes));
                    break;
                case CovenantProviderContentPart.ToolCall:
                    _ = RequireText(part.ToolCallId!, nameof(part.ToolCallId));
                    _ = RequireText(part.Name!, nameof(part.Name));
                    RequireInitialized(part.Bytes, nameof(part.Bytes));
                    break;
                case CovenantProviderContentPart.ToolResult:
                    _ = RequireText(part.ToolCallId!, nameof(part.ToolCallId));
                    RequireInitialized(part.Bytes, nameof(part.Bytes));
                    break;
                case CovenantProviderContentPart.Json:
                    RequireInitialized(part.Bytes, nameof(part.Bytes));
                    break;
                case CovenantProviderContentPart.Uri:
                    _ = RequireText(part.NormalizedUri!, nameof(part.NormalizedUri));
                    ValidateOptionalText(part.MediaType, nameof(part.MediaType), allowEmpty: false);
                    ValidateOptionalCode(part.ImageDetail, nameof(part.ImageDetail));
                    break;
                case CovenantProviderContentPart.TextReasoning:
                    _ = RequireTextValue(part.TextValue!, nameof(part.TextValue), allowEmpty: true);

                    if (!part.ProtectedData.IsDefault)
                    {
                        RequireInitialized(part.ProtectedData, nameof(part.ProtectedData));
                    }

                    break;
            }
        }
    }

    private static void WriteMessage(CovenantCanonicalHashWriter writer, ProviderMessageDigestInput message)
    {
        WriteCode(writer, message.Role);
        WriteOptionalText(writer, message.MessageId);
        WriteOptionalText(writer, message.Name);
        writer.WriteCount((ulong)message.ContentParts.Length);

        foreach (ProviderContentPartDigestInput part in message.ContentParts)
        {
            WriteCode(writer, part.Kind);

            switch (part.Kind)
            {
                case CovenantProviderContentPart.Text:
                    writer.WriteUtf8(part.TextValue!);
                    break;
                case CovenantProviderContentPart.Binary:
                    writer.WriteUtf8(part.MediaType!);
                    WriteOptionalText(writer, part.Name);
                    WriteOptionalEnum(writer, part.ImageDetail);
                    writer.WriteBytes(part.Bytes.AsSpan());
                    break;
                case CovenantProviderContentPart.ToolCall:
                    writer.WriteUtf8(part.ToolCallId!);
                    writer.WriteUtf8(part.Name!);
                    writer.WriteBytes(part.Bytes.AsSpan());
                    break;
                case CovenantProviderContentPart.ToolResult:
                    writer.WriteUtf8(part.ToolCallId!);
                    writer.WriteBytes(part.Bytes.AsSpan());
                    break;
                case CovenantProviderContentPart.Json:
                    writer.WriteBytes(part.Bytes.AsSpan());
                    break;
                case CovenantProviderContentPart.Uri:
                    writer.WriteUtf8(part.NormalizedUri!);
                    WriteOptionalText(writer, part.MediaType);
                    WriteOptionalEnum(writer, part.ImageDetail);
                    break;
                case CovenantProviderContentPart.TextReasoning:
                    writer.WriteUtf8(part.TextValue!);
                    writer.WriteByte(part.ProtectedData.IsDefault ? (byte)0 : (byte)1);

                    if (!part.ProtectedData.IsDefault)
                    {
                        writer.WriteBytes(part.ProtectedData.AsSpan());
                    }

                    break;
            }
        }
    }

    private static void WriteDigest(CovenantCanonicalHashWriter writer, CovenantDigest digest) =>
        writer.WriteFixed32(digest.Bytes);

    private static void WriteOptionalDigest(CovenantCanonicalHashWriter writer, CovenantDigest? digest)
    {
        writer.WriteByte(digest.HasValue ? (byte)1 : (byte)0);

        if (digest is { } present)
        {
            WriteDigest(writer, present);
        }
    }

    private static void WriteOptionalGuid(CovenantCanonicalHashWriter writer, Guid? value)
    {
        writer.WriteByte(value.HasValue ? (byte)1 : (byte)0);

        if (value is { } present)
        {
            writer.WriteGuid(present);
        }
    }

    private static void WriteOptionalUInt64(CovenantCanonicalHashWriter writer, ulong? value)
    {
        writer.WriteByte(value.HasValue ? (byte)1 : (byte)0);

        if (value is { } present)
        {
            writer.WriteUInt64(present);
        }
    }

    private static void WriteOptionalUInt32(CovenantCanonicalHashWriter writer, uint? value)
    {
        writer.WriteByte(value.HasValue ? (byte)1 : (byte)0);

        if (value is { } present)
        {
            writer.WriteUInt32(present);
        }
    }

    private static void WriteOptionalInt64(CovenantCanonicalHashWriter writer, long? value)
    {
        writer.WriteByte(value.HasValue ? (byte)1 : (byte)0);

        if (value is { } present)
        {
            writer.WriteInt64(present);
        }
    }

    private static void WriteOptionalBinary64(CovenantCanonicalHashWriter writer, double? value)
    {
        writer.WriteByte(value.HasValue ? (byte)1 : (byte)0);

        if (value is { } present)
        {
            writer.WriteBinary64(present);
        }
    }

    private static void WriteOptionalText(CovenantCanonicalHashWriter writer, string? value)
    {
        writer.WriteByte(value is null ? (byte)0 : (byte)1);

        if (value is not null)
        {
            writer.WriteUtf8(value);
        }
    }

    private static void WriteOptionalEnum<TEnum>(CovenantCanonicalHashWriter writer, TEnum? value)
        where TEnum : struct, Enum
    {
        writer.WriteByte(value.HasValue ? (byte)1 : (byte)0);

        if (value is { } present)
        {
            WriteCode(writer, present);
        }
    }

    private sealed class RawGuidComparer : IComparer<Guid>
    {
        public static RawGuidComparer Instance { get; } = new();

        public int Compare(Guid left, Guid right)
        {
            Span<byte> leftBytes = stackalloc byte[16];
            Span<byte> rightBytes = stackalloc byte[16];

            left.TryWriteBytes(leftBytes, bigEndian: true, out _);
            right.TryWriteBytes(rightBytes, bigEndian: true, out _);

            return leftBytes.SequenceCompareTo(rightBytes);
        }
    }

    private sealed class SectionItemComparer : IComparer<SectionItemDigestInput>
    {
        public static SectionItemComparer Instance { get; } = new();

        public int Compare(SectionItemDigestInput? left, SectionItemDigestInput? right)
        {
            int key = string.CompareOrdinal(left!.NormalizedKey.Value, right!.NormalizedKey.Value);

            return key != 0 ? key : RawGuidComparer.Instance.Compare(left.EntryId, right.EntryId);
        }
    }

    private sealed class MaterializationSourceComparer : IComparer<MaterializationSourceDigestInput>
    {
        public static MaterializationSourceComparer Instance { get; } = new();

        public int Compare(MaterializationSourceDigestInput? left, MaterializationSourceDigestInput? right)
        {
            int result = RawGuidComparer.Instance.Compare(left!.AttachmentId, right!.AttachmentId);

            if (result == 0)
            {
                result = RawGuidComparer.Instance.Compare(left.AttachmentVersionId, right.AttachmentVersionId);
            }

            if (result == 0)
            {
                result = CompareUtf8(left.LogicalKey, right.LogicalKey);
            }

            if (result == 0)
            {
                result = Code(left.SourceRange, nameof(left.SourceRange)).CompareTo(Code(right.SourceRange, nameof(right.SourceRange)));
            }

            if (result == 0)
            {
                result = CompareNullable(left.SourceStart, right.SourceStart);
            }

            return result != 0 ? result : CompareNullable(left.SourceEnd, right.SourceEnd);
        }
    }

    private sealed class MaterializationOccurrenceComparer : IComparer<MaterializationOccurrenceDigestInput>
    {
        public static MaterializationOccurrenceComparer Instance { get; } = new();

        public int Compare(MaterializationOccurrenceDigestInput? left, MaterializationOccurrenceDigestInput? right)
        {
            int result = Code(left!.Container, nameof(left.Container)).CompareTo(Code(right!.Container, nameof(right.Container)));

            if (result == 0)
            {
                result = CompareNullable(left.MessageIndex, right.MessageIndex);
            }

            if (result == 0)
            {
                result = CompareNullable(left.ContentPartIndex, right.ContentPartIndex);
            }

            if (result == 0)
            {
                result = CompareNullable(left.Utf16Start, right.Utf16Start);
            }

            return result != 0
                ? result
                : Code(left.Occurrence, nameof(left.Occurrence)).CompareTo(Code(right.Occurrence, nameof(right.Occurrence)));
        }
    }

    private static int CompareNullable(uint? left, uint? right) =>
        left.HasValue == right.HasValue
            ? left.GetValueOrDefault().CompareTo(right.GetValueOrDefault())
            : left.HasValue ? 1 : -1;

    private static int CompareUtf8(string left, string right)
    {
        byte[] leftBytes = StrictUtf8.GetBytes(left);
        byte[] rightBytes = StrictUtf8.GetBytes(right);

        return leftBytes.AsSpan().SequenceCompareTo(rightBytes);
    }
}
