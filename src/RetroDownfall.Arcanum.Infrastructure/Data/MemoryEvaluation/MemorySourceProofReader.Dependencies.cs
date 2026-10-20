using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

internal sealed partial class MemorySourceProofReader
{
    private async Task<List<MemoryEvaluationDependencyEdge>> DependenciesAsync(SqliteConnection connection, SqliteTransaction transaction,
        List<MemoryEvaluationSourceCoordinate> sources, Guid? campaign, CovenantCanonicalHashWriter metadata, DateTimeOffset now, CancellationToken ct)
    {
        HashSet<(MemoryEvaluationSource Store, string Version)> visited = sources.Select(source => (source.Store, source.VersionId)).ToHashSet();

        Require(visited.Count == sources.Count);

        List<MemoryEvaluationDependencyEdge> edges = [];

        for (int index = 0; index < sources.Count; index++)
        {
            MemoryEvaluationSourceCoordinate source = sources[index];

            if (source.Store == MemoryEvaluationSource.Covenant)
            {
                continue;
            }

            string predecessor = (string)(await ScalarAsync(connection, transaction,
                "SELECT COALESCE(PredecessorVersionId, '') FROM annal_versions WHERE VersionId = $version AND ClaimId = $claim", ct,
                ("$version", source.VersionId), ("$claim", source.ClaimId)).ConfigureAwait(false))!;

            metadata.WriteUtf8(predecessor);

            List<(MemoryEvaluationDependencyEdge Edge, MemoryEvaluationSagaTarget Target, int Store)> pending = [];

            await using (SqliteCommand command = Command(connection, transaction, """
                SELECT d.DependencyVersionId, d.RelationCode, d.Ordinal, c.SubjectStoreCode, c.SubjectId,
                    v.ContentHashFormatCode, v.ContentHash, d.DependentSequence, d.DependencySequence, v.Sequence,
                    (SELECT Sequence FROM annal_versions WHERE VersionId = d.DependentVersionId), c.ClaimId
                FROM annal_dependencies d
                LEFT JOIN annal_versions v ON v.VersionId = d.DependencyVersionId
                LEFT JOIN annal_claims c ON c.ClaimId = v.ClaimId
                WHERE d.DependentVersionId = $version ORDER BY d.Ordinal LIMIT 17
                """, ("$version", source.VersionId)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    Require(pending.Count < 16 && !reader.IsDBNull(3) && reader.GetInt32(3) is 1 or 2 && !reader.IsDBNull(6)
                        && reader.GetInt64(7) == reader.GetInt64(10) && reader.GetInt64(8) == reader.GetInt64(9)
                        && reader.GetInt64(8) < reader.GetInt64(7) && reader.GetInt32(1) is >= 1 and <= 3 && reader.GetInt32(2) is >= 1 and <= 16
                        && (reader.GetInt32(1) != (int)AnnalDependencyRelation.Supersedes || reader.GetString(0) == predecessor && reader.GetString(11) == source.ClaimId));

                    HashRow(metadata, reader);

                    pending.Add((new(source.Store, source.VersionId, reader.GetString(0), (AnnalDependencyRelation)reader.GetInt32(1), reader.GetInt32(2)),
                        new(reader.GetString(4), reader.GetString(0), (AnnalContentHashFormat)reader.GetInt32(5), new((byte[])reader[6])), reader.GetInt32(3)));
                }
            }

            Require(predecessor.Length == 0 || pending.Count(item => item.Edge.Relation == AnnalDependencyRelation.Supersedes && item.Edge.DependencyVersionId == predecessor) == 1);

            metadata.WriteInt32(pending.Count);

            foreach (var pendingSource in pending)
            {
                edges.Add(pendingSource.Edge);

                if (visited.Add((pendingSource.Store == 1 ? MemoryEvaluationSource.Saga : MemoryEvaluationSource.Lexicon, pendingSource.Target.VersionId)))
                {
                    Require(sources.Count < _limits.ManifestCoordinates);

                    sources.Add(pendingSource.Store == 1
                        ? await ReadSagaAsync(connection, transaction, pendingSource.Target, campaign, metadata, now, ct, intervention: false).ConfigureAwait(false)
                        : await ReadLexiconAsync(connection, transaction, new(Guid.Parse(pendingSource.Target.MemoryId), pendingSource.Target.VersionId,
                            pendingSource.Target.HashFormat, pendingSource.Target.ContentDigest), campaign, metadata, now, ct, intervention: false).ConfigureAwait(false));
                }
            }
        }

        return edges;
    }
}
