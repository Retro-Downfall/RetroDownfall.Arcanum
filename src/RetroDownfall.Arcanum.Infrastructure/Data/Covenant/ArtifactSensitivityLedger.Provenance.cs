using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

internal sealed partial class ArtifactSensitivityLedger
{
    private const int DistinctProvenanceProbeLimit = 16;

    private static async Task<GenerationProvenance?> TrySeekProvenanceAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid sessionId,
        GenerationProvenance incoming, CancellationToken cancellationToken)
    {
        GenerationProvenance merged = incoming;

        ProvenanceSeekRow? after = null;

        for (int probes = 0; ; probes++)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            const string columns = "ProvenanceModeCode, ExactGenerationIds, GenerationBloom";

            const string metadata = ", typeof(ProvenanceModeCode) AS ModeStorage, typeof(ExactGenerationIds) AS ExactStorage, length(ExactGenerationIds) AS ExactLength, typeof(GenerationBloom) AS BloomStorage, length(GenerationBloom) AS BloomLength";

            const string projected = columns + ", ModeStorage, ExactStorage, ExactLength, BloomStorage, BloomLength";

            const string order = " ORDER BY ProvenanceModeCode, ExactGenerationIds, GenerationBloom LIMIT 1";

            const string prefix = "SELECT " + columns + metadata + " FROM artifact_sensitivity WHERE SessionId=$session";

            if (after is null)
            {
                command.CommandText = prefix + order.Replace("LIMIT 1", "LIMIT 17", StringComparison.Ordinal);
            }
            else
            {
                string nextMode = after.Mode is DBNull ? "ProvenanceModeCode IS NOT NULL" : "ProvenanceModeCode > $mode";

                string nextExact = after.Exact is DBNull ? "ExactGenerationIds IS NOT NULL" : "ExactGenerationIds > $exact";

                string nextBloom = after.Bloom is DBNull ? "GenerationBloom IS NOT NULL" : "GenerationBloom > $bloom";

                command.CommandText = "SELECT " + projected + " FROM ("
                    + "SELECT * FROM (" + prefix + " AND " + nextMode + order + ") UNION ALL "
                    + "SELECT * FROM (" + prefix + " AND ProvenanceModeCode IS $mode AND " + nextExact + order + ") UNION ALL "
                    + "SELECT * FROM (" + prefix + " AND ProvenanceModeCode IS $mode AND ExactGenerationIds IS $exact AND " + nextBloom + order + "))" + order;

                command.Parameters.AddWithValue("$mode", after.Mode);

                command.Parameters.AddWithValue("$exact", after.Exact);

                command.Parameters.AddWithValue("$bloom", after.Bloom);
            }

            command.Parameters.AddWithValue("$session", Format(sessionId));

            List<ProvenanceSeekRow> rows = [];

            bool noncanonicalKey = false;

            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!CanRetainProvenanceKey(reader))
                    {
                        noncanonicalKey = true;

                        break;
                    }

                    rows.Add(new(reader.GetValue(0), reader.GetValue(1), reader.GetValue(2), DecodeProvenanceRow(reader)));
                }
            }

            if (rows.Count == 0 && !noncanonicalKey)
            {
                return merged;
            }

            bool initialWindow = after is null;

            if (initialWindow && !noncanonicalKey)
            {
                int distinct = 0;

                ProvenanceSeekRow? previous = null;

                foreach (ProvenanceSeekRow observed in rows)
                {
                    if (previous is null || !SameProvenanceTuple(previous, observed))
                    {
                        distinct++;
                    }

                    merged = merged.Merge(observed.Provenance);

                    previous = observed;
                }

                after = rows[^1];

                if (rows.Count < 17)
                {
                    return merged;
                }

                if (distinct <= DistinctProvenanceProbeLimit)
                {
                    probes = distinct - 1;

                    continue;
                }
            }

            if (noncanonicalKey || initialWindow || probes == DistinctProvenanceProbeLimit)
            {
                return null;
            }

            ProvenanceSeekRow row = rows[0];

            merged = merged.Merge(row.Provenance);

            after = row;
        }
    }

    private static bool CanRetainProvenanceKey(SqliteDataReader reader)
    {
        if (reader.GetString(3) != "integer")
        {
            return false;
        }

        long mode = reader.GetInt64(0);

        if (mode == (long)GenerationProvenanceMode.Exact)
        {
            if (reader.GetString(4) != "blob" || reader.GetString(6) != "null")
            {
                return false;
            }

            long bytes = reader.GetInt64(5);

            return bytes is >= 16 and <= 128 && bytes % 16 == 0;
        }

        return mode == (long)GenerationProvenanceMode.BloomOverflow && reader.GetString(4) == "null"
            && reader.GetString(6) == "blob" && reader.GetInt64(7) == 32;
    }

    private static bool SameProvenanceTuple(ProvenanceSeekRow left, ProvenanceSeekRow right) =>
        SameProvenanceValue(left.Mode, right.Mode) && SameProvenanceValue(left.Exact, right.Exact)
        && SameProvenanceValue(left.Bloom, right.Bloom);

    private static bool SameProvenanceValue(object left, object right)
    {
        if (left is byte[] first && right is byte[] second)
        {
            return first.AsSpan().SequenceEqual(second);
        }

        if (left is long firstInteger && right is long secondInteger)
        {
            return firstInteger == secondInteger;
        }

        return left is DBNull && right is DBNull;
    }

    private static GenerationProvenance DecodeProvenanceRow(SqliteDataReader reader) =>
        (GenerationProvenanceMode)reader.GetInt64(0) is GenerationProvenanceMode.Exact
            ? GenerationProvenance.CreateExact(ReadGenerations((byte[])reader.GetValue(1)))
            : GenerationProvenance.CreateBloom((byte[])reader.GetValue(2));

    private sealed record ProvenanceSeekRow(object Mode, object Exact, object Bloom, GenerationProvenance Provenance);
}
