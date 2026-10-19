using System.Collections.Immutable;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

internal sealed partial class MemoryEvaluationSourceReader(ICovenantConnectionSource connections, ICovenantOperationGate gate, TimeProvider timeProvider, ICovenantStore? covenantStore = null) : IMemoryEvaluationSourceReader
{
    private static readonly Error Unavailable = new(ErrorCodes.MemoryEvaluation.StaleSource, "The exact source evidence is unavailable or no longer eligible.");

    private static readonly Error Forbidden = new(ErrorCodes.Covenant.ForbiddenAuthority, "Evaluation requires its genuine live, scope-matched operator Turn lease.");

    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<Result<MemoryEvaluationSourceSnapshot>> ReadAsync(MemoryEvaluationValidatedRequest request, ArcanumInvocationContext invocation, CovenantTurnLease lease, CancellationToken ct)
    {
        Result authority = await gate.ValidateTurnLeaseAsync(lease, ct).ConfigureAwait(false);

        if (authority.IsFailure || !invocation.CanReadCovenant || invocation.ToolPolicy != ToolPolicy.NoTools
            || invocation.Surface != ArcanumExecutionSurface.StatelessOperatorTurn
            || invocation.ReadAuthorityEpoch?.Matches(lease.Snapshot) != true
            || invocation.Campaign is not { } campaign || campaign.CampaignId != request.Request.Scope.CampaignId
            || lease.Snapshot.Scope?.CampaignId != campaign.CampaignId)
        {
            return Forbidden;
        }

        try
        {
            DateTimeOffset asOfUtc = timeProvider.GetUtcNow();

            PreparedCovenant? covenant = await PrepareCovenantAsync(request, campaign, lease, ct).ConfigureAwait(false);

            SqliteConnection connection = await connections.GetOpenCoreConnectionAsync(ct).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            using CovenantCanonicalHashWriter metadata = BeginMetadata(lease, asOfUtc);

            List<MemoryEvaluationSourceCoordinate> coordinates = covenant?.Coordinates ?? [];

            List<MemoryEvaluationSourceFragment> fragments = covenant?.Fragments ?? [];

            if (covenant is not null)
            {
                CovenantDigest current = await CovenantMetadataAsync(connection, transaction, campaign.CampaignId, lease, ct).ConfigureAwait(false);

                Require(current == covenant.MetadataDigest);

                metadata.WriteFixed32(current.Bytes);
            }

            foreach (MemoryEvaluationSagaTarget target in request.Request.Memory.Saga.OrderBy(target => target.MemoryId, StringComparer.Ordinal).ThenBy(target => target.VersionId, StringComparer.Ordinal))
            {
                MemoryEvaluationSourceCoordinate coordinate = await ReadSagaAsync(connection, transaction, target, campaign.CampaignId, metadata, asOfUtc, ct, remainingBytes: RemainingMemoryBytes(fragments)).ConfigureAwait(false);

                string content = (string)(await ScalarAsync(connection, transaction, "SELECT Content FROM saga_memories WHERE Id = $id", ct, ("$id", target.MemoryId)).ConfigureAwait(false))!;

                Require(new CovenantDigest(Core.Annals.AnnalContentDigest.ForSagaMemory(content)) == target.ContentDigest);

                ArtifactSensitivityLabel? label = await ReadLabelAsync(connection, transaction, coordinate, null, ct).ConfigureAwait(false);

                Require(label is null || label.ArtifactContentDigest == DerivedArtifactContentDigest.ForText(content));

                fragments.Add(new(coordinates.Count, SystemPromptBuilder.RenderEvaluationSaga(content), MemoryEvaluationPromptPart.System));

                coordinates.Add(coordinate);
            }

            foreach (MemoryEvaluationLexiconTarget target in request.Request.Memory.Lexicon.OrderBy(target => target.EntryId).ThenBy(target => target.VersionId, StringComparer.Ordinal))
            {
                MemoryEvaluationSourceCoordinate coordinate = await ReadLexiconAsync(connection, transaction, target, campaign.CampaignId, metadata, asOfUtc, ct, remainingBytes: RemainingMemoryBytes(fragments)).ConfigureAwait(false);

                ArtifactSensitivityLabel? label = await ReadLabelAsync(connection, transaction, coordinate, null, ct).ConfigureAwait(false);

                fragments.Add(new(coordinates.Count, await HydrateLexiconAsync(connection, transaction, coordinate, label, ct).ConfigureAwait(false), MemoryEvaluationPromptPart.System));

                coordinates.Add(coordinate);
            }

            List<MemoryEvaluationDependencyEdge> edges = await DependenciesAsync(connection, transaction, coordinates, campaign.CampaignId, metadata, asOfUtc, ct).ConfigureAwait(false);

            Require(coordinates.Count > 0 && fragments.Sum(fragment => Utf8.GetByteCount(fragment.Text) + 2) <= MemoryEvaluationLimits.RenderedMemoryBytes);

            GenerationProvenance provenance = covenant is null ? GenerationProvenance.CreateExact([]) : GenerationProvenance.CreateExact([lease.Snapshot.DatasetGeneration!.Value]);

            foreach (MemoryEvaluationSourceCoordinate source in coordinates.Where(source => source.Store != MemoryEvaluationSource.Covenant))
            {
                ArtifactSensitivityLabel? label = await ReadLabelAsync(connection, transaction, source, null, ct).ConfigureAwait(false);

                if (label is not null)
                {
                    provenance = provenance.Merge(label.Provenance);
                }
            }

            Require((await gate.ValidateTurnLeaseAsync(lease, ct).ConfigureAwait(false)).IsSuccess);

            RequireManifestBounds(coordinates, edges);

            CovenantDigest manifest = MemoryEvaluationDigests.SourceManifest([.. coordinates], [.. edges]);

            metadata.WriteFixed32(manifest.Bytes);

            CovenantDigest digest = metadata.FinalizeDigest();

            return new MemoryEvaluationSourceSnapshot([.. coordinates], string.Concat(fragments.Select(fragment => fragment.Text)), manifest, Dependencies: [.. edges],
                Fence: new(lease.Snapshot.DatasetGeneration!.Value, lease.Snapshot.CapabilityGeneration, lease.Snapshot.RuntimeAuthorityGeneration,
                    lease.Snapshot.CampaignAvailabilityGeneration ?? 0, digest), Provenance: provenance, Fragments: [.. fragments], AsOfUtc: asOfUtc);
        }
        catch (Exception error) when (error is SourceException or ArgumentException or InvalidOperationException or FormatException or OverflowException or SqliteException or System.Text.Json.JsonException)
        {
            return Unavailable;
        }
    }

    private static int RemainingMemoryBytes(List<MemoryEvaluationSourceFragment> fragments) =>
        MemoryEvaluationLimits.RenderedMemoryBytes - fragments.Sum(fragment => Utf8.GetByteCount(fragment.Text) + 2);

    public async Task<Result> ValidateAsync(MemoryEvaluationSourceSnapshot snapshot, CovenantTurnLease lease, CancellationToken ct)
    {
        if ((await gate.ValidateTurnLeaseAsync(lease, ct).ConfigureAwait(false)).IsFailure || snapshot.Fence is null || snapshot.AsOfUtc is null)
        {
            return Forbidden;
        }

        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();

            RequireManifestBounds(snapshot.Coordinates, snapshot.Dependencies);

            SqliteConnection connection = await connections.GetOpenCoreConnectionAsync(ct).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            using CovenantCanonicalHashWriter metadata = BeginMetadata(lease, snapshot.AsOfUtc.Value);

            List<MemoryEvaluationSourceCoordinate> current = [];

            if (snapshot.Coordinates.Any(source => source.Store == MemoryEvaluationSource.Covenant))
            {
                metadata.WriteFixed32((await CovenantMetadataAsync(connection, transaction, lease.Snapshot.Scope?.CampaignId, lease, ct).ConfigureAwait(false)).Bytes);
            }

            foreach (MemoryEvaluationSourceCoordinate source in snapshot.Coordinates.Where(source => source.Role == MemoryEvaluationCoordinateRole.Intervention))
            {
                if (source.Store == MemoryEvaluationSource.Covenant)
                {
                    current.Add(source);

                    continue;
                }

                if (source.Store == MemoryEvaluationSource.Lexicon)
                {
                    current.Add(await ReadLexiconAsync(connection, transaction, new(Guid.Parse(source.SubjectId), source.VersionId, (AnnalContentHashFormat)source.HashFormat, source.ContentDigest),
                        lease.Snapshot.Scope?.CampaignId, metadata, now, ct).ConfigureAwait(false));

                    continue;
                }

                Require(source.Store == MemoryEvaluationSource.Saga);

                current.Add(await ReadSagaAsync(connection, transaction, new(source.SubjectId, source.VersionId, (AnnalContentHashFormat)source.HashFormat, source.ContentDigest),
                    lease.Snapshot.Scope?.CampaignId, metadata, now, ct).ConfigureAwait(false));
            }

            List<MemoryEvaluationDependencyEdge> edges = await DependenciesAsync(connection, transaction, current, lease.Snapshot.Scope?.CampaignId, metadata, now, ct).ConfigureAwait(false);

            CovenantDigest manifest = MemoryEvaluationDigests.SourceManifest([.. current], [.. edges]);

            metadata.WriteFixed32(manifest.Bytes);

            Require(manifest == snapshot.ManifestDigest && current.SequenceEqual(snapshot.Coordinates) && edges.SequenceEqual(snapshot.Dependencies)
                && metadata.FinalizeDigest() == snapshot.Fence.SourceStateDigest);

            return await gate.ValidateTurnLeaseAsync(lease, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SourceException or ArgumentException or InvalidOperationException or FormatException or OverflowException or SqliteException or System.Text.Json.JsonException)
        {
            return Unavailable;
        }
    }

    private static void RequireManifestBounds(IReadOnlyCollection<MemoryEvaluationSourceCoordinate> sources, IReadOnlyCollection<MemoryEvaluationDependencyEdge> edges)
    {
        Require(sources.Count is >= 1 and <= MemoryEvaluationLimits.ManifestCoordinates);

        // The fixed canonical fields, optional markers and UTF-8 length prefixes use at most
        // 178 bytes per source and 20 per edge. Round up, including domain/vector framing.
        long bytes = 256;

        foreach (MemoryEvaluationSourceCoordinate source in sources)
        {
            bytes += 192 + Utf8.GetByteCount(source.SubjectId) + Utf8.GetByteCount(source.ClaimId ?? string.Empty)
                + Utf8.GetByteCount(source.VersionId) + Utf8.GetByteCount(source.CanonicalOwnerSessionId ?? string.Empty)
                + Utf8.GetByteCount(source.SourceSessionId ?? string.Empty);
        }

        foreach (MemoryEvaluationDependencyEdge edge in edges)
        {
            bytes += 24 + Utf8.GetByteCount(edge.DependentVersionId) + Utf8.GetByteCount(edge.DependencyVersionId);
        }

        Require(bytes <= MemoryEvaluationLimits.EvidenceBytes);
    }

    private static CovenantCanonicalHashWriter BeginMetadata(CovenantTurnLease lease, DateTimeOffset asOfUtc)
    {
        CovenantCanonicalHashWriter writer = new();

        writer.WriteUtf8("arcanum.memory-evaluation.source-state.v1");

        writer.WriteInt64(asOfUtc.UtcTicks);

        writer.WriteGuid(lease.Snapshot.RegistrationId);

        writer.WriteGuid(lease.Snapshot.DatasetGeneration!.Value);

        writer.WriteInt64(lease.Snapshot.AuthorityEpoch);

        writer.WriteInt64(lease.Snapshot.RuntimeAuthorityGeneration);

        writer.WriteInt64(lease.Snapshot.CapabilityGeneration);

        writer.WriteInt64(lease.Snapshot.CampaignAvailabilityGeneration ?? 0);

        return writer;
    }

    private static void HashRow(CovenantCanonicalHashWriter writer, SqliteDataReader reader)
    {
        writer.WriteInt32(reader.FieldCount);

        for (int index = 0; index < reader.FieldCount; index++)
        {
            switch (reader.GetValue(index))
            {
                case DBNull:
                    writer.WriteByte(0);
                    break;
                case long number:
                    writer.WriteByte(1);
                    writer.WriteInt64(number);
                    break;
                case string text:
                    writer.WriteByte(2);
                    writer.WriteUtf8(text);
                    break;
                case byte[] bytes:
                    writer.WriteByte(3);
                    writer.WriteBytes(bytes);
                    break;
                default:
                    throw new SourceException();
            }
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = Command(connection, transaction, sql, parameters);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new SourceException();
        }
    }

    private sealed class SourceException : Exception;
}
