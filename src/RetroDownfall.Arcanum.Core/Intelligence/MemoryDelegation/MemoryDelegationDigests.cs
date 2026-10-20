using System.Text;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;

/// <summary>Canonical identity of the explicit operator selection, never an executable capability.</summary>
public static class MemoryDelegationDigests
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool IsValid(MemoryDelegationGrant grant)
    {
        try
        {
            if (grant is null || grant.Scope is null || grant.Memory is null || grant.Purpose != MemoryDelegationPurpose.DelegatedTask
                || !Enum.IsDefined(grant.Scope.Kind)
                || (grant.Scope.Kind == MemoryEvaluationScopeKind.Global ? grant.Scope.CampaignId is not null : grant.Scope.CampaignId is null || grant.Scope.CampaignId == Guid.Empty)
                || grant.Memory.Covenant.IsDefault || grant.Memory.Saga.IsDefault || grant.Memory.Lexicon.IsDefault)
            {
                return false;
            }

            int count = checked(grant.Memory.Covenant.Length + grant.Memory.Saga.Length + grant.Memory.Lexicon.Length);

            if (count is < MemoryDelegationLimits.MinTargets or > MemoryDelegationLimits.MaxTargets)
            {
                return false;
            }

            HashSet<(MemoryEvaluationSource Store, string Subject, string Version)> identities = [];

            foreach (MemoryEvaluationCovenantTarget source in grant.Memory.Covenant)
            {
                if (source is null || source.EntryId == Guid.Empty || source.VersionId == Guid.Empty || !source.FragmentDigest.IsValid
                    || !identities.Add((MemoryEvaluationSource.Covenant, source.EntryId.ToString("D"), source.VersionId.ToString("D"))))
                {
                    return false;
                }
            }

            foreach (MemoryEvaluationSagaTarget source in grant.Memory.Saga)
            {
                if (source is null || !Identifier(source.MemoryId) || !Identifier(source.VersionId)
                    || source.HashFormat != AnnalContentHashFormat.LegacyStoreDigest || !source.ContentDigest.IsValid
                    || !identities.Add((MemoryEvaluationSource.Saga, source.MemoryId, source.VersionId)))
                {
                    return false;
                }
            }

            foreach (MemoryEvaluationLexiconTarget source in grant.Memory.Lexicon)
            {
                if (source is null || source.EntryId == Guid.Empty || !Identifier(source.VersionId) || !Enum.IsDefined(source.HashFormat)
                    || !source.ContentDigest.IsValid || !identities.Add((MemoryEvaluationSource.Lexicon, source.EntryId.ToString("D"), source.VersionId)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    public static CovenantDigest Grant(MemoryDelegationGrant grant)
    {
        if (!IsValid(grant))
        {
            throw new ArgumentException("The memory delegation grant violates policy v1.", nameof(grant));
        }

        using CovenantCanonicalHashWriter writer = new();

        writer.WriteUtf8("arcanum.memory-delegation.grant.v1");

        writer.WriteInt32(MemoryDelegationLimits.PolicyVersion);

        writer.WriteInt32((int)grant.Purpose);

        writer.WriteInt32((int)grant.Scope.Kind);

        writer.WriteOptional(grant.Scope.CampaignId, static (target, id) => target.WriteGuid(id));

        writer.WriteCount((ulong)grant.Memory.Covenant.Length);

        foreach (MemoryEvaluationCovenantTarget source in grant.Memory.Covenant.OrderBy(source => source.EntryId).ThenBy(source => source.VersionId))
        {
            writer.WriteGuid(source.EntryId);

            writer.WriteGuid(source.VersionId);

            writer.WriteFixed32(source.FragmentDigest.Span);
        }

        writer.WriteCount((ulong)grant.Memory.Saga.Length);

        foreach (MemoryEvaluationSagaTarget source in grant.Memory.Saga.OrderBy(source => source.MemoryId, StringComparer.Ordinal).ThenBy(source => source.VersionId, StringComparer.Ordinal))
        {
            writer.WriteUtf8(source.MemoryId);

            writer.WriteUtf8(source.VersionId);

            writer.WriteInt32((int)source.HashFormat);

            writer.WriteFixed32(source.ContentDigest.Span);
        }

        writer.WriteCount((ulong)grant.Memory.Lexicon.Length);

        foreach (MemoryEvaluationLexiconTarget source in grant.Memory.Lexicon.OrderBy(source => source.EntryId).ThenBy(source => source.VersionId, StringComparer.Ordinal))
        {
            writer.WriteGuid(source.EntryId);

            writer.WriteUtf8(source.VersionId);

            writer.WriteInt32((int)source.HashFormat);

            writer.WriteFixed32(source.ContentDigest.Span);
        }

        return writer.FinalizeDigest();
    }

    /// <summary>Strict canonical byte length of all retained source proof metadata, excluding selected text.</summary>
    public static long ProofMetadataBytes(MemoryEvaluationSourceSnapshot snapshot)
    {
        using CovenantCanonicalHashWriter writer = new();

        writer.WriteUtf8("arcanum.memory-delegation.source-proof.v1");

        writer.WriteInt32(MemoryDelegationLimits.PolicyVersion);

        MemoryEvaluationDigests.WriteSourceManifest(writer, snapshot.Coordinates, snapshot.Dependencies);

        // Each selected fragment retains its source index and prompt placement in addition
        // to its text. Both have fixed-width canonical encodings, known before hydration.
        writer.WriteCount((ulong)snapshot.Coordinates.Count(source => source.Role == MemoryEvaluationCoordinateRole.Intervention));

        for (int index = 0; index < snapshot.Coordinates.Length; index++)
        {
            if (snapshot.Coordinates[index].Role == MemoryEvaluationCoordinateRole.Intervention)
            {
                writer.WriteInt32(index);

                writer.WriteInt32((int)MemoryEvaluationPromptPart.System);
            }
        }

        writer.WriteFixed32(snapshot.ManifestDigest.Span);

        writer.WriteOptional(snapshot.AsOfUtc, static (target, time) => target.WriteInt64(time.UtcTicks));

        writer.WriteOptionalReference(snapshot.Fence, static (target, fence) =>
        {
            target.WriteGuid(fence.DatasetGeneration);

            target.WriteInt64(fence.AdmissionGeneration);

            target.WriteInt64(fence.RuntimeAuthorityGeneration);

            target.WriteInt64(fence.CampaignAvailabilityGeneration);

            target.WriteFixed32(fence.SourceStateDigest.Span);
        });

        writer.WriteOptionalReference(snapshot.Provenance, static (target, provenance) =>
        {
            target.WriteInt32((int)provenance.Mode);

            target.WriteCount((ulong)provenance.ExactGenerationIds.Length);

            foreach (Guid id in provenance.ExactGenerationIds)
            {
                target.WriteGuid(id);
            }

            target.WriteBytes(provenance.BloomBits.AsSpan());
        });

        return writer.BytesWritten;
    }

    private static bool Identifier(string value) => !string.IsNullOrWhiteSpace(value)
        && Utf8.GetByteCount(value) <= MemoryDelegationLimits.EvidenceBytes && !value.Any(char.IsControl);
}
