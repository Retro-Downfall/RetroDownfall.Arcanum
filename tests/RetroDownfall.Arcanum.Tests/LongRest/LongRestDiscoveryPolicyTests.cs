using System.Collections.Immutable;

using System.Text.Json;

using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestDiscoveryPolicyTests
{
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000001";

    [Fact]
    public void Unsupported_scope_and_numeric_codes_are_refused()
    {
        Assert.True(LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).IsSuccess);

        Assert.True(LongRestSweepScope.Create(new(LongRestSweepScopeKind.Campaign, Guid.Parse("22222222-2222-4222-8222-222222222222"))).IsSuccess);

        Assert.True(LongRestSweepScope.Create(new((LongRestSweepScopeKind)3, null)).IsFailure);

        Assert.True(LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, Guid.NewGuid())).IsFailure);

        Assert.True(LongRestSweepScope.Create(new(LongRestSweepScopeKind.Campaign, null)).IsFailure);

        Assert.True(LongRestSweepScope.Create(new(LongRestSweepScopeKind.Campaign, Guid.Empty)).IsFailure);

        Assert.True(LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null), default(CovenantDigest)).IsFailure);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("1", LongRestTestJsonContext.Default.LongRestSweepScopeKind));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"AllScopes\"", LongRestTestJsonContext.Default.LongRestSweepScopeKind));
    }

    [Fact]
    public void New_persisted_codes_are_accepted_without_widening_unknown_codes()
    {
        Assert.Equal(16u, CovenantPolicyV1Manifest.GetCode((SensitiveArtifactKind)16));

        Assert.Equal(7u, CovenantPolicyV1Manifest.GetCode((CovenantMaintenanceStep)7));

        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantPolicyV1Manifest.GetCode((SensitiveArtifactKind)17));

        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantPolicyV1Manifest.GetCode((CovenantMaintenanceStep)8));
    }

    [Fact]
    public void Permutations_and_opaque_identity_delimiters_have_distinct_literal_hashes()
    {
        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).Value;

        LongRestSourceLeaf first = Leaf("a|b", "c", 1);

        LongRestSourceLeaf last = Leaf("z", "z-version", 2);

        Assert.Equal("524796F2AF9CEA71FB56B5A9DD47DDB92EC728EB76D1BD7FEEB7913CEA13EA20", LongRestDiscoveryHash.SourcePage(scope, [last, first]).Value.ToString());

        Assert.Equal("524796F2AF9CEA71FB56B5A9DD47DDB92EC728EB76D1BD7FEEB7913CEA13EA20", LongRestDiscoveryHash.SourcePage(scope, [first, last]).Value.ToString());

        Assert.Equal("46F68C2A24267D78750258B25BB29FC2A9E1813F1F5BDF70CC682726ADF01D41", LongRestDiscoveryHash.SourcePage(scope, [Leaf("a", "b|c", 1), last]).Value.ToString());
    }

    [Fact]
    public void Source_hash_refuses_unbounded_malformed_and_cross_partition_pages()
    {
        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).Value;

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [Leaf("\ud800", "version", 1)]).IsFailure);

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [Leaf(new string('é', 513), "version", 1)]).IsFailure);

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [Leaf("memory", "version", 1) with { Snapshot = Leaf("memory", "version", 1).Snapshot with { ScopeKind = SagaMemoryScopeKind.LegacyUnresolved } }]).IsFailure);

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [Leaf("memory", "version", 1) with { Sensitivity = ContentSensitivity.CovenantDerived }]).IsFailure);

        ImmutableArray<LongRestSourceLeaf> oversized = [.. Enumerable.Range(0, 129).Select(index => Leaf($"memory-{index}", $"version-{index}", index + 1))];

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, oversized).IsFailure);
    }

    private static LongRestSourceLeaf Leaf(string memory, string version, long sequence)
    {
        DateTimeOffset instant = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        LongRestSnapshot snapshot = new(memory, "claim-" + memory, version, 1, AnnalContentHashFormat.LegacyStoreDigest, Hash, AnnalOrigin.OperatorStated, null, SagaMemoryScopeKind.Global, null, ContentSensitivity.None, instant, null, instant, null, null, true, false, false, false, false, [], []);

        return new(snapshot, sequence, 1, null, ContentSensitivity.None, GenerationProvenance.CreateExact([]), []);
    }

    [Fact]
    public void Protected_source_requires_one_canonical_label_and_identical_provenance()
    {
        string memory = "11111111-1111-4111-8111-111111111111";

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.Parse("22222222-2222-4222-8222-222222222222")]);

        CovenantDigest content = new(Convert.FromHexString(Hash));

        ArtifactSensitivityLabel label = new(Guid.NewGuid(), SensitiveArtifactKind.Saga, Guid.Parse(memory), null, null, null, 0, content, ContentSensitivity.CovenantDerived, provenance, null, null, null, DateTimeOffset.UtcNow);

        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null), label.SensitivityDigest).Value;

        LongRestSourceLeaf leaf = Leaf(memory, "version", 1) with
        {
            Sensitivity = ContentSensitivity.CovenantDerived,

            Provenance = provenance,

            LabelDigests = [label.LabelDigest],

            CanonicalLabel = label,

            SagaContentDigest = content,

            SagaRevision = 0,
        };

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [leaf]).IsSuccess);

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [leaf with { LabelDigests = [label.LabelDigest, new CovenantDigest(new byte[32])] }]).IsFailure);

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [leaf with { CanonicalLabel = null }]).IsFailure);

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [leaf with { SagaRevision = 1 }]).IsFailure);

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [leaf with { SagaContentDigest = new CovenantDigest(new byte[32]) }]).IsFailure);

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [leaf with { Provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]) }]).IsFailure);
    }

    [Fact]
    public void Work_hash_binds_the_literal_exact_fence_and_unsigned_path_policy()
    {
        LongRestWorkIdentity work = Work();

        Assert.Equal("051CC2C1B49ABA1D2CE818C4269A00A6CDD2C5AAB1628ED72A7A1F8D5AD65583", LongRestDiscoveryHash.Work(work).Value.ToString());

        Assert.NotEqual(LongRestDiscoveryHash.Work(work).Value, LongRestDiscoveryHash.Work(work with { ItemId = "fold-2" }).Value);

        Assert.True(LongRestDiscoveryHash.Work(work with { ItemId = "\ud800" }).IsFailure);

        CanonicalCampaignContext context = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(Guid.Parse("22222222-2222-4222-8222-222222222222")), 5, uint.MaxValue, 9, new CovenantDigest(new byte[32]));

        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Campaign, context.CampaignId)).Value;

        Assert.True(LongRestDiscoveryHash.Work(work with { Scope = scope, Fence = work.Fence with { CanonicalScope = context } }).IsSuccess);
    }

    [Fact]
    public void Recipe_refuses_changed_code_owned_bounds_and_preserves_model_identity()
    {
        CovenantDigest digest = new(new byte[32]);

        LongRestRecipeIdentity recipe = new(1, 1, "summary-provider", "summary-model", digest, "embedding-provider", "embedding-model", digest, 17);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe).IsSuccess);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { HierarchyChildLimit = 17 }).IsFailure);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { ProviderInputUtf8Bytes = 32769 }).IsFailure);

        // The lexical relevance floor is a code-owned bound that the recipe binds: any other value is not this code's recipe.
        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { LexicalMinimumScore = LongRestDiscoveryLimits.LexicalMinimumScore - 1 }).IsFailure);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { LexicalMinimumScore = LongRestDiscoveryLimits.LexicalMinimumScore + 1 }).IsFailure);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { LexicalMinimumScore = 0 }).IsFailure);

        Assert.True(LongRestDiscoveryHash.Recipe(recipe with { SummaryProvider = " " }).IsFailure);

        Assert.NotEqual(LongRestDiscoveryHash.Recipe(recipe).Value, LongRestDiscoveryHash.Recipe(recipe with { EmbeddingModel = "different-model" }).Value);
    }

    // Independently assembled preimages (Python struct.pack and hashlib.sha256): the bounds, the cosine bits, then the lexical relevance
    // floor as a final Int32BE. The same script reproduces the digests this recipe had before the floor joined it
    // (B668245D...F272 at 32768, 018B917E...98C8 at 4096), so only the appended floor moved them.
    [Theory]
    [InlineData(32768, "D485F93549969BEB4DB1736047677ED0159E5C46BA08170777D18F459EE02A84")]
    [InlineData(4096, "5E543293108EF872C010AE624E005B5D5039789AEFB08D064E29CB083F9A3393")]
    [InlineData(5328, "747E0AC6E33249EE9092FD8096A51044049ACB0427ED63F58BAAD165EC919065")]
    [InlineData(20000, "743464BF77B339B2A59B53802C2C0B11062C6BD219A6F425622630688156A267")]
    public void Recipe_digest_golden_vectors_pin_the_default_the_window_derived_budgets_and_the_lexical_floor(int budget, string expected)
    {
        CovenantDigest digest = new(new byte[32]);

        LongRestRecipeIdentity recipe = new LongRestRecipeIdentity(1, 1, "summary-provider", "summary-model", digest, "embedding-provider", "embedding-model", digest, 17) with { ProviderInputUtf8Bytes = budget };

        Assert.Equal(expected, LongRestDiscoveryHash.Recipe(recipe).Value.ToString());
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, true)]
    [InlineData(32767, true)]
    [InlineData(32768, true)]
    [InlineData(4095, false)]
    [InlineData(32769, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(int.MaxValue, false)]
    public void Recipe_accepts_exactly_the_input_budgets_from_the_minimum_recipe_to_the_32_KiB_ceiling(int budget, bool valid)
    {
        CovenantDigest digest = new(new byte[32]);

        LongRestRecipeIdentity recipe = new LongRestRecipeIdentity(1, 1, "summary-provider", "summary-model", digest, "embedding-provider", "embedding-model", digest, 17) with { ProviderInputUtf8Bytes = budget };

        Assert.Equal(valid, LongRestDiscoveryHash.Recipe(recipe).IsSuccess);
    }

    [Fact]
    public void Provider_payloads_refuse_oversized_utf8_and_freeze_exact_input_snapshots()
    {
        LongRestWorkIdentity work = Work();

        Assert.True(LongRestProviderPayload.Embedding(work, new string('a', 32768)).IsSuccess);

        Assert.True(LongRestProviderPayload.Embedding(work, new string('é', 16385)).IsFailure);

        Assert.True(LongRestProviderPayload.Embedding(work, "\ud800").IsFailure);

        LongRestSourceLeaf leaf = Leaf("memory", "version", 1) with { Content = "source" };

        work = work with { SourceDigest = LongRestDiscoveryHash.SourcePage(work.Scope, [leaf]).Value };

        LongRestProviderPayload payload = LongRestProviderPayload.Adjudication(work, [leaf]).Value;

        Assert.True(LongRestProviderPayload.Adjudication(work, [.. Enumerable.Range(0, 17).Select(index => Leaf($"memory-{index}", $"version-{index}", index + 1) with { Content = "source" })]).IsFailure);

        LongRestDependency edge = new("dependency", AnnalDependencyRelation.DerivedFrom, 1);

        LongRestDependency[] edges = [edge];

        leaf = leaf with { Snapshot = leaf.Snapshot with { Dependencies = edges } };

        work = work with { SourceDigest = LongRestDiscoveryHash.SourcePage(work.Scope, [leaf]).Value };

        payload = LongRestProviderPayload.Adjudication(work, [leaf]).Value;

        edges[0] = edge with { VersionId = "changed" };

        Assert.Equal("dependency", payload.Inputs[0].Snapshot.Dependencies[0].VersionId);
    }

    [Fact]
    public void Adjudication_payload_refuses_a_source_vector_outside_its_frozen_work_identity()
    {
        LongRestSourceLeaf leaf = Leaf("memory", "version", 1) with { Content = "source" };

        LongRestWorkIdentity work = Work() with { Kind = LongRestWorkKind.Anchor };

        work = work with { SourceDigest = LongRestDiscoveryHash.SourcePage(work.Scope, [leaf]).Value };

        Assert.True(LongRestProviderPayload.Adjudication(work, [leaf]).IsSuccess);

        Assert.True(LongRestProviderPayload.Adjudication(work with { SourceDigest = new CovenantDigest(new byte[32]) }, [leaf]).IsFailure);

        Assert.True(LongRestProviderPayload.Adjudication(work, [leaf with { MutationEpoch = 2 }]).IsFailure);
    }

    [Fact]
    public void Summary_payload_refuses_clean_protected_and_mixed_partition_children()
    {
        LongRestWorkIdentity work = Work();

        CovenantDigest digest = new(new byte[32]);

        LongRestHierarchyChild child = new(Guid.NewGuid(), 0, 0, digest, digest, "child summary", ContentSensitivity.None, GenerationProvenance.CreateExact([]), [], []);

        LongRestHierarchyFold fold = new(work, 1, 0, digest, [child]);

        fold = fold with { Work = work with { SourceDigest = LongRestDiscoveryHash.HierarchyFold(fold).Value } };

        Assert.True(LongRestProviderPayload.Summary(fold).IsSuccess);

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.Parse("22222222-2222-4222-8222-222222222222")]);

        child = child with { Sensitivity = ContentSensitivity.CovenantDerived, Provenance = provenance, LabelDigests = [digest] };

        Assert.True(LongRestProviderPayload.Summary(fold with { Children = [child] }).IsFailure);

        LongRestSweepScope protectedScope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null), CovenantDigests.Sensitivity(provenance.ToDigestInput(ContentSensitivity.CovenantDerived))).Value;

        fold = fold with { Work = work with { Scope = protectedScope }, Children = [child] };

        fold = fold with { Work = fold.Work with { SourceDigest = LongRestDiscoveryHash.HierarchyFold(fold).Value } };

        Assert.True(LongRestProviderPayload.Summary(fold).IsSuccess);

        Assert.True(LongRestProviderPayload.Summary(fold with { Children = [child with { LabelDigests = [] }] }).IsFailure);

        Assert.True(LongRestProviderPayload.Summary(fold with { Children = [child with { Provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]) }] }).IsFailure);

        Assert.True(LongRestProviderPayload.Summary(fold with { Children = [child, child with { NodeId = Guid.NewGuid(), Sensitivity = ContentSensitivity.None, Provenance = GenerationProvenance.CreateExact([]), LabelDigests = [] }] }).IsFailure);
    }

    [Fact]
    public void Content_free_progress_and_checkpoint_preserve_the_protected_partition()
    {
        const string json = """{"Scope":"Global","Phase":"Capture","ProtectedPartitionDigest":{"Bytes":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}}""";

        LongRestSweepProgress progress = JsonSerializer.Deserialize(json, LongRestTestJsonContext.Default.LongRestSweepProgress)!;

        using JsonDocument progressJson = JsonDocument.Parse(JsonSerializer.Serialize(progress, LongRestTestJsonContext.Default.LongRestSweepProgress));

        Assert.True(progressJson.RootElement.TryGetProperty("ProtectedPartitionDigest", out JsonElement progressPartition));

        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", progressPartition.GetProperty("Bytes").GetString());

        LongRestSweepCheckpoint checkpoint = JsonSerializer.Deserialize(json, LongRestTestJsonContext.Default.LongRestSweepCheckpoint)!;

        using JsonDocument checkpointJson = JsonDocument.Parse(JsonSerializer.Serialize(checkpoint, LongRestTestJsonContext.Default.LongRestSweepCheckpoint));

        Assert.True(checkpointJson.RootElement.TryGetProperty("ProtectedPartitionDigest", out JsonElement checkpointPartition));

        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", checkpointPartition.GetProperty("Bytes").GetString());
    }

    [Fact]
    public void Hierarchy_fold_hash_has_a_literal_canonical_manifest_and_refuses_malformed_coordinates()
    {
        LongRestHierarchyFold fold = Fold();

        Assert.Equal("8C5E286BB2B923BF4CD2AC4B30EFF61B4CCA32A56846D6E4540750823829B0F6", LongRestDiscoveryHash.HierarchyFold(fold).Value.ToString());

        Assert.Equal(LongRestDiscoveryHash.HierarchyFold(fold).Value, LongRestDiscoveryHash.HierarchyFold(fold with { Children = [.. fold.Children.Reverse()] }).Value);

        Assert.Equal(LongRestDiscoveryHash.HierarchyFold(fold).Value, LongRestDiscoveryHash.HierarchyFold(fold with { Work = fold.Work with { SourceDigest = new CovenantDigest(new byte[32]) }, Children = [.. fold.Children.Select(child => child with { Content = "different plaintext" })] }).Value);

        Assert.NotEqual(LongRestDiscoveryHash.HierarchyFold(fold).Value, LongRestDiscoveryHash.HierarchyFold(fold with { MembershipDigest = new CovenantDigest(new byte[32]) }).Value);

        LongRestHierarchyChild first = fold.Children.Single(child => child.Group == 0);

        Assert.True(LongRestDiscoveryHash.HierarchyFold(fold with { Children = [first, first with { NodeId = Guid.NewGuid() }] }).IsFailure);

        Assert.True(LongRestDiscoveryHash.HierarchyFold(fold with { Children = [first with { SourceDigest = default }] }).IsFailure);

        Assert.True(LongRestDiscoveryHash.HierarchyFold(fold with { Children = [first with { ExpandedTargets = [new("\ud800", "version", Hash)] }] }).IsFailure);

        Assert.True(LongRestDiscoveryHash.HierarchyFold(fold with { Children = [first with { ExpandedTargets = [first.ExpandedTargets[0], first.ExpandedTargets[0]] }] }).IsFailure);
    }

    [Fact]
    public void Carried_children_keep_actual_lower_layers_in_the_fold_hash()
    {
        LongRestHierarchyFold adjacent = Fold() with { Layer = 3, Children = [.. Fold().Children.Select(child => child with { Layer = 2 })] };

        LongRestHierarchyFold mixed = adjacent with { Children = [adjacent.Children[0] with { Layer = 1 }, adjacent.Children[1]] };

        Assert.True(LongRestDiscoveryHash.HierarchyFold(mixed).IsSuccess);

        Assert.NotEqual(LongRestDiscoveryHash.HierarchyFold(adjacent).Value, LongRestDiscoveryHash.HierarchyFold(mixed).Value);

        foreach (int invalidLayer in new[] { -1, 3, 4 })
        {
            Assert.True(LongRestDiscoveryHash.HierarchyFold(mixed with { Children = [mixed.Children[0] with { Layer = invalidLayer }, mixed.Children[1]] }).IsFailure);
        }
    }

    [Fact]
    public void Summary_payload_refuses_a_typed_fold_outside_its_frozen_source_identity()
    {
        LongRestHierarchyFold fold = Fold();

        fold = fold with { Work = fold.Work with { SourceDigest = LongRestDiscoveryHash.HierarchyFold(fold).Value } };

        Assert.True(LongRestProviderPayload.Summary(fold).IsSuccess);

        Assert.True(LongRestProviderPayload.Summary(fold with { Work = fold.Work with { SourceDigest = new CovenantDigest(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()) } }).IsFailure);
    }

    private static LongRestHierarchyFold Fold()
    {
        LongRestWorkIdentity work = Work();

        LongRestHierarchyChild first = new(Guid.Parse("11111111-1111-4111-8111-111111111111"), 0, 0, new CovenantDigest(new byte[32]), work.SourceDigest, "first child", ContentSensitivity.None, GenerationProvenance.CreateExact([]), [], [new("a|b", "c", Hash)]);

        LongRestHierarchyChild last = new(Guid.Parse("22222222-2222-4222-8222-222222222222"), 0, 1, work.SourceDigest, work.RecipeDigest, "last child", ContentSensitivity.None, GenerationProvenance.CreateExact([]), [], [new("z", "z-version", Hash)]);

        return new(work, 1, 4, work.SourceDigest, [last, first]);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    public void Expanded_hierarchy_leaves_retain_the_merged_neighbor_budget(int suppliedCount)
    {
        LongRestTarget anchor = new("anchor", "anchor-version", Hash);

        LongRestCandidate[] supplied = [.. Enumerable.Range(0, suppliedCount).Select(index => new LongRestCandidate(new($"memory-{index:D3}", $"version-{index:D3}", Hash), LongRestDiscoveryLane.Hierarchy, 1))];

        ImmutableArray<LongRestCandidate> merged = LongRestDiscoveryPolicy.MergeCandidates(anchor, supplied.Reverse()).Value;

        Assert.Equal(Math.Min(suppliedCount, 32), merged.Length);

        Assert.Equal(merged.ToArray(), LongRestDiscoveryPolicy.MergeCandidates(anchor, supplied).Value.ToArray());

        Assert.Equal("memory-000", merged[0].Target.MemoryId);

        Assert.Equal($"memory-{merged.Length - 1:D3}", merged[^1].Target.MemoryId);

        ImmutableArray<LongRestCandidateGroup> groups = LongRestDiscoveryPolicy.GroupCandidates(anchor, merged).Value;

        Assert.All(groups, group => Assert.InRange(group.Targets.Length, 2, 16));

        Assert.Equal(merged.Length, groups.Sum(group => group.Targets.Length - 1));
    }

    [Fact]
    public void Returned_payload_dependencies_cannot_mutate_the_frozen_source_identity()
    {
        LongRestDependency outgoing = new("dependency", AnnalDependencyRelation.DerivedFrom, 1);

        LongRestIncomingDependency incoming = new("dependent", AnnalDependencyRelation.DerivedFrom, true);

        LongRestSourceLeaf leaf = Leaf("memory", "version", 1) with { Content = "source" };

        leaf = leaf with { Snapshot = leaf.Snapshot with { Dependencies = [outgoing], IncomingDependencies = [incoming] } };

        LongRestWorkIdentity work = Work();

        work = work with { SourceDigest = LongRestDiscoveryHash.SourcePage(work.Scope, [leaf]).Value };

        LongRestProviderPayload payload = LongRestProviderPayload.Adjudication(work, [leaf]).Value;

        LongRestSnapshot exposed = payload.Inputs[0].Snapshot;

        exposed.Dependencies[0] = outgoing with { VersionId = "changed-outgoing" };

        exposed.IncomingDependencies[0] = incoming with { DependentVersionId = "changed-incoming" };

        Assert.Equal(outgoing, payload.Inputs[0].Snapshot.Dependencies[0]);

        Assert.Equal(incoming, payload.Inputs[0].Snapshot.IncomingDependencies[0]);

        Assert.Equal(work.SourceDigest, LongRestDiscoveryHash.SourcePage(work.Scope, payload.Inputs).Value);
    }

    [Fact]
    public void Observed_source_vectors_bind_literal_bytes_without_inventing_producer_identity()
    {
        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).Value;

        ImmutableArray<float> vector = [1, -2, 3.5f];

        CovenantDigest raw = VectorDigest(vector);

        Assert.Equal("89E4869F4E44C5AE044A22108F669AF1C86DB9944003C8F2C07FDAB3980D5938", raw.ToString());

        LongRestSourceLeaf leaf = ObservedLeaf(vector);

        Assert.Equal("0BC16366554C571B358416CFE0C3216FDEA346FC316B8C21E15C3F23B4B9C1AC", LongRestDiscoveryHash.SourcePage(scope, [leaf]).Value.ToString());

        Assert.Equal(LongRestDiscoveryHash.SourcePage(scope, [leaf]).Value, LongRestDiscoveryHash.SourcePage(scope, [leaf with { Vector = default }]).Value);

        Assert.Equal(LongRestDiscoveryHash.SourcePage(scope, [leaf]).Value, LongRestDiscoveryHash.SourcePage(scope, [leaf with { Vector = [] }]).Value);

        Assert.NotEqual(LongRestDiscoveryHash.SourcePage(scope, [leaf]).Value, LongRestDiscoveryHash.SourcePage(scope, [leaf with { EmbeddingBinding = null, Vector = default }]).Value);

        Assert.NotEqual(LongRestDiscoveryHash.SourcePage(scope, [leaf]).Value, LongRestDiscoveryHash.SourcePage(scope, [leaf with { EmbeddingBinding = leaf.EmbeddingBinding! with { Dimensions = 4 }, Vector = default }]).Value);

        LongRestSourceLeaf changed = ObservedLeaf([1, -2, -3.5f]);

        Assert.NotEqual(LongRestDiscoveryHash.SourcePage(scope, [leaf]).Value, LongRestDiscoveryHash.SourcePage(scope, [changed]).Value);
    }

    [Theory]
    [InlineData("digest-mismatch")]
    [InlineData("signed-zero-byte-mismatch")]
    [InlineData("zero-norm")]
    [InlineData("nonfinite")]
    [InlineData("nonfinite-norm")]
    [InlineData("dimension-mismatch")]
    [InlineData("missing-binding")]
    [InlineData("unknown-kind")]
    [InlineData("overflow-dimension")]
    [InlineData("invalid-digest")]
    public void Source_vector_boundaries_refuse_unproved_or_unusable_bytes(string malformed)
    {
        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).Value;

        LongRestSourceLeaf leaf = ObservedLeaf([1, -2, 3.5f]);

        leaf = malformed switch
        {
            "digest-mismatch" => leaf with { Vector = [1, -2, -3.5f] },

            "signed-zero-byte-mismatch" => ObservedLeaf([1, -0f, 3.5f]) with { Vector = [1, 0f, 3.5f] },

            "zero-norm" => ObservedLeaf([0, 0, 0]),

            "nonfinite" => ObservedLeaf([1, float.NaN, 3.5f]),

            "nonfinite-norm" => ObservedLeaf([.. Enumerable.Repeat(float.MaxValue, 32)]),

            "dimension-mismatch" => leaf with { EmbeddingBinding = leaf.EmbeddingBinding! with { Dimensions = 4 } },

            "missing-binding" => leaf with { EmbeddingBinding = null },

            "unknown-kind" => leaf with { EmbeddingBinding = leaf.EmbeddingBinding! with { Kind = (LongRestSourceEmbeddingKind)2 }, Vector = default },

            "overflow-dimension" => leaf with { EmbeddingBinding = leaf.EmbeddingBinding! with { Dimensions = int.MaxValue }, Vector = default },

            "invalid-digest" => leaf with { EmbeddingBinding = leaf.EmbeddingBinding! with { ValueDigest = default }, Vector = default },

            _ => throw new InvalidOperationException("Unknown test boundary."),
        };

        Assert.True(LongRestDiscoveryHash.SourcePage(scope, [leaf]).IsFailure);
    }

    private static LongRestSourceLeaf ObservedLeaf(ImmutableArray<float> vector)
    {
        LongRestSourceLeaf leaf = Leaf("memory", "version", 1);

        return leaf with
        {
            Snapshot = leaf.Snapshot with { HasEmbedding = true },

            EmbeddingBinding = new(LongRestSourceEmbeddingKind.ObservedSaga, vector.Length, VectorDigest(vector)),

            Vector = vector,
        };
    }

    private static CovenantDigest VectorDigest(ImmutableArray<float> vector) => new(SHA256.HashData(EmbeddingBlobCodec.Encode(vector.AsSpan())));

    private static LongRestWorkIdentity Work()
    {
        CovenantDigest digest = new(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).Value;

        LongRestConfigurationStamp configuration = new(Guid.Parse("55555555-5555-4555-8555-555555555555"), 7, digest);

        LongRestSweepFence fence = new(CanonicalCampaignContext.GlobalOnly, 128, Guid.Parse("11111111-1111-4111-8111-111111111111"), 1, 1, 1, configuration);

        return new(Guid.Parse("66666666-6666-4666-8666-666666666666"), Guid.Parse("44444444-4444-4444-8444-444444444444"), "fold-1", LongRestWorkKind.HierarchyFold, scope, fence, digest, new CovenantDigest(Enumerable.Range(2, 32).Select(value => (byte)value).ToArray()));
    }

    [Fact]
    public void Candidate_merge_is_bounded_and_stable()
    {
        LongRestTarget anchor = new("anchor", "anchor-version", Hash);

        LongRestCandidate[] supplied = Enumerable.Range(0, 100).Select(index =>
            new LongRestCandidate(new($"m{index:D3}", $"v{index:D3}", Hash), LongRestDiscoveryLane.ExactHash, 0)).ToArray();

        ImmutableArray<LongRestCandidate> merged = LongRestDiscoveryPolicy.MergeCandidates(anchor, supplied.Reverse()).Value;

        Assert.Equal(32, merged.Length);

        Assert.Equal("m000", merged[0].Target.MemoryId);

        Assert.Equal("m031", merged[^1].Target.MemoryId);

        Assert.Equal(merged.ToArray(), LongRestDiscoveryPolicy.MergeCandidates(anchor, supplied).Value.ToArray());

        ImmutableArray<LongRestCandidateGroup> groups = LongRestDiscoveryPolicy.GroupCandidates(anchor, merged).Value;

        Assert.Equal(3, groups.Length);

        Assert.All(groups, group => Assert.InRange(group.Targets.Length, 2, 16));

        Assert.All(groups, group => Assert.Equal(anchor, group.Targets[0]));

        Assert.Equal(32, groups.Sum(group => group.Targets.Length - 1));
    }

    [Fact]
    public void Candidate_lanes_merge_by_authority_free_evidence_and_refuse_malformed_scores()
    {
        LongRestTarget anchor = new("anchor", "anchor-version", Hash);

        LongRestTarget duplicate = new("duplicate", "duplicate-version", Hash);

        LongRestCandidate[] supplied =
        [
            new(new("hierarchy", "hierarchy-version", Hash), LongRestDiscoveryLane.Hierarchy, 1),
            new(duplicate, LongRestDiscoveryLane.Vector, 0.99),
            new(duplicate, LongRestDiscoveryLane.ExactHash, 0),
            new(new("lexical", "lexical-version", Hash), LongRestDiscoveryLane.Lexical, -2),
            new(new("vector", "vector-version", Hash), LongRestDiscoveryLane.Vector, 0.92),
            new(new("low-vector", "low-vector-version", Hash), LongRestDiscoveryLane.Vector, 0.919),
            new(anchor, LongRestDiscoveryLane.ExactHash, 0),
        ];

        Assert.Equal(new[] { "duplicate", "lexical", "vector", "hierarchy" }, LongRestDiscoveryPolicy.MergeCandidates(anchor, supplied).Value.Select(item => item.Target.MemoryId));

        Assert.True(LongRestDiscoveryPolicy.MergeCandidates(anchor, [new(duplicate, LongRestDiscoveryLane.Vector, double.NaN)]).IsFailure);

        Assert.True(LongRestDiscoveryPolicy.MergeCandidates(anchor, [new(duplicate, (LongRestDiscoveryLane)99, 0)]).IsFailure);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(LongRestSweepScopeKind))]
[System.Text.Json.Serialization.JsonSerializable(typeof(LongRestSweepProgress))]
[System.Text.Json.Serialization.JsonSerializable(typeof(LongRestSweepCheckpoint))]
internal partial class LongRestTestJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
