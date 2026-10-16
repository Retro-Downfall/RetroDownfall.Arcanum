using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestSweepStore
{
    // What one read counted while it ran. A read can run more than once (a busy retry) and can fail after counting (a rollback), so the
    // numbers are kept here and published only when the transaction that produced them has committed.
    private sealed class PendingWorkCounts
    {
        internal int Oversize;

        internal int PackLimit;

        internal int Unpaired;

        internal int Incohesive;

        internal int Reused;

        // The floors the anchors of this read were queued under: one value each, published as a histogram of permille with no identity.
        internal readonly List<int> Floors = [];

        internal void Reset()
        {
            Oversize = 0;

            PackLimit = 0;

            Unpaired = 0;

            Incohesive = 0;

            Reused = 0;

            Floors.Clear();
        }

        internal void Publish()
        {
            if (Oversize > 0)
            {
                ArcanumMetrics.LongRestPackSkippedCandidatesTotal.Add(Oversize, new KeyValuePair<string, object?>("reason", "oversize"));
            }

            if (Unpaired > 0)
            {
                ArcanumMetrics.LongRestPackSkippedCandidatesTotal.Add(Unpaired, new KeyValuePair<string, object?>("reason", "unpaired"));
            }

            if (Incohesive > 0)
            {
                ArcanumMetrics.LongRestPackSkippedCandidatesTotal.Add(Incohesive, new KeyValuePair<string, object?>("reason", "incohesive"));
            }

            if (PackLimit > 0)
            {
                ArcanumMetrics.LongRestPackSkippedCandidatesTotal.Add(PackLimit, new KeyValuePair<string, object?>("reason", "pack_limit"));
            }

            foreach (int floor in Floors)
            {
                ArcanumMetrics.LongRestPairingFloorAnchorsTotal.Add(1, new KeyValuePair<string, object?>("floor_le_permille", LongRestPairing.FloorBucket(floor)));
            }

            if (Reused > 0)
            {
                ArcanumMetrics.LongRestReusedDecisionsTotal.Add(Reused);
            }
        }
    }

    public async Task<Result<LongRestSweepWork?>> ReadNextWorkAsync(LongRestSweep expected, LongRestMaintenanceCapability? capability, CancellationToken ct)
    {
        PendingWorkCounts counts = new();

        Result<LongRestSweepWork?> read = await ReadNextWorkWithinAsync(expected, capability, counts, ct).ConfigureAwait(false);

        if (read.IsSuccess)
        {
            counts.Publish();
        }

        return read;
    }

    private Task<Result<LongRestSweepWork?>> ReadNextWorkWithinAsync(LongRestSweep expected, LongRestMaintenanceCapability? capability,
        PendingWorkCounts counts, CancellationToken ct) =>
        WithinAsync<LongRestSweepWork?>(async (connection, transaction) =>
    {
        counts.Reset();

        LongRestDiscoveryGeneration generation = await RequireGenerationAsync(connection, transaction, expected, capability, LongRestVerificationDepth.Step, ct).ConfigureAwait(false);

        if (expected.Phase == LongRestSweepPhase.Completed)
        {
            return null;
        }

        string? storedDiscoveryCursor;

        string? cursor;

        await using (SqliteCommand scan = Command(connection, transaction, """
            SELECT DiscoveryCursor, CASE WHEN DiscoveryCursor IS NULL OR CommittedItemCursor > DiscoveryCursor
                THEN CommittedItemCursor ELSE DiscoveryCursor END FROM long_rest_sweeps
            WHERE OperationId = $operation AND GenerationId = $generation AND Revision = $revision;
            """, ("$operation", expected.OperationId.ToString("N")), ("$generation", Format(expected.GenerationId)), ("$revision", expected.Revision)))
        {
            await using SqliteDataReader reader = await scan.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidDataException();

            storedDiscoveryCursor = reader.IsDBNull(0) ? null : reader.GetString(0);

            cursor = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        int remaining = LongRestDiscoveryLimits.SourcePage;

        while (true)
        {
            string? pending;

            await using (SqliteCommand command = Command(connection, transaction, "SELECT ItemId FROM long_rest_work_items WHERE GenerationId = $generation AND OperationId = $operation AND KindCode = 3 AND DispositionCode = 1 ORDER BY ItemId LIMIT 1;",
                ("$generation", Format(generation.GenerationId)), ("$operation", generation.OperationId.ToString("N"))))
            {
                pending = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }

            if (pending is not null)
            {
                LongRestSweepWork current = await ResolveAsync(connection, transaction, generation, expected.Revision, pending, capability, ct).ConfigureAwait(false);

                if (current.AcceptedAdjudication is not null || !DisposeWithoutAdjudication(current))
                {
                    return current;
                }

                _ = await FinishItemAsync(connection, transaction, expected, current, null, ct).ConfigureAwait(false);

                return null;
            }

            while (true)
            {
                string? memory;

                await using (SqliteCommand command = Command(connection, transaction, "SELECT MemoryId FROM long_rest_discovery_leaves WHERE GenerationId = $generation AND ($cursor IS NULL OR MemoryId > $cursor) ORDER BY MemoryId LIMIT 1;", ("$generation", Format(generation.GenerationId)), ("$cursor", cursor)))
                {
                    memory = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                }

                if (memory is null)
                {
                    await AdvanceDiscoveryAsync(connection, transaction, expected, generation, storedDiscoveryCursor, cursor,
                        complete: true, capability, ct).ConfigureAwait(false);

                    return null;
                }

                LongRestSourceLeaf anchor = await LongRestDiscoveryStore.ReadSweepLeafAsync(connection, transaction, generation, memory, capability, ct).ConfigureAwait(false);

                cursor = memory;

                remaining--;

                if (!anchor.Snapshot.IsCurrent || anchor.Snapshot.IsConsolidated || anchor.Snapshot.PinnedAtUtc is not null || anchor.Snapshot.RetiredAtUtc is not null)
                {
                    if (remaining == 0)
                    {
                        await AdvanceDiscoveryAsync(connection, transaction, expected, generation, storedDiscoveryCursor, cursor,
                            complete: false, capability, ct).ConfigureAwait(false);

                        return null;
                    }

                    continue;
                }

                Result<LongRestCandidateGroup[]> found = await LongRestDiscoveryStore.DiscoverWithinAsync(connection, transaction, generation.GenerationId, anchor.Target, capability, ct).ConfigureAwait(false);

                if (found.IsFailure)
                {
                    throw new InvalidDataException();
                }

                (ImmutableArray<ImmutableArray<LongRestSourceLeaf>> packs, int floor) = await PackAsync(connection, transaction, generation, anchor, found.Value, capability, counts, ct).ConfigureAwait(false);

                if (packs.Length == 0)
                {
                    await AdvanceDiscoveryAsync(connection, transaction, expected, generation, storedDiscoveryCursor, cursor,
                        complete: false, capability, ct).ConfigureAwait(false);

                    return null;
                }

                string prefix = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(memory)));

                int queued = 0;

                for (int index = 0; index < packs.Length; index++)
                {
                    ImmutableArray<LongRestSourceLeaf> inputs = packs[index];

                    LongRestWorkIdentity work = new(generation.GenerationId, generation.OperationId, prefix + ":" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                        LongRestWorkKind.Anchor, generation.Scope, generation.Fence, LongRestDiscoveryHash.SourcePage(generation.Scope, inputs).Value, LongRestDiscoveryHash.Recipe(generation.Recipe).Value,
                        floor);

                    // The digest is of the set, whichever member it was found from, and the recipe binds the request text, so a set this generation has
                    // already decided NoChange under this recipe is the same question with the same answer: no work item and no request. Only that
                    // decision is reused: an applied one has consolidated its members, which changes the set's digest.
                    bool decided;

                    await using (SqliteCommand lookup = Command(connection, transaction, """
                        SELECT EXISTS (SELECT 1 FROM long_rest_work_items item JOIN long_rest_adjudications accepted ON accepted.WorkItemId = item.WorkItemId
                            WHERE item.GenerationId = $generation AND item.KindCode = 3 AND item.DispositionCode = 2
                                AND item.SourceDigest = $source AND item.RecipeDigest = $recipe AND accepted.DecisionKindCode = 3);
                        """, ("$generation", Format(generation.GenerationId)), ("$source", work.SourceDigest.Bytes), ("$recipe", work.RecipeDigest.Bytes)))
                    {
                        decided = (long)(await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 1;
                    }

                    if (decided)
                    {
                        counts.Reused++;

                        continue;
                    }

                    queued++;

                    string unit = Format(Guid.NewGuid());

                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO long_rest_work_items (WorkItemId, WorkDigest, GenerationId, OperationId, KindCode, ItemId, SourceDigest, RecipeDigest, PairingFloorPermille, Revision, DispositionCode)
                        VALUES ($unit, $work, $generation, $operation, 3, $item, $source, $recipe, $floor, 0, 1);
                        """, ct, ("$unit", unit), ("$work", LongRestDiscoveryHash.Work(work).Value.Bytes), ("$generation", Format(work.GenerationId)),
                        ("$operation", work.OperationId.ToString("N")), ("$item", work.ItemId), ("$source", work.SourceDigest.Bytes), ("$recipe", work.RecipeDigest.Bytes),
                        ("$floor", work.PairingFloorPermille)).ConfigureAwait(false);

                    for (int ordinal = 0; ordinal < inputs.Length; ordinal++)
                    {
                        await ExecuteAsync(connection, transaction, "INSERT INTO long_rest_work_inputs (WorkItemId, Ordinal, MemoryId, VersionId, SourceEvidenceJson) VALUES ($unit, $ordinal, $memory, $version, $json);", ct,
                            ("$unit", unit), ("$ordinal", ordinal + 1), ("$memory", inputs[ordinal].Snapshot.MemoryId), ("$version", inputs[ordinal].Snapshot.VersionId), ("$json", LeafJson(inputs[ordinal]))).ConfigureAwait(false);
                    }

                    pending ??= work.ItemId;
                }

                if (queued == 0)
                {
                    await AdvanceDiscoveryAsync(connection, transaction, expected, generation, storedDiscoveryCursor, cursor,
                        complete: false, capability, ct).ConfigureAwait(false);

                    return null;
                }

                break;
            }
        }
    }, ct);

    /// <summary>
    /// Loads the exact leaves of every nominated neighbour and packs them behind the anchor: duplicates first, then cliques, then pairs, each
    /// within the recipe's input budget and every pair of memories in a clique unit paired at the anchor's floor (<see cref="LongRestGroupPacker"/>).
    /// A neighbour left out is counted by reason and never by identity, in <paramref name="counts"/>, which the caller publishes once the
    /// transaction has committed. The floor is the one discovery computed from the generation; every group carries the same one.
    /// </summary>
    private static async Task<(ImmutableArray<ImmutableArray<LongRestSourceLeaf>> Packs, int Floor)> PackAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestSourceLeaf anchor, LongRestCandidateGroup[] groups, LongRestMaintenanceCapability? capability,
        PendingWorkCounts counts, CancellationToken ct)
    {
        ImmutableArray<LongRestSourceLeaf>.Builder neighbors = ImmutableArray.CreateBuilder<LongRestSourceLeaf>();

        if (groups.Length == 0)
        {
            return ([], 0);
        }

        int floor = groups[0].PairingFloorPermille;

        foreach (LongRestCandidateGroup group in groups)
        {
            if (group.Targets.Length < 2 || group.Targets[0] != anchor.Target || group.PairingFloorPermille != floor)
            {
                throw new InvalidDataException();
            }

            for (int ordinal = 1; ordinal < group.Targets.Length; ordinal++)
            {
                LongRestTarget target = group.Targets[ordinal];

                LongRestSourceLeaf leaf = await LongRestDiscoveryStore.ReadSweepLeafAsync(connection, transaction, generation, target.MemoryId, capability, ct).ConfigureAwait(false);

                if (leaf.Target != target)
                {
                    throw new InvalidDataException();
                }

                neighbors.Add(leaf);
            }
        }

        Result<LongRestGroupPacking> packing = LongRestGroupPacker.Pack(anchor, neighbors.ToImmutable(), generation.Recipe.ProviderInputUtf8Bytes, floor);

        if (packing.IsFailure)
        {
            throw new InvalidDataException();
        }

        counts.Oversize += packing.Value.SkippedOversize;

        counts.PackLimit += packing.Value.SkippedPackLimit;

        counts.Unpaired += packing.Value.SkippedUnpaired;

        counts.Incohesive += packing.Value.SkippedIncohesive;

        counts.Floors.Add(floor);

        return (packing.Value.Packs, floor);
    }

    private async Task AdvanceDiscoveryAsync(SqliteConnection connection, SqliteTransaction transaction, LongRestSweep expected,
        LongRestDiscoveryGeneration generation, string? previous, string? cursor, bool complete,
        LongRestMaintenanceCapability? capability, CancellationToken ct)
    {
        await ValidateClaimAsync(connection, transaction, generation, capability, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        if (complete)
        {
            // Completion is a seam. Every step before it took the corpus on the evidence of triggers, its inputs and one rotating window of
            // leaves; the sweep does not close on that. Every leaf is re-proved in full, in the transaction that closes it.
            await LongRestDiscoveryStore.ValidateSweepGenerationAsync(connection, transaction, generation, capability, LongRestVerificationDepth.Seam, ct).ConfigureAwait(false);
        }

        await using SqliteCommand advance = Command(connection, transaction, """
            UPDATE long_rest_sweeps SET DiscoveryCursor = $cursor, PhaseCode = $phase, ReadmitCount = 0, Revision = Revision + 1, UpdatedAtUtc = $at
            WHERE OperationId = $operation AND GenerationId = $generation AND Revision = $revision AND PhaseCode = $previousPhase
                AND DiscoveryCursor IS $previous
                AND NOT EXISTS (SELECT 1 FROM long_rest_work_items WHERE GenerationId = $generation AND OperationId = $operation
                    AND KindCode = 3 AND DispositionCode = 1)
                AND EXISTS (SELECT 1 FROM long_rest_discovery_generations generation WHERE generation.GenerationId = $generation
                    AND generation.OperationId = $operation AND generation.Revision = $generationRevision AND generation.PhaseCode = $generationPhase
                    AND generation.PhaseCode IN (4, 5) AND generation.Published = 1);
            """, ("$cursor", cursor), ("$phase", (int)(complete ? LongRestSweepPhase.Completed : expected.Phase)),
            ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())), ("$operation", expected.OperationId.ToString("N")),
            ("$generation", Format(expected.GenerationId)), ("$revision", expected.Revision), ("$previousPhase", (int)expected.Phase),
            ("$previous", previous), ("$generationRevision", generation.Revision), ("$generationPhase", (int)generation.Phase));

        if (await advance.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();

        if (complete)
        {
            await using SqliteCommand finish = Command(connection, transaction, """
                UPDATE long_rest_discovery_generations SET PhaseCode = 6, Revision = Revision + 1
                WHERE GenerationId = $generation AND OperationId = $operation AND Revision = $revision AND PhaseCode = $phase AND Published = 1;
                """, ("$generation", Format(generation.GenerationId)), ("$operation", expected.OperationId.ToString("N")),
                ("$revision", generation.Revision), ("$phase", (int)generation.Phase));

            if (await finish.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();
        }
    }

    internal static Task<LongRestSweepWork> VerifyCleanReadmissionWorkAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string item, CancellationToken ct) =>
        ResolveAsync(connection, transaction, generation, 0, item, null, ct, verificationOnly: true);

    private static async Task<LongRestSweepWork> ResolveAsync(SqliteConnection connection, SqliteTransaction transaction, LongRestDiscoveryGeneration generation,
        long revision, string item, LongRestMaintenanceCapability? capability, CancellationToken ct, bool verificationOnly = false)
    {
        List<(int Ordinal, string Memory, string Version, string Json)> stored = [];

        string unit;

        CovenantDigest source;

        int floor;

        await using (SqliteCommand command = Command(connection, transaction, "SELECT WorkItemId, SourceDigest, RecipeDigest, PairingFloorPermille FROM long_rest_work_items WHERE GenerationId = $generation AND OperationId = $operation AND KindCode = 3 AND ItemId = $item AND DispositionCode = 1;",
            ("$generation", Format(generation.GenerationId)), ("$operation", generation.OperationId.ToString("N")), ("$item", item)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || new CovenantDigest((byte[])reader[2]) != LongRestDiscoveryHash.Recipe(generation.Recipe).Value)
            {
                throw new InvalidDataException();
            }

            unit = reader.GetString(0);

            source = new((byte[])reader[1]);

            floor = reader.GetInt32(3);
        }

        await using (SqliteCommand command = Command(connection, transaction, "SELECT Ordinal, MemoryId, VersionId, SourceEvidenceJson FROM long_rest_work_inputs WHERE WorkItemId = $unit ORDER BY Ordinal LIMIT 17;", ("$unit", unit)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                stored.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        if (stored.Count is < 2 or > LongRestPolicy.MaxTargets)
        {
            throw new InvalidDataException();
        }

        ImmutableArray<LongRestSourceLeaf> retainedLeaves = [.. stored.Select(value => JsonSerializer.Deserialize(value.Json, LongRestJsonContext.Default.LongRestSourceLeaf) ?? throw new InvalidDataException())];

        if (LongRestDiscoveryHash.SourcePage(generation.Scope, retainedLeaves).Value != source)
        {
            throw new InvalidDataException();
        }

        ImmutableArray<LongRestSourceLeaf>.Builder leaves = ImmutableArray.CreateBuilder<LongRestSourceLeaf>();

        bool changed = false;

        foreach (var retained in stored)
        {
            LongRestSourceLeaf current = await LongRestDiscoveryStore.ReadSweepLeafAsync(connection, transaction, generation, retained.Memory, capability, ct).ConfigureAwait(false);

            LongRestSourceLeaf previous = retainedLeaves[leaves.Count];

            LongRestSourceLeaf comparable = current with { Snapshot = current.Snapshot with
            {
                IsConsolidated = previous.Snapshot.IsConsolidated,
                IsTransformationOutput = previous.Snapshot.IsTransformationOutput,
            } };

            if (retained.Ordinal != leaves.Count + 1 || current.Snapshot.VersionId != retained.Version || LeafJson(comparable) != retained.Json
                || (previous.Snapshot.IsConsolidated && !current.Snapshot.IsConsolidated)
                || (previous.Snapshot.IsTransformationOutput && !current.Snapshot.IsTransformationOutput))
            {
                throw new InvalidDataException();
            }

            changed |= LeafJson(current) != retained.Json;

            leaves.Add(current);
        }

        ImmutableArray<LongRestSourceLeaf> inputs = leaves.ToImmutable();

        if (changed)
        {
            if (verificationOnly) throw new InvalidDataException();

            await using SqliteCommand acceptedProof = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM long_rest_adjudications WHERE WorkItemId = $unit);", ("$unit", unit));

            if ((long)(await acceptedProof.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 0)
            {
                throw new InvalidDataException();
            }

            source = LongRestDiscoveryHash.SourcePage(generation.Scope, inputs).Value;

            for (int index = 0; index < inputs.Length; index++)
            {
                await ExecuteAsync(connection, transaction, "UPDATE long_rest_work_inputs SET SourceEvidenceJson = $json WHERE WorkItemId = $unit AND Ordinal = $ordinal;", ct,
                    ("$json", LeafJson(inputs[index])), ("$unit", unit), ("$ordinal", index + 1)).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction, "UPDATE long_rest_work_items SET SourceDigest = $source WHERE WorkItemId = $unit AND DispositionCode = 1;", ct,
                ("$source", source.Bytes), ("$unit", unit)).ConfigureAwait(false);
        }

        LongRestWorkIdentity work = new(generation.GenerationId, generation.OperationId, item, LongRestWorkKind.Anchor,
            generation.Scope, generation.Fence, source, LongRestDiscoveryHash.Recipe(generation.Recipe).Value, floor);

        if (capability is not null && (await capability.ValidateWorkAsync(work, work.Fence.Configuration, ct).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        LongRestAdjudication? accepted = await ReadAcceptedAsync(connection, transaction, unit, work, generation.Recipe, ct).ConfigureAwait(false);

        if (verificationOnly) return new(work, revision, inputs, null, accepted);

        await ExecuteAsync(connection, transaction, "UPDATE long_rest_work_items SET WorkDigest = $work, Revision = Revision + 1 WHERE WorkItemId = $unit AND WorkDigest <> $work AND DispositionCode = 1;", ct,
            ("$unit", unit), ("$work", LongRestDiscoveryHash.Work(work).Value.Bytes)).ConfigureAwait(false);

        await LongRestDiscoveryStore.ValidateCurrentSweepWorkAsync(connection, transaction, work, inputs, ct).ConfigureAwait(false);

        return new(work, revision, inputs, null, accepted);
    }

    private static bool DisposeWithoutAdjudication(LongRestSweepWork work)
    {
        if (work.Inputs.Any(leaf => leaf.Snapshot.IsConsolidated))
        {
            return true;
        }

        if (!work.Inputs.Any(leaf => leaf.Snapshot.IsTransformationOutput))
        {
            return false;
        }

        LongRestRequest request = new(LongRestTransformationKind.EquivalentObservations, work.Inputs.Select(leaf => leaf.Target).ToArray());

        Result<LongRestReceipt> preview = work.Identity.Scope.ProtectedPartitionDigest is { } partition
            ? ProtectedLongRestPolicy.Evaluate(request, work.Inputs, new(partition, work.Inputs[0].Provenance, [.. work.Inputs.SelectMany(leaf => leaf.LabelDigests)]))
            : LongRestPolicy.Evaluate(request, work.Inputs.Select(leaf => leaf.Snapshot).ToArray());

        return preview.IsSuccess && preview.Value.Reason == LongRestReason.DependencyConflict;
    }

    private static string LeafJson(LongRestSourceLeaf leaf) => JsonSerializer.Serialize(leaf with { Content = null, Vector = [] }, LongRestJsonContext.Default.LongRestSourceLeaf);
}
