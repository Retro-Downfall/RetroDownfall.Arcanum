using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    public async Task<Result<LongRestCandidateGroup[]>> DiscoverAsync(Guid generationId, LongRestTarget anchor,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        if (generationId == Guid.Empty || LongRestPolicy.ValidateRequest(new(LongRestTransformationKind.ExactDuplicates, [anchor])).IsFailure)
        {
            return Refuse<LongRestCandidateGroup[]>(ErrorCodes.LongRest.InvalidRequest, "Discovery requires an exact generation and anchor.");
        }

        try
        {
            SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            return await DiscoverWithinAsync(connection, transaction, generationId, anchor, capability, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestCandidateGroup[]>(ErrorCodes.LongRest.Unavailable, "Discovery could not verify its retained source bindings.");
        }
    }

    internal static async Task<Result<LongRestCandidateGroup[]>> DiscoverWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid generationId, LongRestTarget anchor, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        LongRestDiscoveryGeneration? generation = await ReadGenerationAsync(connection, transaction, "GenerationId = $id", Format(generationId), cancellationToken).ConfigureAwait(false);

        if (generation is null || !generation.Published || generation.Phase is not (LongRestSweepPhase.Discover or LongRestSweepPhase.Adjudicate or LongRestSweepPhase.Completed)
            || !await FrozenSourceMetadataMatchesAsync(connection, transaction, generation, cancellationToken).ConfigureAwait(false))
        {
            return Refuse<LongRestCandidateGroup[]>(ErrorCodes.LongRest.StaleInput, "Discovery requires a current complete generation.");
        }

        Result authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

        if (authority.IsFailure)
        {
            return Result<LongRestCandidateGroup[]>.Failure(authority.Error);
        }

        LongRestSourceLeaf source = await ReadFrozenLeafAsync(connection, transaction, generation, anchor.MemoryId, capability, cancellationToken).ConfigureAwait(false);

        if (source.Target != anchor || !CandidateEligible(source))
        {
            return Refuse<LongRestCandidateGroup[]>(ErrorCodes.LongRest.StaleInput, "The anchor is no longer the exact eligible captured version.");
        }

        ImmutableArray<LongRestCandidate>[] lanes = [[], [], [], []];

        float[] direction = SphericalKMeans.NormalizedDirection(source.Vector.ToArray());

        string? cursor = null;

        do
        {
            List<string> page = [];

            await using (SqliteCommand command = Command(connection, transaction, """
                SELECT MemoryId FROM long_rest_discovery_leaves
                WHERE GenerationId = $generation AND ($cursor IS NULL OR MemoryId > $cursor)
                ORDER BY MemoryId LIMIT 128;
                """, ("$generation", Format(generationId)), ("$cursor", cursor)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    page.Add(reader.GetString(0));
                }
            }

            foreach (string memory in page)
            {
                LongRestSourceLeaf candidate = await ReadFrozenLeafAsync(connection, transaction, generation, memory, capability, cancellationToken).ConfigureAwait(false);

                if (memory == anchor.MemoryId || !CandidateEligible(candidate))
                {
                    continue;
                }

                if (StringComparer.OrdinalIgnoreCase.Equals(candidate.Snapshot.ContentHash, source.Snapshot.ContentHash))
                {
                    Rank(new(candidate.Target, LongRestDiscoveryLane.ExactHash, 0));
                }

                if (direction.Length > 0 && candidate.Vector.Length == direction.Length)
                {
                    float[] other = SphericalKMeans.NormalizedDirection(candidate.Vector.ToArray());

                    if (other.Length == direction.Length)
                    {
                        double score = Math.Clamp(SphericalKMeans.DirectionCosine(direction, other), -1, 1);

                        Rank(new(candidate.Target, LongRestDiscoveryLane.Vector, score));
                    }
                }
            }

            cursor = page.Count == 0 ? null : page[^1];
        } while (cursor is not null);

        string query = LexicalQuery(source.Content!);

        if (query.Length > 0)
        {
            string index = generation.Scope.ProtectedPartitionDigest is null ? "long_rest_clean_fts" : "long_rest_protected_fts";

            // MATCH consumes quoted bounded tokens, not raw source syntax. Iterate every exact
            // eligible match before ranking: a tempting excluded hit never spends a top-K slot.
            await using SqliteCommand command = Command(connection, transaction, $"""
                SELECT node.MemoryId, bm25({index})
                FROM {index} JOIN long_rest_discovery_nodes node ON node.NodeId = {index}.NodeId AND node.GenerationId = {index}.GenerationId
                WHERE {index} MATCH $query AND node.GenerationId = $generation AND node.Layer = 0
                ORDER BY node.MemoryId;
                """, ("$query", query), ("$generation", Format(generationId)));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                LongRestSourceLeaf candidate = await ReadFrozenLeafAsync(connection, transaction, generation, reader.GetString(0), capability, cancellationToken).ConfigureAwait(false);

                if (candidate.Target.MemoryId != anchor.MemoryId && CandidateEligible(candidate))
                {
                    Rank(new(candidate.Target, LongRestDiscoveryLane.Lexical, reader.GetDouble(1)));
                }
            }
        }

        ImmutableArray<LongRestCandidate> hierarchy = await HierarchyCandidatesAsync(connection, transaction, generation, anchor, capability, cancellationToken).ConfigureAwait(false);

        ImmutableArray<LongRestCandidate> ranked = LongRestDiscoveryPolicy.MergeCandidates(anchor, lanes.SelectMany(lane => lane).Concat(hierarchy)).Value;

        authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

        if (authority.IsFailure)
        {
            return Result<LongRestCandidateGroup[]>.Failure(authority.Error);
        }

        return LongRestDiscoveryPolicy.GroupCandidates(anchor, ranked).Value.ToArray();

        void Rank(LongRestCandidate candidate)
        {
            int lane = (int)candidate.Lane - 1;

            lanes[lane] = LongRestDiscoveryPolicy.MergeCandidates(anchor, lanes[lane].Append(candidate)).Value;
        }
    }

    private static bool CandidateEligible(LongRestSourceLeaf leaf) => leaf.Snapshot.IsCurrent
        && !leaf.Snapshot.IsConsolidated && leaf.Snapshot.PinnedAtUtc is null && leaf.Snapshot.RetiredAtUtc is null;

    private static string LexicalQuery(string content)
    {
        List<string> tokens = [];

        StringBuilder token = new();

        foreach (Rune rune in content.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                token.Append(rune);
            }
            else
            {
                AddToken();

                if (tokens.Count == LongRestDiscoveryLimits.LexicalTokens)
                {
                    break;
                }
            }
        }

        if (tokens.Count < LongRestDiscoveryLimits.LexicalTokens)
        {
            AddToken();
        }

        return string.Join(" OR ", tokens);

        void AddToken()
        {
            if (token.Length > 0 && Encoding.UTF8.GetByteCount(token.ToString()) <= LongRestDiscoveryLimits.LexicalTokenUtf8Bytes)
            {
                tokens.Add("\"" + token + "\"");
            }

            token.Clear();
        }
    }

    private static async Task<LongRestSourceLeaf> ReadFrozenLeafAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string memory, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken) =>
        (await ReadVerifiedFrozenLeafAsync(connection, transaction, generation, memory, capability, cancellationToken).ConfigureAwait(false)).Leaf;

    private sealed record VerifiedFrozenLeaf(LongRestSourceLeaf Leaf, CovenantDigest OriginalSourceDigest);

    private static async Task<VerifiedFrozenLeaf> ReadVerifiedFrozenLeafAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string memory, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        SourceCoordinate coordinate;

        CovenantDigest expected;

        LongRestSnapshot frozen;

        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT leaf.InsertionSequence, leaf.MutationEpoch, leaf.VersionId, leaf.SourceDigest, leaf.SnapshotJson
            FROM long_rest_discovery_leaves leaf JOIN long_rest_sources current ON current.MemoryId = leaf.MemoryId
            WHERE leaf.GenerationId = $generation AND leaf.MemoryId = $memory
                AND current.InsertionSequence = leaf.InsertionSequence AND current.MutationEpoch = leaf.MutationEpoch;
            """, ("$generation", Format(generation.GenerationId)), ("$memory", memory)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("A captured source coordinate is no longer current.");
            }

            coordinate = new(memory, reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));

            expected = new((byte[])reader[3]);

            frozen = JsonSerializer.Deserialize(reader.GetString(4), LongRestJsonContext.Default.LongRestSnapshot) ?? throw new InvalidDataException();
        }

        Result<LongRestSourceLeaf> loaded = await ReadSourceAsync(connection, transaction, generation, coordinate, capability, true, cancellationToken).ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            throw new InvalidDataException(loaded.Error.Code);
        }

        Result<CovenantDigest> actual = LongRestDiscoveryHash.SourcePage(generation.Scope, [loaded.Value]);

        if (actual.IsFailure || actual.Value != expected)
        {
            LongRestSourceLeaf comparable = loaded.Value with
            {
                Snapshot = loaded.Value.Snapshot with { IsConsolidated = frozen.IsConsolidated, IsTransformationOutput = frozen.IsTransformationOutput },
            };

            if (LongRestDiscoveryHash.SourcePage(generation.Scope, [comparable]).Value != expected)
            {
                throw new InvalidDataException("The captured source bytes or complete decision state changed.");
            }

            await VerifyOwnProjectionAsync(connection, transaction, generation, loaded.Value, frozen, cancellationToken).ConfigureAwait(false);
        }

        return new(loaded.Value, expected);
    }
}
