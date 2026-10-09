using System.Globalization;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestPolicyTests
{
    private const string Digest = "608E75C81AB8335F1E92DE3C436631AC79301B3FA4AD4185DB289F72C7579DA2";

    private static readonly DateTimeOffset Instant = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Exact_duplicates_choose_the_ordinal_survivor_without_changing_source_snapshots()
    {
        LongRestSnapshot first = Snapshot("memory-b");

        LongRestSnapshot second = Snapshot("memory-a");

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second);

        Assert.Equal(LongRestOutcome.Applied, receipt.Outcome);

        Assert.Equal(LongRestReason.None, receipt.Reason);

        Assert.Equal("memory-a", receipt.SurvivorMemoryId);

        Assert.Equal("claim-memory-a", receipt.SurvivorClaimId);

        Assert.Equal("version-memory-a", receipt.SurvivorVersionId);

        Assert.Equal(["version-memory-b"], receipt.SupersededVersionIds);

        Assert.Equal(["memory-a", "memory-b"], receipt.Targets.Select(target => target.MemoryId));

        Assert.Null(first.RetiredAtUtc);

        Assert.Null(first.PinnedAtUtc);

        Assert.True(first.HasEmbedding);

        Assert.False(first.IsConsolidated);
    }

    [Fact]
    public void Operator_origin_precedes_memory_identity_without_relabeling_either_source()
    {
        LongRestSnapshot operatorStatement = Snapshot("memory-z") with { Origin = AnnalOrigin.OperatorStated };

        LongRestSnapshot extracted = Snapshot("memory-a");

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, extracted, operatorStatement);

        Assert.Equal(LongRestOutcome.Applied, receipt.Outcome);

        Assert.Equal("memory-z", receipt.SurvivorMemoryId);

        Assert.Equal(AnnalOrigin.AgentExtracted, extracted.Origin);

        Assert.Equal(AnnalOrigin.OperatorStated, operatorStatement.Origin);
    }

    [Fact]
    public void Equivalent_observation_declaration_accepts_distinct_content_hashes()
    {
        LongRestSnapshot second = Snapshot("memory-b") with { ContentHash = new string('A', 64) };

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.EquivalentObservations, Snapshot("memory-a"), second);

        Assert.Equal(LongRestOutcome.Applied, receipt.Outcome);

        Assert.Equal(["version-memory-b"], receipt.SupersededVersionIds);
    }

    [Fact]
    public void Supersession_uses_the_declared_exact_winner_even_when_origin_priority_would_choose_another()
    {
        LongRestSnapshot first = Snapshot("memory-a") with { Origin = AnnalOrigin.OperatorStated };

        LongRestSnapshot second = Snapshot("memory-b") with { ContentHash = new string('A', 64) };

        LongRestRequest request = Request(LongRestTransformationKind.Supersession, first, second)
            with { SurvivorVersionId = second.VersionId };

        Result<LongRestReceipt> result = LongRestPolicy.Evaluate(request, [first, second]);

        Assert.True(result.IsSuccess);

        Assert.Equal(LongRestOutcome.Applied, result.Value.Outcome);

        Assert.Equal(second.VersionId, result.Value.SurvivorVersionId);

        Assert.Equal([first.VersionId], result.Value.SupersededVersionIds);
    }

    [Fact]
    public void Supersession_requires_a_declared_winner_in_the_exact_target_set()
    {
        LongRestSnapshot[] snapshots = [Snapshot("memory-a"), Snapshot("memory-b")];

        LongRestRequest request = Request(LongRestTransformationKind.Supersession, snapshots);

        AssertFailure(LongRestPolicy.Evaluate(request, snapshots), "LongRest.InvalidRequest");

        AssertFailure(LongRestPolicy.Evaluate(request with { SurvivorVersionId = "unseen" }, snapshots), "LongRest.InvalidRequest");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void A_request_outside_the_target_bound_is_refused(int count)
    {
        LongRestSnapshot[] snapshots = Enumerable.Range(0, count)
            .Select(index => Snapshot($"memory-{index:D2}"))
            .ToArray();

        AssertFailure(LongRestPolicy.Evaluate(Request(LongRestTransformationKind.ExactDuplicates, snapshots), snapshots),
            "LongRest.InvalidRequest");
    }

    [Fact]
    public void Sixteen_distinct_targets_converge_to_one_existing_version()
    {
        LongRestSnapshot[] snapshots = Enumerable.Range(0, 16)
            .Select(index => Snapshot($"memory-{index:D2}"))
            .Reverse()
            .ToArray();

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, snapshots);

        Assert.Equal(LongRestOutcome.Applied, receipt.Outcome);

        Assert.Equal("version-memory-00", receipt.SurvivorVersionId);

        Assert.Equal(15, receipt.SupersededVersionIds.Length);
    }

    [Fact]
    public void Duplicate_memory_or_version_targets_are_refused_before_a_receipt_exists()
    {
        LongRestSnapshot first = Snapshot("memory-a");

        AssertFailure(LongRestPolicy.Evaluate(Request(LongRestTransformationKind.ExactDuplicates, first, first), [first, first]),
            "LongRest.InvalidRequest");

        LongRestSnapshot duplicateVersion = Snapshot("memory-b") with { VersionId = first.VersionId };

        AssertFailure(LongRestPolicy.Evaluate(Request(LongRestTransformationKind.ExactDuplicates, first, duplicateVersion), [first, duplicateVersion]),
            "LongRest.InvalidRequest");
    }

    [Fact]
    public void A_single_source_receives_a_content_addressed_no_change_receipt()
    {
        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-a"));

        Assert.Equal(LongRestOutcome.NoChange, receipt.Outcome);

        Assert.Equal(LongRestReason.SingleInput, receipt.Reason);

        Assert.Null(receipt.SurvivorVersionId);

        Assert.Empty(receipt.SupersededVersionIds);

        Assert.Equal(64, receipt.ReceiptId.Length);

        Assert.Equal(64, receipt.InputHash.Length);

        Assert.Equal(64, receipt.OutputHash.Length);
    }

    [Theory]
    [InlineData(LongRestReason.Pinned)]
    [InlineData(LongRestReason.Retired)]
    [InlineData(LongRestReason.Protected)]
    [InlineData(LongRestReason.AlreadyConsolidated)]
    public void Protected_lifecycle_states_produce_no_change_without_superseded_outputs(LongRestReason reason)
    {
        LongRestSnapshot second = Snapshot("memory-b");

        second = reason switch
        {
            LongRestReason.Pinned => second with { PinnedAtUtc = Instant },
            LongRestReason.Retired => second with { RetiredAtUtc = Instant },
            LongRestReason.Protected => second with { Sensitivity = ContentSensitivity.CovenantDerived },
            LongRestReason.AlreadyConsolidated => second with { IsConsolidated = true },
            _ => second,
        };

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-a"), second);

        Assert.Equal(LongRestOutcome.NoChange, receipt.Outcome);

        Assert.Equal(reason, receipt.Reason);

        Assert.Empty(receipt.SupersededVersionIds);
    }

    [Fact]
    public void An_artifact_sensitivity_label_blocks_even_an_unlabeled_Annal_version()
    {
        LongRestSnapshot second = Snapshot("memory-b") with { HasSensitivityLabel = true };

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-a"), second);

        Assert.Equal(LongRestReason.Protected, receipt.Reason);

        Assert.Equal(LongRestOutcome.NoChange, receipt.Outcome);
    }

    [Theory]
    [InlineData(SagaMemoryScopeKind.Global, SagaMemoryScopeKind.Campaign)]
    [InlineData(SagaMemoryScopeKind.Unclassified, SagaMemoryScopeKind.Unclassified)]
    [InlineData(SagaMemoryScopeKind.LegacyUnresolved, SagaMemoryScopeKind.LegacyUnresolved)]
    public void Consolidation_never_widens_or_invents_Campaign_authority(SagaMemoryScopeKind firstScope, SagaMemoryScopeKind secondScope)
    {
        Guid campaign = Guid.Parse("12345678-1234-1234-1234-123456789012");

        LongRestSnapshot first = Snapshot("memory-a") with { ScopeKind = firstScope };

        LongRestSnapshot second = Snapshot("memory-b") with
        {
            ScopeKind = secondScope,

            CampaignId = secondScope == SagaMemoryScopeKind.Campaign ? campaign : null,
        };

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second);

        Assert.Equal(LongRestReason.DifferentScope, receipt.Reason);

        Assert.Equal(LongRestOutcome.NoChange, receipt.Outcome);
    }

    [Fact]
    public void Different_Campaigns_do_not_consolidate_identical_content()
    {
        LongRestSnapshot first = Snapshot("memory-a") with
        {
            ScopeKind = SagaMemoryScopeKind.Campaign,

            CampaignId = Guid.Parse("12345678-1234-1234-1234-123456789012"),
        };

        LongRestSnapshot second = Snapshot("memory-b") with
        {
            ScopeKind = SagaMemoryScopeKind.Campaign,

            CampaignId = Guid.Parse("87654321-4321-4321-4321-210987654321"),
        };

        Assert.Equal(LongRestReason.DifferentScope,
            Evaluate(LongRestTransformationKind.ExactDuplicates, first, second).Reason);
    }

    [Fact]
    public void Exact_duplicate_identity_includes_the_content_hash_format()
    {
        LongRestSnapshot second = Snapshot("memory-b") with { ContentHashFormat = AnnalContentHashFormat.LexiconStructuredSnapshot };

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-a"), second);

        Assert.Equal(LongRestReason.DifferentContent, receipt.Reason);

        Assert.Equal(LongRestOutcome.NoChange, receipt.Outcome);
    }

    [Fact]
    public void Exact_duplicates_with_distinct_digests_produce_no_change()
    {
        LongRestSnapshot second = Snapshot("memory-b") with { ContentHash = new string('A', 64) };

        Assert.Equal(LongRestReason.DifferentContent,
            Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-a"), second).Reason);
    }

    [Fact]
    public void Different_open_validity_starts_converge_without_rewriting_validity()
    {
        LongRestSnapshot second = Snapshot("memory-b") with { ValidFromUtc = Instant.AddDays(3) };

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-a"), second);

        Assert.Equal(LongRestOutcome.Applied, receipt.Outcome);

        Assert.Equal(Instant.AddDays(3), second.ValidFromUtc);

        Assert.Null(second.ValidToUtc);
    }

    [Fact]
    public void Finite_differing_validity_intervals_do_not_become_a_wider_claim()
    {
        LongRestSnapshot first = Snapshot("memory-a") with { ValidToUtc = Instant.AddDays(1) };

        LongRestSnapshot second = Snapshot("memory-b") with { ValidFromUtc = Instant.AddDays(2), ValidToUtc = Instant.AddDays(3) };

        Assert.Equal(LongRestReason.IncompatibleValidity,
            Evaluate(LongRestTransformationKind.ExactDuplicates, first, second).Reason);
    }

    [Fact]
    public void Dependency_targets_and_relations_must_agree_for_equivalent_observations()
    {
        LongRestSnapshot first = Snapshot("memory-a") with
        {
            Dependencies = [new LongRestDependency("earlier-a", AnnalDependencyRelation.DerivedFrom, 1)],
        };

        LongRestSnapshot second = Snapshot("memory-b") with
        {
            Dependencies = [new LongRestDependency("earlier-b", AnnalDependencyRelation.DerivedFrom, 1)],
        };

        Assert.Equal(LongRestReason.DependencyConflict,
            Evaluate(LongRestTransformationKind.EquivalentObservations, first, second).Reason);

        second = second with
        {
            Dependencies = [new LongRestDependency("earlier-a", AnnalDependencyRelation.Corroborates, 1)],
        };

        Assert.Equal(LongRestReason.DependencyConflict,
            Evaluate(LongRestTransformationKind.EquivalentObservations, first, second).Reason);
    }

    [Fact]
    public void A_current_derived_dependent_blocks_suppressing_its_exact_source()
    {
        LongRestSnapshot second = Snapshot("memory-b") with
        {
            IncomingDependencies = [new LongRestIncomingDependency("dependent-version", AnnalDependencyRelation.DerivedFrom, true)],
        };

        Assert.Equal(LongRestReason.DependencyConflict,
            Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-a"), second).Reason);

        second = second with
        {
            IncomingDependencies = [new LongRestIncomingDependency("dependent-version", AnnalDependencyRelation.DerivedFrom, false)],
        };

        Assert.Equal(LongRestOutcome.Applied,
            Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-a"), second).Outcome);
    }

    [Fact]
    public void A_prior_transformation_output_cannot_be_eliminated_into_a_suppression_chain()
    {
        LongRestSnapshot second = Snapshot("memory-b") with { IsTransformationOutput = true };

        Assert.Equal(LongRestReason.DependencyConflict,
            Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-a"), second).Reason);
    }

    [Fact]
    public void All_input_and_dependency_enumeration_orders_have_the_same_result_and_receipt_identity()
    {
        LongRestDependency[] dependencies =
        [
            new("earlier-b", AnnalDependencyRelation.DerivedFrom, 2),

            new("earlier-a", AnnalDependencyRelation.Corroborates, 1),
        ];

        LongRestSnapshot first = Snapshot("memory-a") with { Dependencies = dependencies };

        LongRestSnapshot second = Snapshot("memory-b") with { Dependencies = [.. dependencies.Reverse()] };

        LongRestReceipt forward = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second);

        LongRestReceipt backward = Evaluate(LongRestTransformationKind.ExactDuplicates, second, first);

        Assert.Equal(LongRestOutcome.Applied, forward.Outcome);

        Assert.Equal(forward.ReceiptId, backward.ReceiptId);

        Assert.Equal(forward.InputHash, backward.InputHash);

        Assert.Equal(forward.OutputHash, backward.OutputHash);

        Assert.Equal(forward.SurvivorVersionId, backward.SurvivorVersionId);
    }

    [Fact]
    public void Canonical_identity_is_independent_of_current_head()
    {
        LongRestSnapshot first = Snapshot("memory-a");

        LongRestSnapshot second = Snapshot("memory-b");

        LongRestReceipt applied = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second);

        Result<string> replayIdentity = LongRestPolicy.Identify(Request(LongRestTransformationKind.ExactDuplicates, first, second),
            [first with { IsCurrent = false }, second with { IsCurrent = false }]);

        Assert.Equal(LongRestOutcome.Applied, applied.Outcome);

        Assert.True(replayIdentity.IsSuccess);

        Assert.Equal(applied.ReceiptId, replayIdentity.Value);
    }

    [Theory]
    [InlineData(true, false, LongRestReason.AlreadyConsolidated)]
    [InlineData(false, true, LongRestReason.DependencyConflict)]
    public void Decision_relevant_consolidation_context_changes_canonical_identity(
        bool consolidated, bool transformationOutput, LongRestReason expectedReason)
    {
        LongRestSnapshot first = Snapshot("memory-a");

        LongRestSnapshot second = Snapshot("memory-b");

        LongRestReceipt available = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second);

        LongRestSnapshot contextual = second with
        {
            IsConsolidated = consolidated,

            IsTransformationOutput = transformationOutput,
        };

        LongRestReceipt blocked = Evaluate(LongRestTransformationKind.ExactDuplicates, first, contextual);

        Assert.Equal(LongRestOutcome.Applied, available.Outcome);

        Assert.Equal(LongRestOutcome.NoChange, blocked.Outcome);

        Assert.Equal(expectedReason, blocked.Reason);

        Assert.NotEqual(available.InputHash, blocked.InputHash);

        Assert.NotEqual(available.ReceiptId, blocked.ReceiptId);

        Result<string> identity = LongRestPolicy.Identify(Request(LongRestTransformationKind.ExactDuplicates, first, contextual),
            [first, contextual]);

        Assert.True(identity.IsSuccess);

        Assert.Equal(blocked.ReceiptId, identity.Value);
    }

    [Fact]
    public void Pin_metadata_changes_the_receipt_identity()
    {
        LongRestSnapshot first = Snapshot("memory-a");

        LongRestSnapshot second = Snapshot("memory-b");

        LongRestReceipt unpinned = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second);

        LongRestReceipt pinned = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second with { PinnedAtUtc = Instant });

        Assert.Equal(LongRestOutcome.Applied, unpinned.Outcome);

        Assert.NotEqual(unpinned.InputHash, pinned.InputHash);

        Assert.NotEqual(unpinned.ReceiptId, pinned.ReceiptId);

        Assert.Equal(LongRestReason.Pinned, pinned.Reason);
    }

    [Fact]
    public void Equivalent_UTC_instants_and_hex_spellings_have_identical_receipt_identity()
    {
        LongRestSnapshot first = Snapshot("memory-a");

        LongRestSnapshot second = Snapshot("memory-b");

        LongRestReceipt original = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second);

        LongRestReceipt alternate = Evaluate(LongRestTransformationKind.ExactDuplicates,
            first with { ContentHash = Digest.ToLowerInvariant(), ValidFromUtc = Instant.ToOffset(TimeSpan.FromHours(-5)) },
            second with { RecordedAtUtc = Instant.ToOffset(TimeSpan.FromHours(9)) });

        Assert.Equal(LongRestOutcome.Applied, original.Outcome);

        Assert.Equal(original.ReceiptId, alternate.ReceiptId);

        Assert.Equal(original.InputHash, alternate.InputHash);
    }

    [Fact]
    public void Culture_does_not_change_the_survivor_or_canonical_hashes()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

            LongRestReceipt turkish = Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-I"), Snapshot("memory-ı"));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

            LongRestReceipt english = Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-I"), Snapshot("memory-ı"));

            Assert.Equal("memory-I", turkish.SurvivorMemoryId);

            Assert.Equal(turkish.ReceiptId, english.ReceiptId);

            Assert.Equal(turkish.OutputHash, english.OutputHash);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_malformed_hash_stale_target_or_unpaired_surrogate_is_refused()
    {
        LongRestSnapshot first = Snapshot("memory-a");

        LongRestSnapshot malformed = Snapshot("memory-b") with { ContentHash = "broken" };

        AssertFailure(LongRestPolicy.Evaluate(Request(LongRestTransformationKind.ExactDuplicates, first, malformed), [first, malformed]),
            "LongRest.InvalidRequest");

        LongRestSnapshot stale = Snapshot("memory-b") with { IsCurrent = false };

        AssertFailure(LongRestPolicy.Evaluate(Request(LongRestTransformationKind.ExactDuplicates, first, stale), [first, stale]),
            "LongRest.StaleInput");

        LongRestSnapshot invalidText = Snapshot("memory-\uD800");

        AssertFailure(LongRestPolicy.Evaluate(Request(LongRestTransformationKind.ExactDuplicates, first, invalidText), [first, invalidText]),
            "LongRest.InvalidRequest");
    }

    [Fact]
    public void Snapshot_binding_must_match_the_exact_expected_version_and_content_digest()
    {
        LongRestSnapshot[] snapshots = [Snapshot("memory-a"), Snapshot("memory-b")];

        LongRestRequest request = Request(LongRestTransformationKind.ExactDuplicates, snapshots);

        AssertFailure(LongRestPolicy.Evaluate(request, [snapshots[0], snapshots[1] with { VersionId = "other-version" }]),
            "LongRest.StaleInput");

        AssertFailure(LongRestPolicy.Evaluate(request, [snapshots[0], snapshots[1] with { ContentHash = new string('A', 64) }]),
            "LongRest.StaleInput");
    }

    [Fact]
    public void Receipt_identity_can_be_probed_after_the_exact_inputs_cease_being_current()
    {
        LongRestSnapshot first = Snapshot("memory-a") with { IsCurrent = false };

        LongRestSnapshot second = Snapshot("memory-b") with { IsCurrent = false };

        Result<string> identity = LongRestPolicy.Identify(Request(LongRestTransformationKind.ExactDuplicates, first, second), [first, second]);

        Assert.True(identity.IsSuccess);

        Assert.Equal("CC7B25C0544BF5754A0AE1952D4906B0761E6EBBB1554214CFCA67B6E1C548E0", identity.Value);
    }

    [Fact]
    public void The_durable_canonical_grammar_matches_an_independently_encoded_fixture()
    {
        // Independently encoded strict UTF-8 lengths and big-endian UTC ticks 639029198450000000.
        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, Snapshot("memory-b"), Snapshot("memory-a"));

        Assert.Equal("1C0F46B841A1FF74AFA56DCD205FCCA732042D218B7BEC7303B4DD6164A90B50", receipt.InputHash);

        Assert.Equal("CC7B25C0544BF5754A0AE1952D4906B0761E6EBBB1554214CFCA67B6E1C548E0", receipt.ReceiptId);

        Assert.Equal("DE018082D0B9216DB91EB205826134C2B2EB18CDE7A270784967FBAE11E9714C", receipt.OutputHash);
    }

    [Fact]
    public void Receipt_identity_binds_the_declared_kind_and_exact_requested_survivor()
    {
        LongRestSnapshot[] snapshots = [Snapshot("memory-a"), Snapshot("memory-b")];

        LongRestRequest request = Request(LongRestTransformationKind.Supersession, snapshots)
            with { SurvivorVersionId = snapshots[0].VersionId };

        LongRestReceipt defaultWinner = LongRestPolicy.Evaluate(request, snapshots).Value;

        LongRestReceipt declaredWinner = LongRestPolicy.Evaluate(request with { SurvivorVersionId = snapshots[1].VersionId }, snapshots).Value;

        LongRestReceipt exact = Evaluate(LongRestTransformationKind.ExactDuplicates, snapshots);

        Assert.Equal(defaultWinner.InputHash, declaredWinner.InputHash);

        Assert.NotEqual(defaultWinner.ReceiptId, declaredWinner.ReceiptId);

        Assert.NotEqual(defaultWinner.OutputHash, declaredWinner.OutputHash);

        Assert.NotEqual(defaultWinner.ReceiptId, exact.ReceiptId);
    }

    [Theory]
    [InlineData(LongRestTransformationKind.ExactDuplicates)]
    [InlineData(LongRestTransformationKind.EquivalentObservations)]
    public void Only_supersession_may_declare_a_survivor_instead_of_using_deterministic_selection(LongRestTransformationKind kind)
    {
        LongRestSnapshot[] snapshots = [Snapshot("memory-a"), Snapshot("memory-b")];

        LongRestRequest request = Request(kind, snapshots) with { SurvivorVersionId = snapshots[1].VersionId };

        AssertFailure(LongRestPolicy.Evaluate(request, snapshots), "LongRest.InvalidRequest");
    }

    [Fact]
    public void A_survivor_without_an_embedding_cannot_consolidate_away_retrievable_sources()
    {
        LongRestSnapshot first = Snapshot("memory-a") with { HasEmbedding = false };

        LongRestSnapshot second = Snapshot("memory-b");

        LongRestReceipt receipt = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second);

        Assert.Equal(LongRestOutcome.NoChange, receipt.Outcome);

        Assert.Equal(LongRestReason.EmbeddingMissing, receipt.Reason);

        Assert.Empty(receipt.SupersededVersionIds);

        Assert.True(second.HasEmbedding);

        Assert.False(second.IsConsolidated);
    }

    [Fact]
    public void Every_retained_attribute_changes_canonical_input_identity()
    {
        LongRestSnapshot first = Snapshot("memory-a");

        LongRestSnapshot second = Snapshot("memory-b");

        LongRestSnapshot[] alternatives =
        [
            second with { ClaimId = "another-claim" },

            second with { VersionId = "another-version" },

            second with { Revision = 2 },

            second with { ContentHashFormat = AnnalContentHashFormat.LexiconStructuredSnapshot },

            second with { ContentHash = new string('A', 64) },

            second with { Origin = AnnalOrigin.OperatorStated },

            second with { SourceSessionId = Guid.Parse("12345678-1234-1234-1234-123456789012") },

            second with { ScopeKind = SagaMemoryScopeKind.Campaign, CampaignId = Guid.Parse("12345678-1234-1234-1234-123456789012") },

            second with { Sensitivity = ContentSensitivity.CovenantDerived },

            second with { ValidFromUtc = Instant.AddTicks(1) },

            second with { ValidToUtc = Instant.AddDays(1) },

            second with { RecordedAtUtc = Instant.AddTicks(1) },

            second with { RetiredAtUtc = Instant },

            second with { PinnedAtUtc = Instant },

            second with { HasEmbedding = false },

            second with { HasSensitivityLabel = true },

            second with { Dependencies = [new LongRestDependency("source", AnnalDependencyRelation.DerivedFrom, 1)] },

            second with { IncomingDependencies = [new LongRestIncomingDependency("dependent", AnnalDependencyRelation.Corroborates, true)] },
        ];

        LongRestReceipt baseline = Evaluate(LongRestTransformationKind.ExactDuplicates, first, second);

        foreach (LongRestSnapshot alternative in alternatives)
        {
            Assert.NotEqual(baseline.InputHash, Evaluate(LongRestTransformationKind.ExactDuplicates, first, alternative).InputHash);
        }
    }

    [Fact]
    public void Length_prefixes_prevent_control_character_identity_boundaries_from_colliding()
    {
        LongRestSnapshot first = Snapshot("a\u001Fb") with { ClaimId = "c" };

        LongRestSnapshot alternate = Snapshot("a") with { ClaimId = "b\u001Fc", VersionId = first.VersionId };

        LongRestReceipt original = Evaluate(LongRestTransformationKind.ExactDuplicates, first);

        LongRestReceipt changed = Evaluate(LongRestTransformationKind.ExactDuplicates, alternate);

        Assert.NotEqual(original.InputHash, changed.InputHash);

        Assert.NotEqual(original.ReceiptId, changed.ReceiptId);
    }

    [Fact]
    public void Bounded_dependency_evidence_refuses_overflow_duplicate_targets_and_unknown_relations()
    {
        LongRestSnapshot first = Snapshot("memory-a");

        LongRestSnapshot second = Snapshot("memory-b");

        LongRestSnapshot[] invalid =
        [
            second with
            {
                Dependencies = Enumerable.Range(1, 17)
                    .Select(index => new LongRestDependency($"source-{index}", AnnalDependencyRelation.DerivedFrom, index)).ToArray(),
            },

            second with
            {
                Dependencies = [new("source", AnnalDependencyRelation.DerivedFrom, 1), new("source", AnnalDependencyRelation.Corroborates, 2)],
            },

            second with { Dependencies = [new("source", (AnnalDependencyRelation)99, 1)] },

            second with
            {
                IncomingDependencies = Enumerable.Range(1, 17)
                    .Select(index => new LongRestIncomingDependency($"dependent-{index}", AnnalDependencyRelation.DerivedFrom, true)).ToArray(),
            },
        ];

        foreach (LongRestSnapshot snapshot in invalid)
        {
            AssertFailure(LongRestPolicy.Evaluate(Request(LongRestTransformationKind.ExactDuplicates, first, snapshot), [first, snapshot]),
                "LongRest.InvalidRequest");
        }
    }

    private static LongRestReceipt Evaluate(LongRestTransformationKind kind, params LongRestSnapshot[] snapshots)
    {
        Result<LongRestReceipt> result = LongRestPolicy.Evaluate(Request(kind, snapshots), snapshots);

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;
    }

    private static LongRestRequest Request(LongRestTransformationKind kind, params LongRestSnapshot[] snapshots) =>
        new(kind, snapshots.Select(snapshot => new LongRestTarget(snapshot.MemoryId, snapshot.VersionId, snapshot.ContentHash)).ToArray());

    private static LongRestSnapshot Snapshot(string id) =>
        new(id, $"claim-{id}", $"version-{id}", 1, AnnalContentHashFormat.LegacyStoreDigest, Digest,
            AnnalOrigin.AgentExtracted, null, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            Instant, null, Instant, null, null, true, false, true, false, false, [], []);

    private static void AssertFailure(Result<LongRestReceipt> result, string code)
    {
        Assert.True(result.IsFailure);

        Assert.Equal(code, result.Error.Code);
    }
}
