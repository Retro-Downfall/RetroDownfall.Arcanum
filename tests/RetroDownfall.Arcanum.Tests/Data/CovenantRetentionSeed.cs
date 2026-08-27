using System.Globalization;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Seeds one complete, referentially valid Covenant family into a Grimoire the retention suites can
/// then sweep over.
/// </summary>
/// <remarks>
/// The graph is small but deliberately spans every arm issue #116 promises pruning cannot reach: an
/// entry with an immutable version and a lane head, attachment provenance, a turn receipt, a mutation
/// receipt, a labelled Session artifact with its conservative projection, an erasure receipt
/// (a tombstone), a managed-file write intent with its erasure work item, and both the receipt table
/// and the folded aggregate on the disclosure side.
///
/// <para>The privileged inserts run under the same authorization scopes production uses. The guard
/// triggers begin denied on every connection, so a bare insert here would be simulating a write the
/// product cannot perform — the test would then prove nothing about the real table.</para>
/// </remarks>
internal static class CovenantRetentionSeed
{

    /// <summary>
    /// The Session the whole family hangs from, in the spelling every writer of <c>Sessions."Id"</c>
    /// renders: uppercase, dashed, 36 characters.
    /// </summary>
    /// <remarks>
    /// It was lowercase, which is a spelling no writer of that column has ever produced - the
    /// object-relational writer, the protected artifact transfer store and the backup session importer
    /// all render the canonical form, and the SQLite value binder uppercases a Guid unconditionally. The
    /// version-5 identity guards refuse it outright, which is how the misrepresentation surfaced. The
    /// remaining identities below are not of that family - a label, an artifact, a write operation and a
    /// work item - and are left as they are.
    /// </remarks>
    internal const string SessionId = "9F3A1C44-0D21-4A6E-9C31-6F2B0D55E701";

    internal const string SummaryArtifactId = "9f3a1c44-0d21-4a6e-9c31-6f2b0d55e702";

    internal const string SummaryLabelId = "9f3a1c44-0d21-4a6e-9c31-6f2b0d55e703";

    internal const string ManagedArtifactId = "9f3a1c44-0d21-4a6e-9c31-6f2b0d55e704";

    internal const string ManagedLabelId = "9f3a1c44-0d21-4a6e-9c31-6f2b0d55e705";

    internal const string WriteOperationId = "9f3a1c44-0d21-4a6e-9c31-6f2b0d55e706";

    internal const string WorkItemId = "9f3a1c44-0d21-4a6e-9c31-6f2b0d55e707";

    /// <summary>The exact number of nonrevocable possible attempts this seed folds.</summary>
    internal const long PossibleDisclosures = 3;

    private const string Iso = "2026-01-01T00:00:00.0000000Z";

    /// <summary>
    /// Seeds the family.
    /// </summary>
    /// <param name="sessionAgedOut">
    /// Whether the owning Session is old enough for an ordinary <c>ActiveSessions</c> rule to select
    /// it. Default false, so a sweep-invariance test measures whether any rule targets the Covenant
    /// family rather than whether Session retention cascades — which it does, by design.
    /// </param>
    internal static async Task SeedAsync(
        ArcanumDbContext db,
        CancellationToken cancellationToken,
        bool sessionAgedOut = false)
    {

        ArgumentNullException.ThrowIfNull(db);

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken);

        }

        await SeedEntryGraphAsync(connection, cancellationToken);

        await SeedProtectedSummaryAsync(
            connection,
            sessionAgedOut
                ? Iso
                : DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            cancellationToken);

        await SeedManagedFileAsync(connection, cancellationToken);

        await SeedDisclosureAsync(connection, cancellationToken);

    }

    private static async Task SeedEntryGraphAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await ExecuteAsync(
            connection,
            """
            INSERT INTO covenant_entries (
                EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
            VALUES ('entry-1', 1, NULL, 'project/goal', 'project/goal', $now);
            """,
            cancellationToken,
            ("$now", Iso));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO covenant_versions (
                VersionId, EntryId, LaneCode, LaneRevision, OperationCode, CompiledByteCost,
                RequiredFenceLength, CompilerPolicyVersion, RendererPolicyVersion, OriginCode,
                MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest,
                AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
            VALUES ('version-1', 'entry-1', 1, 1, 2, 0, 0, 1, 1, 1, 'mutation-1', zeroblob(32),
                    zeroblob(32), zeroblob(32), 1, zeroblob(32), $now);
            """,
            cancellationToken,
            ("$now", Iso));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO covenant_version_attachment_provenance (
                VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
                SourceRangeKindCode)
            VALUES ('version-1', 0, 'attachment-1', 'attachment-1:1', 'notes.md', zeroblob(32), 1);
            """,
            cancellationToken);

        // A head's search row id has to be allocated out of covenant_state first: the validate-insert
        // trigger refuses any id at or past NextSearchRowId, which is how the canonical tier keeps two
        // heads from ever claiming the same accelerator row.
        long searchRowId = await ScalarAsync(
            connection,
            "SELECT NextSearchRowId FROM covenant_state WHERE StateKey = 1;",
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            UPDATE covenant_state
            SET NextSearchRowId = NextSearchRowId + 1,
                UpdatedAtUtc = $now
            WHERE StateKey = 1;
            """,
            cancellationToken,
            ("$now", Iso));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO covenant_heads (
                EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode,
                ScopeCode, CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId,
                UpdatedAtUtc)
            VALUES ('entry-1', 1, 'version-1', 1, 2, 1, NULL, 'project/goal', 0, 1, $searchRow, $now);
            """,
            cancellationToken,
            ("$searchRow", searchRowId),
            ("$now", Iso));

    }

    private static async Task SeedProtectedSummaryAsync(
        SqliteConnection connection,
        string sessionTimestamp,
        CancellationToken cancellationToken)
    {

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "Status", "Summary", "CreatedAt", "UpdatedAt")
            VALUES ($session, 'active', 'a protected summary', $sessionNow, $sessionNow);
            """,
            cancellationToken,
            ("$session", SessionId),
            ("$sessionNow", sessionTimestamp));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO session_summary_artifacts (
                ArtifactId, SessionId, Revision, ContentDigest, SensitivityCode,
                SensitivityDigest, SummarizedThroughUtc, CreatedAtUtc)
            VALUES ($artifact, $session, 1, zeroblob(32), 1, zeroblob(32), NULL, $now);
            """,
            cancellationToken,
            ("$artifact", SummaryArtifactId),
            ("$session", SessionId),
            ("$now", Iso));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO session_summary_state (SessionId, CurrentArtifactId, Revision, UpdatedAtUtc)
            VALUES ($session, $artifact, 1, $now);
            """,
            cancellationToken,
            ("$session", SessionId),
            ("$artifact", SummaryArtifactId),
            ("$now", Iso));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO session_sensitivity_state (
                SessionId, TaintedArtifactCount, MaximumSensitivityCode,
                GenerationProvenanceDigest, Revision, UpdatedAtUtc)
            VALUES ($session, 2, 1, zeroblob(32), 1, $now);
            """,
            cancellationToken,
            ("$session", SessionId),
            ("$now", Iso));

        await SeedLabelAsync(
            connection,
            SummaryLabelId,
            SummaryArtifactId,
            SensitiveArtifactKind.Summary,
            SessionId,
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO assistant_entry_erasure_receipts (
                AssistantEntryId, SessionId, FinalizationGuardDigest, ErasureReasonCode,
                OperationId, ErasedAtUtc)
            VALUES ($entry, $session, zeroblob(32), 1, $operation, $now);
            """,
            cancellationToken,
            ("$entry", "9f3a1c44-0d21-4a6e-9c31-6f2b0d55e708"),
            ("$session", SessionId),
            ("$operation", "9f3a1c44-0d21-4a6e-9c31-6f2b0d55e70a"),
            ("$now", Iso));

    }

    private static async Task SeedManagedFileAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await SeedLabelAsync(
            connection,
            ManagedLabelId,
            ManagedArtifactId,
            SensitiveArtifactKind.ManagedWorkspaceFile,
            SessionId,
            cancellationToken);

        using (CovenantSqliteAuthorizationScope writer =
            CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.ManagedFileIntentMutation))
        {

            await ExecuteAsync(
                connection,
                """
                INSERT INTO managed_file_write_intents (
                    WriteOperationId, StableEffectIdentityDigest, ArtifactId, SensitivityLabelId,
                    SensitivityLabelDigest, PendingArtifactSensitivityLabel, DurableLocationEvidence,
                    ExpectedContentHash, ExpectedContentLength, PhaseCode, Revision, RetryCount,
                    CreatedAtUtc, UpdatedAtUtc)
                VALUES ($write, zeroblob(32), $artifact, $label, zeroblob(32), zeroblob(64),
                        zeroblob(64), zeroblob(32), 0, 1, 0, 0, $now, $now);
                """,
                cancellationToken,
                ("$write", WriteOperationId),
                ("$artifact", ManagedArtifactId),
                ("$label", ManagedLabelId),
                ("$now", Iso));

            // The phase machine advances exactly one step per revision, so adoption is walked rather
            // than jumped to. A seed that skipped straight to AdoptedAndLabeled would be creating a row
            // the product cannot produce, and the erasure work item hanging off it would then prove
            // nothing about a real adoption.
            await ExecuteAsync(
                connection,
                """
                UPDATE managed_file_write_intents
                SET PhaseCode = 2,
                    Revision = Revision + 1,
                    CreatedChildPhysicalIdentityDigest = zeroblob(32),
                    UpdatedAtUtc = $now
                WHERE WriteOperationId = $write;
                """,
                cancellationToken,
                ("$write", WriteOperationId),
                ("$now", Iso));

            for (int phase = 3; phase <= 6; phase++)
            {

                await ExecuteAsync(
                    connection,
                    """
                    UPDATE managed_file_write_intents
                    SET PhaseCode = $phase,
                        Revision = Revision + 1,
                        UpdatedAtUtc = $now
                    WHERE WriteOperationId = $write;
                    """,
                    cancellationToken,
                    ("$phase", phase),
                    ("$write", WriteOperationId),
                    ("$now", Iso));

            }

            await ExecuteAsync(
                connection,
                """
                UPDATE managed_file_write_intents
                SET PhaseCode = 7,
                    Revision = Revision + 1,
                    PendingArtifactSensitivityLabel = NULL,
                    FinalOwnershipEvidence = zeroblob(64),
                    UpdatedAtUtc = $now
                WHERE WriteOperationId = $write;
                """,
                cancellationToken,
                ("$write", WriteOperationId),
                ("$now", Iso));

        }

        long sourceRevision = await ScalarAsync(
            connection,
            $"SELECT Revision FROM managed_file_write_intents WHERE WriteOperationId = '{WriteOperationId}';",
            cancellationToken);

        using CovenantSqliteAuthorizationScope maintenance =
            CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO local_erasure_work_items (
                WorkItemId, ErasureOperationId, SourceWriteOperationId, ExpectedSourceRevision,
                ArtifactId, SourceSensitivityLabelId, DurableLocationEvidence,
                ExpectedOwnershipEvidence, StateCode, CheckpointRevision, RetryCount,
                CreatedAtUtc, UpdatedAtUtc)
            VALUES ($item, $operation, $write, $sourceRevision, $artifact, $label, zeroblob(64),
                    zeroblob(64), 1, 0, 0, $now, $now);
            """,
            cancellationToken,
            ("$item", WorkItemId),
            ("$operation", "9f3a1c44-0d21-4a6e-9c31-6f2b0d55e70b"),
            ("$write", WriteOperationId),
            ("$sourceRevision", sourceRevision),
            ("$artifact", ManagedArtifactId),
            ("$label", ManagedLabelId),
            ("$now", Iso));

    }

    private static async Task SeedDisclosureAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await ExecuteAsync(
            connection,
            """
            INSERT INTO external_disclosure_receipts (
                OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal, EffectCategoryCode,
                CategoryPhysicalAttemptOrdinal, EffectIdentityDigest, DestinationCode,
                RevocabilityCode, DestinationDigest, SensitivityCode,
                GenerationProvenanceModeCode, ExactGenerationIds, GenerationBloom, DisclosedAtUtc)
            VALUES ($origin, 2, $subject, 1, 4, 1, zeroblob(32), 1, 2, zeroblob(32), 1,
                    1, $generations, NULL, $now);
            """,
            cancellationToken,
            ("$origin", "ffffffff-6666-4666-8666-ffffffffffff"),
            ("$subject", "ffffffff-7777-4777-8777-ffffffffffff"),
            ("$generations", Enumerable.Repeat((byte)7, 16).ToArray()),
            ("$now", Iso));

        // Nonrevocable: exactly what the destructive-operation copy is a statement about.
        await ExecuteAsync(
            connection,
            """
            INSERT INTO external_disclosure_state (
                DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                MaxDisclosedAtUtcTicks, EvidenceBloom, UpdatedAtUtc)
            VALUES (1, 2, 1, 1, 3, 638000000000000000, $bloom, $now);
            """,
            cancellationToken,
            ("$bloom", Enumerable.Repeat((byte)9, 32).ToArray()),
            ("$now", Iso));

        // Locally revocable, and therefore deliberately outside the possible-attempt count: including
        // it would inflate the number an operator weighs with disclosures Arcanum can still undo.
        await ExecuteAsync(
            connection,
            """
            INSERT INTO external_disclosure_state (
                DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                MaxDisclosedAtUtcTicks, EvidenceBloom, UpdatedAtUtc)
            VALUES (2, 1, 1, 1, 11, 638000000000000000, $bloom, $now);
            """,
            cancellationToken,
            ("$bloom", Enumerable.Repeat((byte)5, 32).ToArray()),
            ("$now", Iso));

    }

    private static async Task SeedLabelAsync(
        SqliteConnection connection,
        string labelId,
        string artifactId,
        SensitiveArtifactKind kind,
        string sessionId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            connection,
            """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, $kind, $artifact, 1, 1, $generations, NULL, $session, NULL, NULL,
                    1, zeroblob(32), zeroblob(32), NULL, NULL, NULL, zeroblob(32), $now);
            """,
            cancellationToken,
            ("$label", labelId),
            ("$kind", (int)kind),
            ("$artifact", artifactId),
            ("$generations", Enumerable.Repeat((byte)7, 16).ToArray()),
            ("$session", sessionId),
            ("$now", Iso));

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

}
