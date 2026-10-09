using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class SagaLongRestServiceTests
{
    private static readonly DateTimeOffset Recorded = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

    [SkippableFact]
    public async Task Exact_duplicates_converge_without_rewriting_source_claims_or_embeddings()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("b", "same conclusion"), ("a", "same conclusion"));

        SagaLongRestService service = new(h.Context, TimeProvider.System);

        Result<LongRestReceipt> result = await service.ApplyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");

        Assert.Equal(LongRestOutcome.Applied, result.Value.Outcome);

        Assert.Equal("a", result.Value.SurvivorMemoryId);

        Assert.Equal(2, await h.CountAsync("saga_memories", "1 = 1"));

        Assert.Equal(2, await h.CountAsync("saga_memory_embeddings", "1 = 1"));

        Assert.Equal(2, await h.CountAsync("annal_versions", "1 = 1"));

        Assert.Equal(1, await h.CountAsync("long_rest_receipts", "1 = 1"));

        Assert.Equal(1, await h.CountAsync("long_rest_suppressions", "1 = 1"));

        IReadOnlyDictionary<string, SagaMemoryDto> recalled = await h.Store.GetByIdsAsync(["a", "b"], CancellationToken.None);

        Assert.Equal(["a"], recalled.Keys);

        SagaMemoryDto[] inspected = await h.Store.ListAsync(null, null, MemoryScope.Installation, 10, 0, CancellationToken.None);

        Assert.Equal(2, inspected.Length);

        Assert.Equal("a", Assert.Single(inspected, m => m.Id == "b").ConsolidatedIntoMemoryId);
    }

    [SkippableFact]
    public async Task Applied_replay_and_receipt_inspection_perform_zero_database_writes()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("a", "same"), ("b", "same"));

        SagaLongRestService service = new(h.Context, TimeProvider.System);

        Result<LongRestReceipt> first = await service.ApplyAsync(request, CancellationToken.None);

        Assert.True(first.IsSuccess);

        long before = await ScalarAsync(h, "SELECT total_changes()");

        Result<LongRestReceipt> second = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(
            request with { Targets = request.Targets.Reverse().ToArray() }, CancellationToken.None);

        Result<LongRestReceipt> read = await service.GetReceiptAsync(first.Value.ReceiptId, CancellationToken.None);

        Assert.True(second.IsSuccess);

        Assert.True(read.IsSuccess);

        Assert.Equal(first.Value.ReceiptId, second.Value.ReceiptId);

        Assert.Equal(first.Value.OutputHash, read.Value.OutputHash);

        Assert.Equal(before, await ScalarAsync(h, "SELECT total_changes()"));
    }

    [SkippableFact]
    public async Task Pinned_input_writes_one_no_change_receipt_and_unpin_allows_a_distinct_transformation()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("a", "same"), ("b", "same"));

        _ = await h.Store.SetPinAsync("b", true, Recorded.AddDays(2), CancellationToken.None);

        SagaLongRestService service = new(h.Context, TimeProvider.System);

        Result<LongRestReceipt> first = await service.ApplyAsync(request, CancellationToken.None);

        Assert.True(first.IsSuccess);

        Assert.Equal(LongRestReason.Pinned, first.Value.Reason);

        Assert.Equal(0, await h.CountAsync("long_rest_suppressions", "1 = 1"));

        long before = await ScalarAsync(h, "SELECT total_changes()");

        _ = await service.ApplyAsync(request, CancellationToken.None);

        Assert.Equal(before, await ScalarAsync(h, "SELECT total_changes()"));

        _ = await h.Store.SetPinAsync("b", false, Recorded.AddDays(3), CancellationToken.None);

        Result<LongRestReceipt> after = await service.ApplyAsync(request, CancellationToken.None);

        Assert.True(after.IsSuccess);

        Assert.Equal(LongRestOutcome.Applied, after.Value.Outcome);

        Assert.NotEqual(first.Value.ReceiptId, after.Value.ReceiptId);
    }

    [SkippableFact]
    public async Task Equivalent_observations_preserve_each_content_and_supersession_names_the_exact_survivor()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("a", "old observation"), ("b", "replacement observation"));

        request = request with { Kind = LongRestTransformationKind.Supersession, SurvivorVersionId = request.Targets[1].ExpectedVersionId };

        Result<LongRestReceipt> result = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("b", result.Value.SurvivorMemoryId);

        SagaMemoryCurationRow? source = await h.Store.ReadCurationRowAsync("a", CancellationToken.None);

        Assert.NotNull(source);

        Assert.Equal("old observation", source.Memory.Content);

        Assert.Null(source.Lifecycle.RetiredAtUtc);

        Assert.Equal(SagaRetrievalEligibility.Consolidated, SagaRetrievalEligibilityClassifier.Classify(source, false));
    }

    [SkippableFact]
    public async Task Filtering_precedes_top_k_in_both_accelerator_states()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("a", "same"), ("b", "same"));

        _ = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(request, CancellationToken.None);

        DivinationService divination = new(h.Context, h.VectorAccelerator, NullLogger<DivinationService>.Instance);

        foreach (bool accelerated in new[] { false, true })
        {
            h.VectorAccelerator.SetAvailable(accelerated);

            Result<DivinationResult[]> hits = await divination.SearchAsync("saga_memory_embeddings_vec", "MemoryId", "Embedding",
                new Embedding<float>(h.Embedding()), 1, 0, CancellationToken.None);

            Assert.True(hits.IsSuccess, hits.IsFailure ? hits.Error.Message : "");

            Assert.Equal("a", Assert.Single(hits.Value).Id);
        }
    }

    [SkippableFact]
    public async Task Pinning_or_correcting_a_source_releases_only_its_exact_version_suppression()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("a", "same"), ("b", "same"));

        _ = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(request, CancellationToken.None);

        _ = await h.Store.SetPinAsync("b", true, Recorded.AddDays(3), CancellationToken.None);

        Assert.Equal(2, (await h.Store.GetByIdsAsync(["a", "b"], CancellationToken.None)).Count);

        _ = await h.Store.SetPinAsync("b", false, Recorded.AddDays(4), CancellationToken.None);

        _ = await h.Store.CorrectAsync("b", AnnalContentDigest.ForSagaMemory("same"), "changed", h.Embedding(), Recorded.AddDays(5), CancellationToken.None);

        Assert.Equal(2, (await h.Store.GetByIdsAsync(["a", "b"], CancellationToken.None)).Count);
    }

    [SkippableFact]
    public async Task Erasing_any_member_removes_the_whole_receipt_manifest_and_suppression()
    {
        foreach (string erased in new[] { "a", "b" })
        {
            await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

            LongRestRequest request = await SeedAsync(h, ("a", "same"), ("b", "same"));

            _ = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(request, CancellationToken.None);

            Assert.True(await h.Store.DeleteAsync(erased, CancellationToken.None));

            Assert.Equal(0, await h.CountAsync("long_rest_receipts", "1 = 1"));

            Assert.Equal(0, await h.CountAsync("long_rest_receipt_inputs", "1 = 1"));

            Assert.Equal(0, await h.CountAsync("long_rest_suppressions", "1 = 1"));

            Assert.Equal(1, await h.CountAsync("annal_claims", "1 = 1"));
        }
    }

    [SkippableFact]
    public async Task A_projection_failure_rolls_back_the_receipt_and_all_inputs()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("a", "same"), ("b", "same"));

        await ExecuteAsync(h, "CREATE TEMP TRIGGER reject_long_rest BEFORE INSERT ON long_rest_suppressions BEGIN SELECT RAISE(ABORT,'test projection failure'); END;");

        Result<LongRestReceipt> result = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(request, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, await h.CountAsync("long_rest_receipts", "1 = 1"));

        Assert.Equal(0, await h.CountAsync("long_rest_receipt_inputs", "1 = 1"));

        Assert.Equal(0, await h.CountAsync("long_rest_suppressions", "1 = 1"));

        Assert.Equal(2, await h.CountAsync("saga_memory_embeddings", "1 = 1"));
    }

    [SkippableFact]
    public async Task A_stale_or_claimless_target_cannot_create_a_receipt()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("a", "same"), ("b", "same"));

        _ = await h.Store.CorrectAsync("b", AnnalContentDigest.ForSagaMemory("same"), "changed", h.Embedding(), Recorded.AddDays(5), CancellationToken.None);

        Result<LongRestReceipt> result = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(request, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("LongRest.StaleInput", result.Error.Code);

        Assert.Equal(0, await h.CountAsync("long_rest_receipts", "1 = 1"));
    }

    [SkippableFact]
    public async Task A_retired_tombstone_receipt_binds_the_stored_row_without_changing_the_tombstone()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("a", "same"), ("b", "same"));

        _ = await h.Store.RetireAsync("b", AnnalContentDigest.ForSagaMemory("same"), Recorded.AddDays(2), CancellationToken.None);

        AnnalClaimHead? retired = await h.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "b", CancellationToken.None);

        Assert.NotNull(retired);

        request.Targets[1] = request.Targets[1] with { ExpectedVersionId = retired.CurrentVersionId };

        Result<LongRestReceipt> result = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");

        Assert.Equal(LongRestReason.Retired, result.Value.Reason);

        Assert.Equal(1, await ScalarAsync(h, "SELECT count(*) FROM annal_versions WHERE OperationCode = 3 AND ContentHash IS NULL"));

        Assert.Equal(0, await h.CountAsync("long_rest_suppressions", "1 = 1"));
    }

    [SkippableFact]
    public async Task A_live_artifact_label_blocks_consolidation_even_when_the_Annals_sensitivity_is_none()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        Guid protectedId = new("BBBBBBBB-0000-4000-8000-000000000001");

        LongRestRequest request = await SeedAsync(h, ("a", "same"), (protectedId.ToString("D"), "same"));

        await h.LabelSensitiveAsync(protectedId);

        Result<LongRestReceipt> result = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");

        Assert.Equal(LongRestReason.Protected, result.Value.Reason);

        Assert.Equal(0, await h.CountAsync("long_rest_suppressions", "1 = 1"));

        Assert.Equal(2, await h.CountAsync("saga_memory_embeddings", "1 = 1"));
    }

    [SkippableFact]
    public async Task Separate_campaigns_produce_a_scoped_no_change_receipt()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        List<LongRestTarget> targets = [];

        foreach (string id in new[] { "a", "b" })
        {
            Guid session = await h.SessionBoundToNewCampaignAsync();

            _ = await h.Store.InsertAsync(id, "same", Recorded, session, null, "extraction", h.Embedding(), CancellationToken.None);

            AnnalClaimHead? head = await h.Annals.GetClaimAsync(AnnalSubjectStore.Saga, id, CancellationToken.None);

            Assert.NotNull(head);

            targets.Add(new LongRestTarget(id, head.CurrentVersionId, Convert.ToHexString(AnnalContentDigest.ForSagaMemory("same"))));
        }

        Result<LongRestReceipt> result = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(
            new LongRestRequest(LongRestTransformationKind.ExactDuplicates, targets.ToArray()), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : "");

        Assert.Equal(LongRestReason.DifferentScope, result.Value.Reason);

        Assert.Equal(0, await h.CountAsync("long_rest_suppressions", "1 = 1"));
    }

    [SkippableFact]
    public async Task Equivalent_observations_converge_and_replay_preserves_the_original_historical_decision_after_correction()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = (await SeedAsync(h, ("a", "prefers tabs"), ("b", "prefers tab indentation")))
            with { Kind = LongRestTransformationKind.EquivalentObservations };

        SagaLongRestService service = new(h.Context, TimeProvider.System);

        Result<LongRestReceipt> first = await service.ApplyAsync(request, CancellationToken.None);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : "");

        Assert.Equal(LongRestOutcome.Applied, first.Value.Outcome);

        _ = await h.Store.CorrectAsync("b", AnnalContentDigest.ForSagaMemory("prefers tab indentation"), "prefers spaces", h.Embedding(), Recorded.AddDays(2), CancellationToken.None);

        long before = await ScalarAsync(h, "SELECT total_changes()");

        Result<LongRestReceipt> replay = await service.ApplyAsync(request, CancellationToken.None);

        Result<LongRestReceipt> inspected = await service.GetReceiptAsync(first.Value.ReceiptId, CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : "");

        Assert.True(inspected.IsSuccess);

        Assert.Equal(first.Value.OutputHash, replay.Value.OutputHash);

        Assert.Equal(first.Value.ReceiptId, inspected.Value.ReceiptId);

        Assert.Equal(before, await ScalarAsync(h, "SELECT total_changes()"));
    }

    [SkippableFact]
    public async Task Current_derived_claims_block_eliminating_their_exact_dependency()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest request = await SeedAsync(h, ("a", "same"), ("b", "same"));

        LongRestRequest dependent = await SeedAsync(h, ("c", "derived from b"));

        await using DbCommand edge = h.Connection.CreateCommand();

        edge.CommandText = """
            INSERT INTO annal_dependencies
                (DependentVersionId, DependentSequence, DependencyVersionId, DependencySequence, RelationCode, Ordinal, CreatedAtUtc)
            SELECT d.VersionId, d.Sequence, s.VersionId, s.Sequence, 2, 1, d.RecordedAtUtc
            FROM annal_versions d, annal_versions s WHERE d.VersionId = @dependent AND s.VersionId = @source
            """;

        DbParameter dep = edge.CreateParameter();

        dep.ParameterName = "@dependent";

        dep.Value = dependent.Targets[0].ExpectedVersionId;

        edge.Parameters.Add(dep);

        DbParameter source = edge.CreateParameter();

        source.ParameterName = "@source";

        source.Value = request.Targets[1].ExpectedVersionId;

        edge.Parameters.Add(source);

        _ = await edge.ExecuteNonQueryAsync();

        Result<LongRestReceipt> result = await new SagaLongRestService(h.Context, TimeProvider.System).ApplyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(LongRestReason.DependencyConflict, result.Value.Reason);

        Assert.Equal(1, await h.CountAsync("annal_dependencies", "1 = 1"));

        Assert.Equal(0, await h.CountAsync("long_rest_suppressions", "1 = 1"));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_no_change_receipt_cannot_freeze_eligibility_after_its_blocking_projection_is_erased(bool eliminatePriorOutput)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestRequest first = await SeedAsync(h, ("a", "same"), ("b", "same"));

        LongRestRequest third = await SeedAsync(h, ("c", "same"));

        SagaLongRestService service = new(h.Context, TimeProvider.System);

        if (eliminatePriorOutput)
        {
            first = first with { Kind = LongRestTransformationKind.Supersession, SurvivorVersionId = first.Targets[1].ExpectedVersionId };
        }

        Assert.Equal(LongRestOutcome.Applied, (await service.ApplyAsync(first, CancellationToken.None)).Value.Outcome);

        LongRestRequest next = new(
            eliminatePriorOutput ? LongRestTransformationKind.Supersession : LongRestTransformationKind.ExactDuplicates,
            [first.Targets[1], third.Targets[0]],
            eliminatePriorOutput ? third.Targets[0].ExpectedVersionId : null);

        Result<LongRestReceipt> blocked = await service.ApplyAsync(next, CancellationToken.None);

        LongRestReason originalReason = eliminatePriorOutput ? LongRestReason.DependencyConflict : LongRestReason.AlreadyConsolidated;

        Assert.Equal(originalReason, blocked.Value.Reason);

        Assert.True(await h.Store.DeleteAsync("a", CancellationToken.None));

        Result<LongRestReceipt> released = await service.ApplyAsync(next, CancellationToken.None);

        Assert.True(released.IsSuccess, released.IsFailure ? released.Error.Message : "");

        Assert.Equal(LongRestOutcome.Applied, released.Value.Outcome);

        Assert.NotEqual(blocked.Value.ReceiptId, released.Value.ReceiptId);

        Assert.Equal(eliminatePriorOutput ? "c" : "b", released.Value.SurvivorMemoryId);

        long beforeReplay = await ScalarAsync(h, "SELECT total_changes()");

        Result<LongRestReceipt> replay = await service.ApplyAsync(next, CancellationToken.None);

        Assert.Equal(released.Value.ReceiptId, replay.Value.ReceiptId);

        Assert.Equal(originalReason,
            (await service.GetReceiptAsync(blocked.Value.ReceiptId, CancellationToken.None)).Value.Reason);

        Assert.Equal(beforeReplay, await ScalarAsync(h, "SELECT total_changes()"));
    }

    [SkippableFact]
    public async Task A_protected_retirement_cannot_bind_a_caller_invented_content_hash()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        Guid id = Guid.NewGuid();

        LongRestRequest request = await SeedAsync(h, ("a", "same"), (id.ToString("D"), "same"));

        await h.LabelSensitiveAsync(id);

        _ = await h.Store.RetireAsync(id.ToString("D"), AnnalContentDigest.ForSagaMemory("same"), Recorded.AddDays(2), CancellationToken.None);

        AnnalClaimHead? retired = await h.Annals.GetClaimAsync(AnnalSubjectStore.Saga, id.ToString("D"), CancellationToken.None);

        Assert.NotNull(retired);

        LongRestTarget original = request.Targets[1] with { ExpectedVersionId = retired.CurrentVersionId };

        request.Targets[1] = original with { ExpectedContentHash = new string('F', 64) };

        SagaLongRestService service = new(h.Context, TimeProvider.System);

        Result<LongRestReceipt> invalid = await service.ApplyAsync(request, CancellationToken.None);

        Assert.True(invalid.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.StaleInput, invalid.Error.Code);

        Assert.Equal(0, await h.CountAsync("long_rest_receipts", "1 = 1"));

        request.Targets[1] = original;

        Result<LongRestReceipt> valid = await service.ApplyAsync(request, CancellationToken.None);

        Assert.True(valid.IsSuccess, valid.IsFailure ? valid.Error.Message : "");

        Assert.Equal(LongRestOutcome.NoChange, valid.Value.Outcome);

        Assert.Equal(LongRestReason.Retired, valid.Value.Reason);
    }

    private static async Task<LongRestRequest> SeedAsync(SagaStoreHarness h, params (string Id, string Content)[] memories)
    {
        List<LongRestTarget> targets = [];

        foreach ((string id, string content) in memories)
        {
            _ = await h.Store.InsertAsync(id, content, Recorded, null, null, "extraction", h.Embedding(), CancellationToken.None);

            AnnalClaimHead? head = await h.Annals.GetClaimAsync(AnnalSubjectStore.Saga, id, CancellationToken.None);

            Assert.NotNull(head);

            targets.Add(new LongRestTarget(id, head.CurrentVersionId, Convert.ToHexString(AnnalContentDigest.ForSagaMemory(content))));
        }

        return new LongRestRequest(LongRestTransformationKind.ExactDuplicates, targets.ToArray());
    }

    private static async Task<long> ScalarAsync(SagaStoreHarness h, string sql)
    {
        await using DbCommand cmd = h.Connection.CreateCommand();

        cmd.CommandText = sql;

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SagaStoreHarness h, string sql)
    {
        await using DbCommand cmd = h.Connection.CreateCommand();

        cmd.CommandText = sql;

        _ = await cmd.ExecuteNonQueryAsync();
    }
}
