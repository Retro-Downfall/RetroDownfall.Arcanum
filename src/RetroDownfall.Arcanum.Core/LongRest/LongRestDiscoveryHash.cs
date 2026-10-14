using System.Buffers.Binary;

using System.Collections.Immutable;

using System.Security.Cryptography;

using System.Runtime.InteropServices;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// Discovery-v1 grammar: domain plus NUL, signed big-endian numbers, strict UTF-8 strings with
/// unsigned big-endian byte lengths, one-byte nullable presence tags and raw 32-byte digests.
/// Source pages sort exact identities and bind each unchanged v1 policy identity plus current-head,
/// insertion, mutation, embedding and separately observed sensitivity evidence. No plaintext enters
/// these hashes. Work identities additionally bind every canonical fence and configuration epoch.
/// </summary>
public static class LongRestDiscoveryHash
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Result<CovenantDigest> SourcePage(LongRestSweepScope scope, ImmutableArray<LongRestSourceLeaf> leaves)
    {
        if (!scope.IsInitialized || leaves.IsDefault || leaves.Length > LongRestDiscoveryLimits.SourcePage)
        {
            return Invalid("A source page requires an exact initialized scope and at most 128 leaves.");
        }

        List<(LongRestSourceLeaf Leaf, string PolicyIdentity)> normalized = new(leaves.Length);

        HashSet<string> memories = new(StringComparer.Ordinal);

        HashSet<string> claims = new(StringComparer.Ordinal);

        HashSet<string> versions = new(StringComparer.Ordinal);

        GenerationProvenance? partitionProvenance = null;

        foreach (LongRestSourceLeaf leaf in leaves)
        {
            if (leaf is null || leaf.Snapshot is null || leaf.InsertionSequence < 1 || leaf.MutationEpoch < 0
                || !MatchesScope(scope, leaf.Snapshot)
                || !memories.Add(leaf.Snapshot.MemoryId) || !claims.Add(leaf.Snapshot.ClaimId) || !versions.Add(leaf.Snapshot.VersionId)
                || !ValidSensitivity(scope, leaf)
                || !ValidEmbedding(leaf))
            {
                return Invalid("A source page contains malformed, repeated or cross-partition bindings.");
            }

            Result<string> policy = LongRestPolicy.Identify(new(LongRestTransformationKind.ExactDuplicates, [leaf.Target]), [leaf.Snapshot]);

            if (policy.IsFailure)
            {
                return Result<CovenantDigest>.Failure(policy.Error);
            }

            if (scope.ProtectedPartitionDigest is not null)
            {
                if (partitionProvenance is not null && !partitionProvenance.Equals(leaf.Provenance))
                {
                    return Invalid("A protected source page requires identical nonempty canonical provenance.");
                }

                partitionProvenance = leaf.Provenance;
            }

            normalized.Add((leaf, policy.Value));
        }

        normalized.Sort((left, right) => StringComparer.Ordinal.Compare(left.Leaf.Snapshot.MemoryId, right.Leaf.Snapshot.MemoryId));

        using MemoryStream bytes = Start("Arcanum.LongRest.SourcePage.v1\0");

        Scope(bytes, scope);

        Int32(bytes, normalized.Count);

        foreach ((LongRestSourceLeaf leaf, string policyIdentity) in normalized)
        {
            String(bytes, leaf.Snapshot.MemoryId);

            bytes.Write(Convert.FromHexString(policyIdentity));

            Int64(bytes, leaf.InsertionSequence);

            Int64(bytes, leaf.MutationEpoch);

            Boolean(bytes, leaf.EmbeddingBinding is not null);

            if (leaf.EmbeddingBinding is { } embedding)
            {
                Int32(bytes, (int)embedding.Kind);

                Int32(bytes, embedding.Dimensions);

                Digest(bytes, embedding.ValueDigest);
            }

            Boolean(bytes, leaf.Snapshot.IsCurrent);

            Int32(bytes, (int)leaf.Sensitivity);

            Int32(bytes, (int)leaf.Provenance.Mode);

            Int32(bytes, leaf.Provenance.ExactGenerationIds.Length);

            foreach (Guid generation in leaf.Provenance.ExactGenerationIds)
            {
                String(bytes, generation.ToString("D"));
            }

            Int32(bytes, leaf.Provenance.BloomBits.Length);

            bytes.Write(leaf.Provenance.BloomBits.AsSpan());

            Int32(bytes, leaf.LabelDigests.Length);

            foreach (CovenantDigest label in leaf.LabelDigests.OrderBy(value => value.ToString(), StringComparer.Ordinal))
            {
                Digest(bytes, label);
            }

            if (leaf.CanonicalLabel is { } canonicalLabel)
            {
                String(bytes, canonicalLabel.LabelId.ToString("D"));
            }
        }

        return Hash(bytes);
    }

    /// <summary>Typed child manifest, independent of plaintext and the work's own source digest.</summary>
    public static Result<CovenantDigest> HierarchyFold(LongRestHierarchyFold fold)
    {
        if (fold is null || fold.Work is null || !fold.Work.Scope.IsInitialized || fold.Work.Kind != LongRestWorkKind.HierarchyFold
            || fold.Layer is < 1 or > LongRestDiscoveryLimits.HierarchyLevels || fold.Group < 0 || !fold.MembershipDigest.IsValid
            || fold.Children.IsDefaultOrEmpty || fold.Children.Length > LongRestDiscoveryLimits.HierarchyChildren
            || fold.Children.Any(child => child is null || child.NodeId == Guid.Empty || (child.Layer < 0 || child.Layer >= fold.Layer)
                || child.Group < 0 || !child.MembershipDigest.IsValid || !child.SourceDigest.IsValid
                || child.Provenance is null || child.LabelDigests.IsDefault || child.ExpandedTargets.IsDefault
                || child.ExpandedTargets.Length > LongRestDiscoveryLimits.ExpandedLeaves)
            || fold.Children.Select(child => child.NodeId).Distinct().Count() != fold.Children.Length
            || fold.Children.Select(child => (child.Layer, child.Group)).Distinct().Count() != fold.Children.Length
            || !ValidChildPartitions(fold))
        {
            return Invalid("A fold hash requires one bounded exact child manifest and sensitivity partition.");
        }

        HashSet<string> memories = new(StringComparer.Ordinal);

        HashSet<string> versions = new(StringComparer.Ordinal);

        foreach (LongRestHierarchyChild child in fold.Children)
        {
            foreach (LongRestTarget target in child.ExpandedTargets)
            {
                if (target is null || LongRestPolicy.ValidateRequest(new(LongRestTransformationKind.ExactDuplicates, [target])).IsFailure
                    || !memories.Add(target.MemoryId) || !versions.Add(target.ExpectedVersionId))
                {
                    return Invalid("A fold manifest contains malformed or repeated exact expanded targets.");
                }
            }
        }

        using MemoryStream bytes = Start("Arcanum.LongRest.HierarchyFold.v1\0");

        Scope(bytes, fold.Work.Scope);

        Int32(bytes, fold.Layer);

        Int64(bytes, fold.Group);

        Digest(bytes, fold.MembershipDigest);

        Int32(bytes, fold.Children.Length);

        foreach (LongRestHierarchyChild child in fold.Children.OrderBy(child => child.Layer).ThenBy(child => child.Group).ThenBy(child => child.NodeId.ToString("D"), StringComparer.Ordinal))
        {
            String(bytes, child.NodeId.ToString("D"));

            Int32(bytes, child.Layer);

            Int64(bytes, child.Group);

            Digest(bytes, child.MembershipDigest);

            Digest(bytes, child.SourceDigest);

            Int32(bytes, (int)child.Sensitivity);

            Int32(bytes, (int)child.Provenance.Mode);

            Int32(bytes, child.Provenance.ExactGenerationIds.Length);

            foreach (Guid generation in child.Provenance.ExactGenerationIds)
            {
                String(bytes, generation.ToString("D"));
            }

            Int32(bytes, child.Provenance.BloomBits.Length);

            bytes.Write(child.Provenance.BloomBits.AsSpan());

            Int32(bytes, child.LabelDigests.Length);

            foreach (CovenantDigest label in child.LabelDigests.OrderBy(value => value.ToString(), StringComparer.Ordinal))
            {
                Digest(bytes, label);
            }

            Int32(bytes, child.ExpandedTargets.Length);

            foreach (LongRestTarget target in child.ExpandedTargets.OrderBy(target => target.MemoryId, StringComparer.Ordinal).ThenBy(target => target.ExpectedVersionId, StringComparer.Ordinal))
            {
                String(bytes, target.MemoryId);

                String(bytes, target.ExpectedVersionId);

                bytes.Write(Convert.FromHexString(target.ExpectedContentHash));
            }
        }

        return Hash(bytes);
    }

    public static Result<CovenantDigest> Work(LongRestWorkIdentity work)
    {
        if (work is null || work.GenerationId == Guid.Empty || work.OperationId == Guid.Empty
            || !ValidIdentity(work.ItemId) || work.Kind is < LongRestWorkKind.SourcePage or > LongRestWorkKind.Anchor
            || !work.Scope.IsInitialized || !work.SourceDigest.IsValid || !work.RecipeDigest.IsValid
            || !ValidFence(work.Fence, work.Scope))
        {
            return Invalid("A work identity requires exact valid source, recipe, scope and generation fences.");
        }

        using MemoryStream bytes = Start("Arcanum.LongRest.Work.v1\0");

        String(bytes, work.GenerationId.ToString("D"));

        String(bytes, work.OperationId.ToString("D"));

        String(bytes, work.ItemId);

        Int32(bytes, (int)work.Kind);

        Scope(bytes, work.Scope);

        CanonicalCampaignContext context = work.Fence.CanonicalScope;

        Int32(bytes, (int)context.Binding.Kind);

        NullableInt64(bytes, context.CampaignAvailabilityGeneration);

        Boolean(bytes, context.PathIdentityPolicyVersion.HasValue);

        if (context.PathIdentityPolicyVersion is { } policyVersion)
        {
            UInt32(bytes, policyVersion);
        }

        NullableInt64(bytes, context.PathIdentityRevision);

        NullableDigest(bytes, context.RootIdentityDigest);

        Int64(bytes, work.Fence.InsertionHighWatermark);

        String(bytes, work.Fence.DatasetGeneration.ToString("D"));

        Int64(bytes, work.Fence.AdmissionGeneration);

        Int64(bytes, work.Fence.RuntimeAuthorityGeneration);

        Int64(bytes, work.Fence.CapabilityGeneration);

        String(bytes, work.Fence.Configuration.BootId.ToString("D"));

        Int64(bytes, work.Fence.Configuration.Epoch);

        Digest(bytes, work.Fence.Configuration.ValueDigest);

        Digest(bytes, work.SourceDigest);

        Digest(bytes, work.RecipeDigest);

        return Hash(bytes);
    }

    public static Result<CovenantDigest> Recipe(LongRestRecipeIdentity recipe)
    {
        if (recipe is null || recipe.SummaryRecipeVersion < 1 || recipe.ClusteringRecipeVersion < 1
            || !ValidIdentity(recipe.SummaryProvider) || !ValidIdentity(recipe.SummaryModel)
            || !ValidIdentity(recipe.EmbeddingProvider) || !ValidIdentity(recipe.EmbeddingModel)
            || !recipe.SummaryDestinationDigest.IsValid || !recipe.EmbeddingDestinationDigest.IsValid
            || recipe.EmbeddingDimensions < 1
            || recipe.SourcePageSize != LongRestDiscoveryLimits.SourcePage
            || recipe.ProviderInputUtf8Bytes is < LongRestPromptLayout.MinimumInputUtf8Bytes or > LongRestDiscoveryLimits.ProviderInputUtf8Bytes
            || recipe.SummaryUtf8Bytes != LongRestDiscoveryLimits.SummaryUtf8Bytes
            || recipe.TargetLimit != LongRestDiscoveryLimits.Targets
            || recipe.HierarchyChildLimit != LongRestDiscoveryLimits.HierarchyChildren
            || recipe.HierarchyLevelLimit != LongRestDiscoveryLimits.HierarchyLevels
            || recipe.LexicalTokenLimit != LongRestDiscoveryLimits.LexicalTokens
            || recipe.LexicalTokenUtf8Bytes != LongRestDiscoveryLimits.LexicalTokenUtf8Bytes
            || recipe.LexicalTopK != LongRestDiscoveryLimits.LexicalTopK
            || recipe.VectorTopK != LongRestDiscoveryLimits.VectorTopK
            || recipe.SummaryTopK != LongRestDiscoveryLimits.SummaryTopK
            || recipe.MinimumVectorCosine != LongRestDiscoveryLimits.MinimumVectorCosine
            || recipe.ExpandedLeafLimit != LongRestDiscoveryLimits.ExpandedLeaves
            || recipe.TraversalFrontierLimit != LongRestDiscoveryLimits.TraversalFrontier
            || recipe.MergedNeighborLimit != LongRestDiscoveryLimits.MergedNeighbors
            || recipe.LexicalMinimumScore != LongRestDiscoveryLimits.LexicalMinimumScore)
        {
            return Invalid("A recipe requires exact configured identities and the fixed code-owned work bounds.");
        }

        using MemoryStream bytes = Start("Arcanum.LongRest.Recipe.v1\0");

        Int32(bytes, recipe.SummaryRecipeVersion);

        Int32(bytes, recipe.ClusteringRecipeVersion);

        String(bytes, recipe.SummaryProvider);

        String(bytes, recipe.SummaryModel);

        Digest(bytes, recipe.SummaryDestinationDigest);

        String(bytes, recipe.EmbeddingProvider);

        String(bytes, recipe.EmbeddingModel);

        Digest(bytes, recipe.EmbeddingDestinationDigest);

        Int32(bytes, recipe.EmbeddingDimensions);

        int[] bounds =
        [
            recipe.SourcePageSize, recipe.ProviderInputUtf8Bytes, recipe.SummaryUtf8Bytes, recipe.TargetLimit,
            recipe.HierarchyChildLimit, recipe.HierarchyLevelLimit, recipe.LexicalTokenLimit, recipe.LexicalTokenUtf8Bytes,
            recipe.LexicalTopK, recipe.VectorTopK, recipe.SummaryTopK, recipe.ExpandedLeafLimit,
            recipe.TraversalFrontierLimit, recipe.MergedNeighborLimit,
        ];

        foreach (int bound in bounds)
        {
            Int32(bytes, bound);
        }

        Int64(bytes, BitConverter.DoubleToInt64Bits(recipe.MinimumVectorCosine));

        Int32(bytes, recipe.LexicalMinimumScore);

        return Hash(bytes);
    }

    internal static bool ValidIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > LongRestPolicy.MaxIdentityBytes)
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(value) <= LongRestPolicy.MaxIdentityBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool ValidFence(LongRestSweepFence fence, LongRestSweepScope scope)
    {
        if (fence is null || fence.Configuration is not { IsValid: true } || fence.DatasetGeneration == Guid.Empty
            || fence.InsertionHighWatermark < 0
            || fence.AdmissionGeneration < 1 || fence.RuntimeAuthorityGeneration < 1 || fence.CapabilityGeneration < 1)
        {
            return false;
        }

        try
        {
            return fence.CanonicalScope.CampaignId == scope.CampaignId
                && fence.CanonicalScope.IsCampaignBound == (scope.Kind == LongRestSweepScopeKind.Campaign);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool MatchesScope(LongRestSweepScope scope, LongRestSnapshot snapshot) =>
        scope.Kind == LongRestSweepScopeKind.Global
            ? snapshot.ScopeKind == SagaMemoryScopeKind.Global && snapshot.CampaignId is null
            : snapshot.ScopeKind == SagaMemoryScopeKind.Campaign && snapshot.CampaignId == scope.CampaignId;

    private static bool ValidSensitivity(LongRestSweepScope scope, LongRestSourceLeaf leaf)
    {
        if (leaf.Provenance is null || leaf.LabelDigests.IsDefault || leaf.LabelDigests.Length > 1
            || leaf.LabelDigests.Any(label => !label.IsValid))
        {
            return false;
        }

        if (scope.ProtectedPartitionDigest is null)
        {
            return leaf.Sensitivity == ContentSensitivity.None && leaf.Snapshot.Sensitivity == ContentSensitivity.None
                && !leaf.Snapshot.HasSensitivityLabel && leaf.Provenance.IsEmpty && leaf.LabelDigests.IsEmpty
                && leaf.CanonicalLabel is null && leaf.SagaContentDigest is null && leaf.SagaRevision is null;
        }

        ArtifactSensitivityLabel? label = leaf.CanonicalLabel;

        return leaf.Sensitivity == ContentSensitivity.CovenantDerived && !leaf.Provenance.IsEmpty
            && leaf.LabelDigests.Length == 1 && label is not null
            && label.ArtifactKind == SensitiveArtifactKind.Saga
            && leaf.Snapshot.MemoryId == label.ArtifactId.ToString("D")
            && label.SessionId == leaf.Snapshot.SourceSessionId && label.CampaignId == scope.CampaignId
            && leaf.SagaRevision == label.ArtifactRevision && leaf.SagaContentDigest == label.ArtifactContentDigest
            && label.Sensitivity == ContentSensitivity.CovenantDerived && label.Provenance.Equals(leaf.Provenance)
            && label.SensitivityDigest == scope.ProtectedPartitionDigest && leaf.LabelDigests[0] == label.LabelDigest;
    }

    private static bool ValidChildPartitions(LongRestHierarchyFold fold)
    {
        GenerationProvenance? partition = null;

        foreach (LongRestHierarchyChild child in fold.Children)
        {
            if (child.LabelDigests.Length > LongRestDiscoveryLimits.Targets
                || child.LabelDigests.Any(label => !label.IsValid)
                || child.LabelDigests.Distinct().Count() != child.LabelDigests.Length)
            {
                return false;
            }

            if (fold.Work.Scope.ProtectedPartitionDigest is not { } protectedPartition)
            {
                if (child.Sensitivity != ContentSensitivity.None || !child.Provenance.IsEmpty || !child.LabelDigests.IsEmpty)
                {
                    return false;
                }

                continue;
            }

            if (child.Sensitivity != ContentSensitivity.CovenantDerived || child.Provenance.IsEmpty || child.LabelDigests.IsEmpty
                || CovenantDigests.Sensitivity(child.Provenance.ToDigestInput(child.Sensitivity)) != protectedPartition
                || (partition is not null && !partition.Equals(child.Provenance)))
            {
                return false;
            }

            partition = child.Provenance;
        }

        return true;
    }

    private static bool ValidEmbedding(LongRestSourceLeaf leaf)
    {
        if (leaf.EmbeddingBinding is not { } binding)
        {
            return leaf.Vector.IsDefaultOrEmpty;
        }

        if (binding.Kind != LongRestSourceEmbeddingKind.ObservedSaga || binding.Dimensions < 1
            || binding.Dimensions > int.MaxValue / sizeof(float) || !binding.ValueDigest.IsValid)
        {
            return false;
        }

        if (leaf.Vector.IsDefaultOrEmpty)
        {
            return true;
        }

        if (leaf.Vector.Length != binding.Dimensions || !leaf.Vector.All(float.IsFinite))
        {
            return false;
        }

        double norm = EmbeddingBlobCodec.NormSquared(leaf.Vector.AsSpan());

        if (!double.IsFinite(norm) || norm <= 0)
        {
            return false;
        }

        CovenantDigest actual = new(SHA256.HashData(MemoryMarshal.AsBytes(leaf.Vector.AsSpan())));

        return actual == binding.ValueDigest;
    }

    private static MemoryStream Start(string domain)
    {
        MemoryStream bytes = new();

        bytes.Write(StrictUtf8.GetBytes(domain));

        Int32(bytes, 1);

        return bytes;
    }

    private static void Scope(MemoryStream bytes, LongRestSweepScope scope)
    {
        Int32(bytes, (int)scope.Kind);

        Boolean(bytes, scope.CampaignId.HasValue);

        if (scope.CampaignId is { } campaign)
        {
            String(bytes, campaign.ToString("D"));
        }

        NullableDigest(bytes, scope.ProtectedPartitionDigest);
    }

    private static void String(MemoryStream bytes, string value)
    {
        byte[] encoded = StrictUtf8.GetBytes(value);

        UInt32(bytes, checked((uint)encoded.Length));

        bytes.Write(encoded);
    }

    private static void Int32(MemoryStream bytes, int value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(encoded, value);

        bytes.Write(encoded);
    }

    private static void UInt32(MemoryStream bytes, uint value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(encoded, value);

        bytes.Write(encoded);
    }

    private static void Int64(MemoryStream bytes, long value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(long)];

        BinaryPrimitives.WriteInt64BigEndian(encoded, value);

        bytes.Write(encoded);
    }

    private static void NullableInt64(MemoryStream bytes, long? value)
    {
        Boolean(bytes, value.HasValue);

        if (value is { } number)
        {
            Int64(bytes, number);
        }
    }

    private static void Digest(MemoryStream bytes, CovenantDigest value) => bytes.Write(value.Span);

    private static void NullableDigest(MemoryStream bytes, CovenantDigest? value)
    {
        Boolean(bytes, value.HasValue);

        if (value is { } digest)
        {
            Digest(bytes, digest);
        }
    }

    private static void Boolean(MemoryStream bytes, bool value) => bytes.WriteByte(value ? (byte)1 : (byte)0);

    private static CovenantDigest Hash(MemoryStream bytes) =>
        new(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));

    private static Result<CovenantDigest> Invalid(string message) => Result<CovenantDigest>.Failure(new Error(ErrorCodes.LongRest.InvalidRequest, message));
}
