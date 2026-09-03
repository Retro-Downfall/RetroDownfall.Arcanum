using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// A real encrypted Grimoire holding a whole Covenant family, the core evidence beside it, and the
/// retained markers, for the one transaction that erases the first and must not touch the rest.
/// </summary>
/// <remarks>
/// File-backed and real rather than a set of doubles, because every claim in issue #125 is a claim
/// about what SQLite does: the delete guards consult authorization functions that only exist on an
/// initialized connection, the canonical singleton's monotonic counters are enforced by a trigger
/// rather than by C#, and <c>external_disclosure_state</c> is protected by an unconditional abort
/// that a faked connection would never fire.
///
/// <para>The scratch connection is enrolled in the drain the erasure runs, which is not decoration.
/// An exclusive maintenance connection cannot take its lock while another handle holds the same WAL
/// open, so a transaction that skipped the drain would fail here rather than quietly pass.</para>
/// </remarks>
internal sealed class CovenantCanonicalErasureFixture : IAsyncDisposable
{

    private const string Iso = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private static readonly DateTimeOffset SeedTime = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid SessionOne = new("55555555-6666-4777-8888-999999999999");

    private readonly CovenantSchemaScratchDatabase? _database;

    private readonly SqliteConnection? _attachedConnection;

    private readonly IDesignTimeGrimoireConnectionFactory? _attachedConnections;

    private readonly ICovenantSqliteConnectionInitializer? _attachedInitializer;

    private readonly CovenantConnectionDrain? _scratchDrain;

    private readonly IDisposable _enrolment;

    private CovenantCanonicalErasureFixture(
        CovenantSchemaScratchDatabase database,
        CovenantConnectionDrain drain,
        IDisposable enrolment)
    {

        _database = database;

        _enrolment = enrolment;

        _scratchDrain = drain;

    }

    private CovenantCanonicalErasureFixture(
        SqliteConnection connection,
        IDesignTimeGrimoireConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer,
        IDisposable enrolment)
    {

        _attachedConnection = connection;

        _attachedConnections = connections;

        _attachedInitializer = initializer;

        _enrolment = enrolment;

    }

    internal SqliteConnection Connection => _database?.Connection
        ?? _attachedConnection
        ?? throw new ObjectDisposedException(nameof(CovenantCanonicalErasureFixture));

    internal CovenantConnectionDrain Drain => _scratchDrain
        ?? throw new InvalidOperationException("An attached production fixture exposes its production drain through DI.");

    internal InMemoryOsCredentialStore Credentials { get; } = new();

    internal static async Task<CovenantCanonicalErasureFixture> CreateAsync(
        CancellationToken cancellationToken,
        bool withAccelerator = true)
    {

        CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(cancellationToken);

        try
        {

            await database.InstallCoreObjectsAsync(
                [
                    "Campaigns",
                    "campaign_registry_state",
                    "capability_cleanup_state",
                    "grimoire_feature_schemas",
                    "external_disclosure_receipts",
                    "external_disclosure_receipts_guard_delete",
                    "external_disclosure_state",
                    "external_disclosure_state_guard_delete",
                    "disclosure_subject_state",
                    "disclosure_subject_state_guard_delete",
                    "disclosure_subject_aggregates",
                    "disclosure_subject_aggregates_guard_delete",
                    "covenant_schema_repair_intents",
                    .. CovenantRetainedEvidence.CoreObjects,
                ],
                cancellationToken);

            await database.ExecuteAsync(
                "INSERT OR IGNORE INTO campaign_registry_state (StateKey, RegistryEpoch) VALUES (1, 1);",
                cancellationToken);

            await database.InstallCanonicalAsync(cancellationToken);

            if (withAccelerator)
            {

                await database.InstallAcceleratorAsync(cancellationToken);

            }

            CovenantConnectionDrain drain = new();

            IDisposable enrolment = drain.Register(database.Connection);

            return new CovenantCanonicalErasureFixture(database, drain, enrolment);

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

    }

    /// <summary>
    /// Attaches the seeding/assertion surface to the exact production maintenance factory and drain
    /// resolved by an integrated host. The fixture owns only this enrolled handle; the provider owns
    /// the database, runtime generation, gate, writer, and every erasure component.
    /// </summary>
    internal static async Task<CovenantCanonicalErasureFixture> AttachAsync(
        IDesignTimeGrimoireConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer,
        ICovenantConnectionDrain drain,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections.OpenAsync(cancellationToken);

        IDisposable enrolment = drain.Register(connection);

        try
        {

            await initializer.InitializeAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                cancellationToken);

            return new CovenantCanonicalErasureFixture(
                connection,
                connections,
                initializer,
                enrolment);

        }
        catch
        {

            enrolment.Dispose();

            await connection.DisposeAsync();

            throw;

        }

    }

    /// <summary>Absolute path of the scratch Grimoire this fixture erases.</summary>
    internal string DatabasePath => _database?.DatabasePath
        ?? _attachedConnections?.DatabasePath
        ?? throw new ObjectDisposedException(nameof(CovenantCanonicalErasureFixture));

    /// <summary>
    /// Hands the erasure its own unpooled handle to this same file.
    /// </summary>
    internal IDesignTimeGrimoireConnectionFactory Connections() =>
        _database?.MaintenanceConnections()
        ?? _attachedConnections
        ?? throw new ObjectDisposedException(nameof(CovenantCanonicalErasureFixture));

    internal CovenantV3MaintenanceTestConnectionFactory V3Connections() =>
        new(Connections(), _attachedInitializer ?? CovenantSqliteConnectionInitializer.Instance);

    /// <summary>
    /// Seeds one of everything the erasure must remove, and one of everything it must not.
    /// </summary>
    internal async Task SeedAsync(CancellationToken cancellationToken)
    {

        await SeedCampaignAsync(cancellationToken);

        await SeedOwnerDeletionJournalAsync(cancellationToken);

        await SeedCleanupCursorAsync(cancellationToken);

        await SeedFeatureSchemaAsync(cancellationToken);

        await SeedFamilyAsync(cancellationToken);

        await SeedReceiptsAsync(cancellationToken);

        await SeedDisclosureAsync(cancellationToken);

        await CovenantRetainedEvidence.SeedAsync(Connection, Credentials, cancellationToken);

    }

    /// <summary>
    /// Seeds only acceptance content into an already-installed production catalog. Schema metadata
    /// and retained authority rows belong to bootstrap and are deliberately not duplicated here.
    /// </summary>
    internal async Task SeedAcceptanceStateAsync(CancellationToken cancellationToken)
    {

        await SeedCampaignAsync(cancellationToken);

        await SeedFamilyAsync(cancellationToken);

        await SeedReceiptsAsync(cancellationToken);

        await SeedDisclosureAsync(cancellationToken);

        await SeedSensitivityLabelAsync(cancellationToken);

    }

    internal async Task ReopenAsync(CancellationToken cancellationToken)
    {

        if (_database is { } database)
        {

            await database.ReopenAsync(cancellationToken);

            return;

        }

        if (Connection.State != System.Data.ConnectionState.Closed)
        {

            return;

        }

        await Connection.OpenAsync(cancellationToken);

        await _attachedInitializer!.InitializeAsync(
            Connection,
            CovenantSqliteConnectionMode.ReadWrite,
            cancellationToken);

    }

    internal Task<long> CountAsync(string table, CancellationToken cancellationToken) =>
        ScalarLongAsync($"SELECT COUNT(*) FROM \"{table}\";", cancellationToken);

    internal async Task<long> ScalarLongAsync(string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    internal async Task<string?> ScalarStringAsync(string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);

    }

    internal async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    internal async Task<bool> ObjectExistsAsync(
        string name,
        string type,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE "type" = $type AND "name" = $name
            LIMIT 1;
            """;

        _ = command.Parameters.AddWithValue("$type", type);

        _ = command.Parameters.AddWithValue("$name", name);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;

    }

    internal async Task<Guid?> ReadDatasetGenerationAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value is byte[] bytes ? new Guid(bytes) : null;

    }

    /// <summary>
    /// Reads the live canonical state and preselects the target a launch would have committed to.
    /// </summary>
    /// <remarks>
    /// The production preselection lives in the checkpoint initiator, which a transaction-level suite
    /// has no business standing up. What matters here is that the pair handed to the transaction is
    /// the pair a real launch would produce: the exact source on disk, and a target whose generation
    /// is fresh and whose three epochs are each the successor of their own source.
    /// </remarks>
    internal async Task<CovenantCanonicalDatasetTransition> PreselectAsync(
        CancellationToken cancellationToken)
    {

        CovenantOfflineTransitionEpochsV1 source = await ReadEpochsAsync(cancellationToken);

        return new CovenantCanonicalDatasetTransition(
            await ReadDatasetGenerationAsync(cancellationToken) ?? Guid.Empty,
            source,
            Guid.NewGuid(),
            new CovenantOfflineTransitionEpochsV1(
                source.AcceleratorEpoch + 1,
                source.KeyReclamationEpoch + 1,
                source.EnvelopeKeyEpoch + 1));

    }

    internal async Task<CovenantOfflineTransitionEpochsV1> ReadEpochsAsync(
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            SELECT AcceleratorEpoch, KeyReclamationEpoch, EnvelopeKeyEpoch
            FROM covenant_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new CovenantOfflineTransitionEpochsV1(
                (ulong)reader.GetInt64(0),
                (ulong)reader.GetInt64(1),
                (ulong)reader.GetInt64(2))
            : new CovenantOfflineTransitionEpochsV1(1, 1, 1);

    }

    internal Task<CovenantRetainedEvidenceSnapshot> CaptureRetainedAsync(CancellationToken cancellationToken) =>
        CovenantRetainedEvidence.CaptureAsync(Connection, Credentials, cancellationToken);

    internal Task AssertRetainedAsync(
        CovenantRetainedEvidenceSnapshot before,
        CancellationToken cancellationToken) =>
        CovenantRetainedEvidence.AssertRetainedAsync(before, Connection, Credentials, cancellationToken);

    public async ValueTask DisposeAsync()
    {

        _enrolment.Dispose();

        if (_database is { } database)
        {

            await database.DisposeAsync();

        }
        else if (_attachedConnection is { } connection)
        {

            await connection.DisposeAsync();

        }

    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Iso, CultureInfo.InvariantCulture);

    private async Task SeedCampaignAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, 'one', 'one', '/tmp/one', 1, '{}', $created, $created);
            """;

        _ = command.Parameters.AddWithValue("$id", CovenantOperationGateFixture.CampaignOne);

        _ = command.Parameters.AddWithValue("$created", Timestamp(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    /// <summary>
    /// Seeds two Campaign and one Session deletion event, with the cursor left behind them.
    /// </summary>
    /// <remarks>
    /// Distinct maxima per owner kind, and neither equal to the other: a reset that reset both cursors
    /// from one query, or that transposed them, would still satisfy a fixture whose two kinds happened
    /// to share a number.
    /// </remarks>
    private async Task SeedOwnerDeletionJournalAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO owner_deletion_events (Sequence, OwnerKindCode, OwnerId, OperationId, ExclusiveEffectDigest, DeletedAtUtc)
            VALUES (1, 1, $campaign, NULL, NULL, $deleted),
                   (2, 2, $session, NULL, NULL, $deleted),
                   (3, 1, $campaign, NULL, NULL, $deleted),
                   (4, 1, $campaign, NULL, NULL, $deleted);
            """;

        _ = command.Parameters.AddWithValue("$campaign", CovenantOperationGateFixture.CampaignTwo.ToString("D"));

        _ = command.Parameters.AddWithValue("$session", SessionOne.ToString("D"));

        _ = command.Parameters.AddWithValue("$deleted", Timestamp(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private async Task SeedCleanupCursorAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO capability_cleanup_state (
                CapabilityFamilyCode, AppliedCampaignSequence, AppliedSessionSequence, FullSweepRequired, UpdatedAtUtc)
            VALUES (1, 0, 0, 1, $updated);
            """;

        _ = command.Parameters.AddWithValue("$updated", Timestamp(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private async Task SeedFeatureSchemaAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO grimoire_feature_schemas (
                FamilyCode, TransactionTierCode, SchemaVersion, SourceDefinitionFingerprint,
                InstalledCatalogFingerprint, InstalledAtUtc, HealthCode, HealthDetailCode)
            VALUES (1, 1, 1, $source, $installed, $updated, 0, NULL);
            """;

        _ = command.Parameters.AddWithValue("$source", new string('A', 64));

        _ = command.Parameters.AddWithValue("$installed", "sha256-" + new string('b', 64));

        _ = command.Parameters.AddWithValue("$updated", Timestamp(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    /// <summary>
    /// Seeds one entry, its version, its head, its provenance leaf, its outbox delta, and its
    /// accelerator projection row.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than through <c>CovenantCanonicalFixture</c> because this suite needs
    /// the accelerator projection populated too, and the projection is the only place the erasure's
    /// "search IDs" claim can be observed.
    ///
    /// <para>The key epoch row is deliberately not inserted here. <c>covenant_heads_key_epoch_insert</c>
    /// creates it, and <c>covenant_heads_key_epoch_delete</c> recreates it on the way out — which is
    /// exactly why an erasure has to delete key epochs after heads rather than before, and why seeding
    /// one by hand would hide that ordering behind a duplicate-key failure.</para>
    /// </remarks>
    private async Task SeedFamilyAsync(CancellationToken cancellationToken)
    {

        Guid entryId = new("aaaaaaaa-1111-4111-8111-111111111111");

        Guid versionId = new("bbbbbbbb-2222-4222-8222-222222222222");

        await using (SqliteCommand command = Connection.CreateCommand())
        {

            command.CommandText = """
                INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
                VALUES ($entry, 1, NULL, 'tone', 'tone', $created);

                INSERT INTO covenant_versions (
                    VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
                    AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
                    RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
                    AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
                    RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
                    AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
                VALUES (
                    $version, $entry, 1, 1, 1, 'be brief', 'be brief',
                    $hash, $hash, 8, 0, 1,
                    1, 1, NULL, NULL, NULL,
                    NULL, NULL, NULL, $mutation,
                    $hash, $hash, $hash, NULL,
                    1, $hash, $created);

                INSERT INTO covenant_version_attachment_provenance (
                    VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
                    SourceRangeKindCode, SourceStart, SourceEnd, SourceTurnId, MaterializationReference)
                VALUES ($version, 0, $attachment, $attachment, 'notes.md', $hash, 1, NULL, NULL, NULL, NULL);

                UPDATE covenant_state SET NextSearchRowId = NextSearchRowId + 1 WHERE StateKey = 1;

                INSERT INTO covenant_heads (
                    EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode, ScopeCode,
                    CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc)
                VALUES ($entry, 1, $version, 1, 1, 1, NULL, 'tone', 8, 1, 1, $created);

                UPDATE covenant_state
                SET CanonicalSearchSequence = CanonicalSearchSequence + 1, UpdatedAtUtc = $created
                WHERE StateKey = 1;

                INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
                SELECT CanonicalSearchSequence, 0, 1, $entry, 1, $version FROM covenant_state WHERE StateKey = 1;
                """;

            _ = command.Parameters.AddWithValue("$entry", entryId.ToString("D"));

            _ = command.Parameters.AddWithValue("$version", versionId.ToString("D"));

            _ = command.Parameters.AddWithValue("$mutation", Guid.NewGuid().ToString("D"));

            _ = command.Parameters.AddWithValue("$attachment", Guid.NewGuid().ToString("D"));

            _ = command.Parameters.AddWithValue("$hash", CovenantRetainedEvidence.Digest(0x80).Bytes);

            _ = command.Parameters.AddWithValue("$created", Timestamp(SeedTime));

            _ = await command.ExecuteNonQueryAsync(cancellationToken);

        }

        if (!await ObjectExistsAsync("covenant_search_documents", "table", cancellationToken))
        {

            return;

        }

        await using SqliteCommand projection = Connection.CreateCommand();

        projection.CommandText = """
            INSERT INTO covenant_search_documents (
                SearchRowId, EntryId, LaneCode, VersionId, ScopeCode, CampaignId, LifecycleCode,
                NormalizedKey, AuthoredContent, CompiledContent, DatasetGeneration, CanonicalSearchSequence)
            SELECT 1, $entry, 1, $version, 1, NULL, 1, 'tone', 'be brief', 'be brief', DatasetGeneration, 1
            FROM covenant_state WHERE StateKey = 1;
            """;

        _ = projection.Parameters.AddWithValue("$entry", entryId.ToString("D"));

        _ = projection.Parameters.AddWithValue("$version", versionId.ToString("D"));

        _ = await projection.ExecuteNonQueryAsync(cancellationToken);

    }

    private async Task SeedReceiptsAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_turn_receipts (
                AssistantEntryId, SessionId, CampaignId, DatasetGeneration, PlanDigest, AttemptedAdmissionCount,
                AttemptChainHead, CommittedBranchDigest, LineageHeadDigest, ExternalDisclosureCount,
                DisclosureChainHead, ConfirmedTokenCost, ProposedTokenCost, MutationCount, FinalOutcomeCode,
                CreatedAtUtc)
            SELECT $assistant, $session, NULL, DatasetGeneration, $hash, $count,
                   $hash, $hash, $hash, $count,
                   $hash, 11, 3, 1, 1,
                   $created
            FROM covenant_state WHERE StateKey = 1;

            INSERT INTO covenant_turn_receipt_aggregate (
                SessionId, CoveredCount, EarliestCoveredAtUtc, LatestCoveredAtUtc, ConfirmedTokenTotal,
                ProposedTokenTotal, CompletedOutcomeCount, FailedOutcomeCount, CancelledOutcomeCount,
                InterruptedOutcomeCount, MutationTotal, ChainDigest, UpdatedAtUtc)
            VALUES ($session, 1, $created, $created, 11, 3, 1, 0, 0, 0, 1, $hash, $created);

            INSERT INTO covenant_mutation_receipts (
                MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, MutationKindCode,
                ScopeCode, CampaignId, TargetIdentityDigest, LaneCode, OutcomeCode, ResultingVersionId,
                ResultingLaneRevision, ResponseReceiptDigest, SourceTurnId, CommittedAtUtc)
            VALUES ($mutation, $hash, $hash, $hash, 1, 1, NULL, $hash, 1, 2, NULL, NULL, $hash, NULL, $created);
            """;

        _ = command.Parameters.AddWithValue("$assistant", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$session", SessionOne.ToString("D"));

        _ = command.Parameters.AddWithValue("$mutation", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$hash", CovenantRetainedEvidence.Digest(0x90).Bytes);

        _ = command.Parameters.AddWithValue("$count", new byte[8]);

        _ = command.Parameters.AddWithValue("$created", Timestamp(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    /// <summary>
    /// Seeds a nonrevocable receipt, the subject that owns it, its folded aggregate, and the joined
    /// installation-wide bucket.
    /// </summary>
    private async Task SeedDisclosureAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO disclosure_subject_state (
                OriginInstallationId, SubjectKind, SubjectId, LifecycleCode, CreatorBootId, LastHeartbeatAtUtc,
                ClosedAtUtc, ProviderAttemptCount, ExternalEffectCount, LastAllocatedOrdinal, LastFoldedOrdinal,
                DisclosureChainDigest)
            VALUES ($installation, 1, $subject, 3, 'boot-1', $created, $created, 1, 1, 1, 1, $hash);

            INSERT INTO external_disclosure_receipts (
                OriginInstallationId, SubjectKind, SubjectId, SubjectOrdinal, EffectCategoryCode,
                CategoryPhysicalAttemptOrdinal, EffectIdentityDigest, DestinationCode, RevocabilityCode,
                DestinationDigest, SensitivityCode, GenerationProvenanceModeCode, ExactGenerationIds,
                GenerationBloom, WardEvidenceDigest, AdmissionEvidenceDigest, BackupEvidenceDigest, DisclosedAtUtc)
            VALUES ($installation, 1, $subject, 1, 1, 1, $hash, 1, 2, $hash, 1, 1, $generation, NULL, NULL, NULL, NULL, $created);

            INSERT INTO disclosure_subject_aggregates (
                OriginInstallationId, SubjectKind, SubjectId, DestinationCode, RevocabilityCode, CountKindCode,
                FoldedCount, EverOccurred, MaxDisclosedAtUtcTicks, EvidenceBloom, UpdatedAtUtc)
            VALUES ($installation, 1, $subject, 1, 2, 1, 1, 1, 638000000000000000, $hash, $created);

            INSERT INTO external_disclosure_state (
                DestinationCode, RevocabilityCode, CountKindCode, EverOccurred, JoinedCount,
                MaxDisclosedAtUtcTicks, EvidenceBloom, UpdatedAtUtc)
            VALUES (1, 2, 1, 1, 3, 638000000000000000, $hash, $created);
            """;

        _ = command.Parameters.AddWithValue("$installation", "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90");

        _ = command.Parameters.AddWithValue("$subject", SessionOne.ToString("D"));

        _ = command.Parameters.AddWithValue("$hash", CovenantRetainedEvidence.Digest(0xA0).Bytes);

        _ = command.Parameters.AddWithValue("$generation", new byte[16]);

        _ = command.Parameters.AddWithValue("$created", Timestamp(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private async Task SeedSensitivityLabelAsync(CancellationToken cancellationToken)
    {

        Guid dataset = await ReadDatasetGenerationAsync(cancellationToken)
            ?? throw new InvalidOperationException("The installed Covenant catalog has no dataset generation.");

        ArtifactSensitivityLabel label = new(
            new Guid("dddddddd-1111-4111-8111-111111111111"),
            SensitiveArtifactKind.ToolArtifact,
            new Guid("eeeeeeee-2222-4222-8222-222222222222"),
            sessionId: null,
            campaignId: null,
            turnId: null,
            artifactRevision: 1,
            CovenantRetainedEvidence.Digest(0xB0),
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.Create([dataset]),
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null,
            SeedTime);

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES (
                $label, $kind, $artifact, $sensitivity, $mode, $generations, NULL, NULL, NULL, NULL,
                $revision, $content, $sensitivityDigest, NULL, NULL, NULL, $labelDigest, $created);
            """;

        _ = command.Parameters.AddWithValue("$label", label.LabelId.ToString("D"));

        _ = command.Parameters.AddWithValue("$kind", (long)label.ArtifactKind);

        _ = command.Parameters.AddWithValue("$artifact", label.ArtifactId.ToString("D"));

        _ = command.Parameters.AddWithValue("$sensitivity", (long)label.Sensitivity);

        _ = command.Parameters.AddWithValue("$mode", (long)label.Provenance.Mode);

        _ = command.Parameters.AddWithValue("$generations", label.Provenance.ToCanonicalExactBytes());

        _ = command.Parameters.AddWithValue("$revision", checked((long)label.ArtifactRevision));

        _ = command.Parameters.AddWithValue("$content", label.ArtifactContentDigest.Bytes);

        _ = command.Parameters.AddWithValue("$sensitivityDigest", label.SensitivityDigest.Bytes);

        _ = command.Parameters.AddWithValue("$labelDigest", label.LabelDigest.Bytes);

        _ = command.Parameters.AddWithValue("$created", Timestamp(label.CreatedAt));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

}
