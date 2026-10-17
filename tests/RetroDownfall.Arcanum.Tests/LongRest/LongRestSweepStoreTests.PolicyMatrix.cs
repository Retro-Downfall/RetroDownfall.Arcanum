using System.Collections.Immutable;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    [Theory]
    [InlineData("pin", LongRestReason.Pinned)]
    [InlineData("retire", LongRestReason.Retired)]
    [InlineData("embedding", LongRestReason.EmbeddingMissing)]
    [InlineData("validity", LongRestReason.IncompatibleValidity)]
    [InlineData("dependency", LongRestReason.DependencyConflict)]
    [InlineData("incoming", LongRestReason.DependencyConflict)]
    [InlineData("output", LongRestReason.DependencyConflict)]
    public async Task Protected_canonical_store_preserves_conservative_policy_and_unchanged_NoChange_replay(string fault, LongRestReason reason)
    {
        // A pinned or retired memory is never captured, so a sweep cannot meet one; the kernel still refuses it, and that refusal is what this
        // case holds. Its lifecycle is therefore changed after the leaves are read, and the input is rebuilt from the live canonical snapshot.
        bool lifecycle = fault is "pin" or "retire";

        await using Fixture fixture = await Fixture.CreateAsync(true, duplicates: true, policyFault: lifecycle ? null : fault);

        _ = await fixture.BeginAsync();

        ImmutableArray<LongRestSourceLeaf> inputs;

        LongRestWorkIdentity work;

        await using (SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: false))
        {
            _ = await LongRestAttemptStore.ValidatePendingClaimWithinTransactionAsync(fixture.H.Connection, transaction, fixture.Capability!.Claim,
                fixture.Generation.Fence.CanonicalScope, DateTimeOffset.UtcNow, CancellationToken.None);

            inputs = [await LongRestDiscoveryStore.ReadSweepLeafAsync(fixture.H.Connection, transaction, fixture.Generation, "10000000-0000-0000-0000-000000000001", fixture.Capability, CancellationToken.None),
                await LongRestDiscoveryStore.ReadSweepLeafAsync(fixture.H.Connection, transaction, fixture.Generation, "10000000-0000-0000-0000-000000000002", fixture.Capability, CancellationToken.None)];

            await transaction.CommitAsync();
        }

        if (lifecycle)
        {
            if (fault == "pin")
            {
                await fixture.H.Saga.Store.SetPinAsync("10000000-0000-0000-0000-000000000001", true, DateTimeOffset.UtcNow, CancellationToken.None);
            }
            else
            {
                await fixture.H.Saga.Store.RetireAsync("10000000-0000-0000-0000-000000000001", AnnalContentDigest.ForSagaMemory("identical observation"), DateTimeOffset.UtcNow, CancellationToken.None);
            }

            // A retirement advances the head, so the live target is read again rather than carried over.
            string head = (string)(await fixture.H.ScalarAsync("SELECT h.CurrentVersionId FROM annal_claims c JOIN annal_heads h ON h.ClaimId = c.ClaimId WHERE c.SubjectId = '10000000-0000-0000-0000-000000000001';"))!;

            LongRestTarget current = new(inputs[0].Target.MemoryId, head, inputs[0].Target.ExpectedContentHash);

            await using SqliteTransaction read = fixture.H.Connection.BeginTransaction(deferred: false);

            var live = LongRestSweepHarness.Require(await SagaLongRestService.ReadSnapshotAsync(fixture.H.Connection, read, current, CancellationToken.None, materializeContent: false));

            inputs = [inputs[0] with { Snapshot = live.Snapshot }, inputs[1]];
        }

        await using (SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: false))
        {
            work = new(fixture.Generation.GenerationId, fixture.Generation.OperationId, "canonical-matrix", LongRestWorkKind.Anchor, fixture.Generation.Scope,
                fixture.Generation.Fence, LongRestDiscoveryHash.SourcePage(fixture.Generation.Scope, inputs).Value, LongRestDiscoveryHash.Recipe(fixture.Generation.Recipe).Value, 920);

            await using SqliteCommand unit = fixture.H.Connection.CreateCommand();

            unit.Transaction = transaction;

            unit.CommandText = "INSERT INTO long_rest_work_items (WorkItemId, WorkDigest, GenerationId, OperationId, KindCode, ItemId, SourceDigest, RecipeDigest, PairingFloorPermille, Revision, DispositionCode) VALUES ($unit, $work, $generation, $operation, 3, 'canonical-matrix', $source, $recipe, 920, 0, 1);";

            string id = Guid.NewGuid().ToString("D").ToUpperInvariant();

            unit.Parameters.AddWithValue("$unit", id);

            unit.Parameters.AddWithValue("$work", LongRestDiscoveryHash.Work(work).Value.Bytes);

            unit.Parameters.AddWithValue("$generation", work.GenerationId.ToString("D").ToUpperInvariant());

            unit.Parameters.AddWithValue("$operation", work.OperationId.ToString("N"));

            unit.Parameters.AddWithValue("$source", work.SourceDigest.Bytes);

            unit.Parameters.AddWithValue("$recipe", work.RecipeDigest.Bytes);

            await unit.ExecuteNonQueryAsync();

            for (int index = 0; index < inputs.Length; index++)
            {
                unit.Parameters.Clear();

                unit.CommandText = "INSERT INTO long_rest_work_inputs VALUES ($unit, $ordinal, $memory, $version, $json);";

                unit.Parameters.AddWithValue("$unit", id);

                unit.Parameters.AddWithValue("$ordinal", index + 1);

                unit.Parameters.AddWithValue("$memory", inputs[index].Snapshot.MemoryId);

                unit.Parameters.AddWithValue("$version", inputs[index].Snapshot.VersionId);

                unit.Parameters.AddWithValue("$json", JsonSerializer.Serialize(inputs[index] with { Content = null, Vector = [] }, LongRestJsonContext.Default.LongRestSourceLeaf));

                await unit.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        string before = await CanonicalSourceStateAsync(fixture.H);

        long suppressions = (long)(await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"))!;

        LongRestRequest request = new(LongRestTransformationKind.EquivalentObservations, inputs.Select(leaf => leaf.Target).ToArray());

        LongRestProtectedEvidence protection = new(work.Scope.ProtectedPartitionDigest!.Value, inputs[0].Provenance, [.. inputs.SelectMany(leaf => leaf.LabelDigests)]);

        SagaLongRestService service = new(fixture.H.Saga.Context, TimeProvider.System);

        LongRestReceipt result;

        await using (SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: false))
        {
            result = LongRestSweepHarness.Require(await service.ApplyInTransactionAsync(fixture.H.Connection, transaction, request, protection, new(work, inputs), CancellationToken.None));

            await transaction.CommitAsync();
        }

        Assert.Equal(LongRestOutcome.NoChange, result.Outcome);

        Assert.Equal(reason, result.Reason);

        Assert.Equal(2, result.PolicyVersion);

        Assert.Equal(suppressions, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(before, await CanonicalSourceStateAsync(fixture.H));

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        await using (SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: false))
        {
            Assert.Equal(result.ReceiptId, LongRestSweepHarness.Require(await service.ApplyInTransactionAsync(fixture.H.Connection, transaction, request, protection, new(work, inputs), CancellationToken.None)).ReceiptId);

            await transaction.CommitAsync();
        }

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(before, await CanonicalSourceStateAsync(fixture.H));
    }

    private static async Task<string> CanonicalSourceStateAsync(LongRestSweepHarness h)
    {
        List<string> rows = [];

        foreach (string table in new[] { "saga_memories", "saga_memory_embeddings", "annal_claims", "annal_heads", "annal_versions", "annal_dependencies", "artifact_sensitivity" })
        {
            await using SqliteCommand command = h.Connection.CreateCommand();

            command.CommandText = $"SELECT * FROM {table} ORDER BY 1;";

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                for (int index = 0; index < reader.FieldCount; index++)
                {
                    object value = reader.GetValue(index);

                    string text = value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;

                    rows.Add(table + ":" + index + ":" + value.GetType().Name + ":" + text.Length + ":" + text);
                }
            }
        }

        return string.Join("\n", rows);
    }

    private sealed partial class Fixture
    {
        private static async Task PreparePolicyFaultAsync(LongRestSweepHarness h, string? fault)
        {
            const string first = "10000000-0000-0000-0000-000000000001";

            const string second = "10000000-0000-0000-0000-000000000002";

            const string third = "10000000-0000-0000-0000-000000000003";

            if (fault == "pin")
            {
                await h.Saga.Store.SetPinAsync(first, true, DateTimeOffset.UtcNow, CancellationToken.None);
            }
            else if (fault == "retire")
            {
                await h.Saga.Store.RetireAsync(first, AnnalContentDigest.ForSagaMemory("identical observation"), DateTimeOffset.UtcNow, CancellationToken.None);
            }
            else if (fault == "embedding")
            {
                await h.ExecuteAsync("DELETE FROM saga_memory_embeddings WHERE MemoryId = $id;", ("$id", first));
            }
            else if (fault == "validity")
            {
                string version = Guid.NewGuid().ToString("D");

                await h.ExecuteAsync("""
                    INSERT INTO annal_versions (VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, CampaignId,
                        SensitivityCode, ContentHash, ValidFromUtc, ValidToUtc, RecordedAtUtc, PredecessorVersionId, SourceSessionId, ContentHashFormatCode)
                    SELECT $version, v.ClaimId, v.Revision + 1, 2, v.OriginCode, v.ScopeKindCode, v.CampaignId, v.SensitivityCode, v.ContentHash,
                        v.ValidFromUtc, '2026-10-11T00:00:00.0000000Z', '2026-10-11T00:00:00.0000000Z', v.VersionId, v.SourceSessionId, v.ContentHashFormatCode
                    FROM annal_versions v JOIN annal_heads head ON head.CurrentVersionId = v.VersionId
                    JOIN annal_claims claim ON claim.ClaimId = v.ClaimId WHERE claim.SubjectId = $id;
                    UPDATE annal_heads SET CurrentVersionId = $version, CurrentRevision = CurrentRevision + 1, CurrentOperationCode = 2,
                        UpdatedAtUtc = '2026-10-11T00:00:00.0000000Z' WHERE ClaimId = (SELECT ClaimId FROM annal_versions WHERE VersionId = $version);
                    """, ("$version", version), ("$id", second));
            }
            else if (fault is "dependency" or "incoming")
            {
                if (fault == "incoming")
                {
                    await h.SeedAsync(third, "current derived claim");
                }

                await h.ExecuteAsync("""
                    INSERT INTO annal_dependencies (DependentVersionId, DependentSequence, DependencyVersionId, DependencySequence, RelationCode, Ordinal, CreatedAtUtc)
                    SELECT newer.VersionId, newer.Sequence, older.VersionId, older.Sequence, $relation, 1, '2026-10-10T00:00:00.0000000Z'
                    FROM annal_versions newer JOIN annal_claims nc ON nc.ClaimId = newer.ClaimId
                    JOIN annal_versions older JOIN annal_claims oc ON oc.ClaimId = older.ClaimId
                    WHERE nc.SubjectId = $new AND oc.SubjectId = $old;
                    """, ("$new", fault == "incoming" ? third : second), ("$old", fault == "incoming" ? second : first), ("$relation", fault == "incoming" ? 2 : 3));
            }
            else if (fault == "separate-fourth")
            {
                // The fourth memory gets an axis of its own, so no vector connects it to the others.
                float[] vector = new float[64];

                vector[1] = 1;

                await h.SetEmbeddingAsync("10000000-0000-0000-0000-000000000004", vector);
            }
            else if (fault == "output")
            {
                await h.SeedAsync(third, "identical observation");

                LongRestTarget survivor = await h.TargetAsync(second);

                LongRestSweepHarness.Require(await new SagaLongRestService(h.Saga.Context, TimeProvider.System).ApplyAsync(
                    new(LongRestTransformationKind.Supersession, [survivor, await h.TargetAsync(third)], survivor.ExpectedVersionId), CancellationToken.None));
            }
        }
    }
}
