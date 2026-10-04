using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>One live sensitivity label an erase measured: its identity, its artifact, and its owner.</summary>
/// <param name="OwnerCampaignId">The Campaign the label records as owner, or null for the Global scope.</param>
internal sealed record MemoryErasureLabelRow(Guid LabelId, string ArtifactId, Guid? OwnerCampaignId);

/// <summary>
/// The sensitivity labels an erase measures and removes, read and deleted exactly.
/// </summary>
/// <remarks>
/// <para>The label table has one writer, the sensitivity ledger, which spells every identity upper-case
/// and dashed, so each read compares that one spelling exactly and seeks the one-label-per-artifact
/// index. An artifact identity that is not a GUID cannot carry a label and is refused.</para>
///
/// <para>A label is the only record that its artifact was Covenant-derived, so a delete is allowed only
/// under the sensitivity retention purge authorization, borrowed for the one statement and released
/// before it returns. The delete names both the label and its artifact, so it removes exactly the label
/// the erase measured and never a successor.</para>
///
/// <para>Nothing here logs, and nothing here names content.</para>
/// </remarks>
internal static class MemoryErasureLabels
{
    /// <summary>The live labels of these artifacts of one kind, in the order the identities were given.</summary>
    /// <exception cref="ArgumentException">An artifact identity is not a GUID.</exception>
    internal static async Task<IReadOnlyList<MemoryErasureLabelRow>> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SensitiveArtifactKind kind,
        IReadOnlyList<string> artifactIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(artifactIds);

        List<MemoryErasureLabelRow> labels = [];

        if (artifactIds.Count == 0)
        {
            return labels;
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT LabelId, ArtifactId, CampaignId
            FROM artifact_sensitivity
            WHERE ArtifactKindCode = $kind AND ArtifactId = $artifactId;
            """;

        _ = command.Parameters.AddWithValue("$kind", (long)kind);

        SqliteParameter artifact = command.Parameters.Add("$artifactId", SqliteType.Text);

        foreach (string artifactId in artifactIds)
        {
            artifact.Value = Canonical(artifactId);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                labels.Add(new MemoryErasureLabelRow(
                    Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
            }
        }

        return labels;
    }

    /// <summary>Removes one exact label of one exact artifact under the retention-purge authorization.</summary>
    /// <returns>The labels removed: one, or zero when that label no longer names that artifact.</returns>
    internal static async Task<int> DeleteExactAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ICovenantSqliteConnectionInitializer initializer,
        Guid labelId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(initializer);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DELETE FROM artifact_sensitivity WHERE LabelId = $labelId AND ArtifactId = $artifactId;";

        _ = command.Parameters.AddWithValue("$labelId", Canonical(labelId));

        _ = command.Parameters.AddWithValue("$artifactId", Canonical(artifactId));

        using CovenantSqliteAuthorizationScope purge = initializer.Authorize(
            connection,
            CovenantSqliteAuthorizationKind.SensitivityRetentionPurge);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The ledger's own spelling of an identity: upper-case and dashed.</summary>
    private static string Canonical(string identity) => MemoryErasureDigestGrammar.CanonicalRowId(identity);

    private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();
}
