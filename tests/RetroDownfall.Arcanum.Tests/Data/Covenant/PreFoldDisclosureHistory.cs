using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Writes disclosure receipts the way a build without the live disclosure fold did.
/// </summary>
/// <remarks>
/// That build's journal allocated the ordinal, inserted the receipt, and advanced the subject's
/// counters and chain in one transaction, and never touched <c>external_disclosure_state</c> or
/// <c>LastFoldedOrdinal</c>. Every row written here has exactly that shape: a receipt, a subject row
/// whose watermark is still zero, and no joined bucket. It is the history an upgraded installation
/// carries, and the one a reader must never mistake for "nothing left this installation".
/// </remarks>
internal static class PreFoldDisclosureHistory
{

    private static readonly Guid BootId = Guid.Parse("0f0f0f0f-1e1e-4d2d-8c3c-4b4b4b4b4b4b");

    private static readonly GenerationProvenance Provenance =
        GenerationProvenance.CreateExact([CovenantTask6Fixture.DatasetGeneration]);

    /// <summary>
    /// The Covenant-derived sensitivity every receipt here carries, built the way the journal tests
    /// build theirs, so a rebuilt row hashes to the draft's own sensitivity digest.
    /// </summary>
    internal static readonly ProviderCallSensitivity Sensitivity = new(
        ContentSensitivity.CovenantDerived,
        Provenance,
        CovenantDigests.Sensitivity(new SensitivityDigestInput(
            ContentSensitivity.CovenantDerived,
            Provenance.Mode,
            Provenance.ExactGenerationIds,
            Provenance.BloomBits)));

    /// <summary>
    /// Inserts one receipt for <paramref name="draft"/> at <paramref name="ordinal"/>, and seeds or
    /// advances its subject row without folding anything.
    /// </summary>
    /// <param name="exactGenerationIds">
    /// Overrides the stored generation identities. A value the sensitivity grammar refuses, such as a
    /// zero identity, makes the row one no reader can rebuild into its original receipt.
    /// </param>
    /// <param name="disclosedAtUtc">
    /// Overrides the stored instant text. The column has no shape check, so a row can carry text no
    /// supported instant format parses.
    /// </param>
    /// <param name="allocate">
    /// False leaves the subject row exactly as it was, so a receipt past its last allocated ordinal
    /// is a stray the journal never allocated. The schema admits one: nothing ties a receipt's ordinal
    /// to its subject's counter.
    /// </param>
    internal static async Task InsertAsync(
        SqliteConnection connection,
        CovenantDisclosureDraft draft,
        ulong ordinal,
        CancellationToken cancellationToken,
        CovenantDisclosureEffectCategory category = CovenantDisclosureEffectCategory.ProviderDispatch,
        byte[]? exactGenerationIds = null,
        string? disclosedAtUtc = null,
        bool allocate = true)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(draft);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        await using (SqliteCommand seed = Command(connection, transaction, draft))
        {

            seed.CommandText = """
                INSERT OR IGNORE INTO disclosure_subject_state (
                    OriginInstallationId, SubjectKind, SubjectId, LifecycleCode, CreatorBootId,
                    LastHeartbeatAtUtc, ClosedAtUtc, ProviderAttemptCount, ExternalEffectCount,
                    LastAllocatedOrdinal, LastFoldedOrdinal, DisclosureChainDigest)
                VALUES ($installation, $kind, $subject, 1, $boot, $now, NULL, 0, 0, 0, 0, $seedChain);
                """;

            _ = seed.Parameters.AddWithValue("$boot", BootId.ToString("D"));

            _ = seed.Parameters.AddWithValue("$now", Iso(draft.Timestamp));

            _ = seed.Parameters.AddWithValue(
                "$seedChain",
                CovenantEvidenceChains.SeedDisclosureChain().Head.Bytes);

            _ = await seed.ExecuteNonQueryAsync(cancellationToken);

        }

        CovenantDisclosureChain chain;

        await using (SqliteCommand read = Command(connection, transaction, draft))
        {

            read.CommandText = """
                SELECT ExternalEffectCount, DisclosureChainDigest
                FROM disclosure_subject_state
                WHERE OriginInstallationId = $installation AND SubjectKind = $kind AND SubjectId = $subject;
                """;

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken));

            chain = CovenantEvidenceChains.AppendDisclosure(
                new CovenantDisclosureChain(
                    (ulong)reader.GetInt64(0),
                    new CovenantDigest((byte[])reader.GetValue(1))),
                new CovenantDisclosureReceipt(draft, ordinal).Digest);

        }

        await using (SqliteCommand insert = Command(connection, transaction, draft))
        {

            insert.CommandText = """
                INSERT INTO external_disclosure_receipts (
                    OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal, EffectCategoryCode,
                    CategoryPhysicalAttemptOrdinal, EffectIdentityDigest, DestinationCode,
                    RevocabilityCode, DestinationDigest, SensitivityCode, GenerationProvenanceModeCode,
                    ExactGenerationIds, GenerationBloom, WardEvidenceDigest, AdmissionEvidenceDigest,
                    BackupEvidenceDigest, DisclosedAtUtc)
                VALUES (
                    $installation, $kind, $subject, $ordinal, $category,
                    (SELECT COUNT(*) + 1
                     FROM external_disclosure_receipts
                     WHERE OriginInstallationId = $installation
                         AND SubjectKind = $kind
                         AND SubjectId = $subject
                         AND EffectCategoryCode = $category),
                    $effect, $destination,
                    $revocability, $destinationDigest, $sensitivity, 1,
                    $exactIds, NULL, $ward, $admission,
                    $backup, $disclosedAt);
                """;

            _ = insert.Parameters.AddWithValue("$ordinal", (long)ordinal);

            _ = insert.Parameters.AddWithValue("$category", (long)category);

            _ = insert.Parameters.AddWithValue("$effect", draft.EffectIdentityDigest.Bytes);

            _ = insert.Parameters.AddWithValue("$destination", (long)draft.Destination);

            _ = insert.Parameters.AddWithValue("$revocability", (long)draft.Revocability);

            _ = insert.Parameters.AddWithValue(
                "$destinationDigest",
                draft.OpaqueDestinationIdentityDigest.Bytes);

            _ = insert.Parameters.AddWithValue("$sensitivity", (long)Sensitivity.Level);

            _ = insert.Parameters.AddWithValue(
                "$exactIds",
                exactGenerationIds ?? PackGenerationIds(Sensitivity.Provenance));

            _ = insert.Parameters.AddWithValue(
                "$ward",
                draft.WardEvidenceDigest is { } ward ? ward.Bytes : DBNull.Value);

            _ = insert.Parameters.AddWithValue(
                "$admission",
                draft.AdmissionDigest is { } admission ? admission.Bytes : DBNull.Value);

            _ = insert.Parameters.AddWithValue(
                "$backup",
                draft.BackupEvidenceDigest is { } backup ? backup.Bytes : DBNull.Value);

            _ = insert.Parameters.AddWithValue("$disclosedAt", disclosedAtUtc ?? Iso(draft.Timestamp));

            Assert.Equal(1, await insert.ExecuteNonQueryAsync(cancellationToken));

        }

        if (allocate)
        {

            await AdvanceAsync(connection, transaction, draft, ordinal, category, chain, cancellationToken);

        }

        await transaction.CommitAsync(cancellationToken);

    }

    private static async Task AdvanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantDisclosureDraft draft,
        ulong ordinal,
        CovenantDisclosureEffectCategory category,
        CovenantDisclosureChain chain,
        CancellationToken cancellationToken)
    {

        await using (SqliteCommand advance = Command(connection, transaction, draft))
        {

            advance.CommandText = """
                UPDATE disclosure_subject_state
                SET LastAllocatedOrdinal = max(LastAllocatedOrdinal, $ordinal),
                    ExternalEffectCount = $effects,
                    ProviderAttemptCount = ProviderAttemptCount + $providerAttempt,
                    DisclosureChainDigest = $chain,
                    LastHeartbeatAtUtc = $now
                WHERE OriginInstallationId = $installation AND SubjectKind = $kind AND SubjectId = $subject;
                """;

            _ = advance.Parameters.AddWithValue("$ordinal", (long)ordinal);

            _ = advance.Parameters.AddWithValue("$effects", (long)chain.Count);

            _ = advance.Parameters.AddWithValue(
                "$providerAttempt",
                category is CovenantDisclosureEffectCategory.ProviderDispatch ? 1L : 0L);

            _ = advance.Parameters.AddWithValue("$chain", chain.Head.Bytes);

            _ = advance.Parameters.AddWithValue("$now", Iso(draft.Timestamp));

            Assert.Equal(1, await advance.ExecuteNonQueryAsync(cancellationToken));

        }

    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantDisclosureDraft draft)
    {

        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        _ = command.Parameters.AddWithValue("$installation", draft.OriginInstallationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$kind", (long)draft.SubjectKind);

        _ = command.Parameters.AddWithValue("$subject", draft.SubjectId.ToString("D"));

        return command;

    }

    private static byte[] PackGenerationIds(GenerationProvenance provenance)
    {

        byte[] packed = new byte[provenance.ExactGenerationIds.Length * 16];

        for (int index = 0; index < provenance.ExactGenerationIds.Length; index++)
        {

            _ = provenance.ExactGenerationIds[index].TryWriteBytes(
                packed.AsSpan(index * 16, 16),
                bigEndian: true,
                out _);

        }

        return packed;

    }

    private static string Iso(long timestamp) =>
        UtcInstantText.Format(DateTimeOffset.FromUnixTimeMilliseconds(timestamp));

}
