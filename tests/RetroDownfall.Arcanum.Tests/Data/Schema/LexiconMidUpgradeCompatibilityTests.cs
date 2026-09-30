using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

[Trait("Category", "Integration")]
public sealed class LexiconMidUpgradeCompatibilityTests
{
    private static readonly Guid EntryId = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");

    private static readonly LexiconCurationScope Global = new(LexiconScopeKind.Global, null);

    static LexiconMidUpgradeCompatibilityTests() => SqliteNativeRuntime.Instance.Initialize();

    public static IEnumerable<object[]> Reads() =>
        from version in Enumerable.Range(2, 9)
        from operation in new[] { "exact", "fts", "like", "effective", "scoped", "show", "show-effective", "list", "inspection-search", "inspection-count", "delete", "annals-provenance" }
        select new object[] { version, operation };

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Existing_operations_remain_available_before_version_eleven(int version, string operation)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await PrepareAsync(file, version);

        await SeedAsync(connection);

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        LexiconService service = CreateService(db);

        if (operation == "like")
        {
            await ExecuteAsync(connection, "DROP TABLE lexicon_fts;");
        }

        LexiconEntryDto entry;

        if (operation is "exact" or "fts" or "like")
        {
            var result = await service.MatchEntitiesAsync([operation == "exact" ? "Entity" : "searchable"], 5, LexiconScope.Global);

            Assert.True(result.IsSuccess, result.Error.Message);

            entry = Assert.Single(result.Value);
        }
        else if (operation is "effective" or "scoped")
        {
            var result = operation == "effective"
                ? await service.GetByNameAsync("Entity", LexiconScope.ForCampaign(Guid.NewGuid()))
                : await service.GetByNameInScopeAsync("Entity", LexiconScope.Global);

            Assert.True(result.IsSuccess, result.Error.Message);

            entry = Assert.IsType<LexiconEntryDto>(result.Value);
        }
        else if (operation is "show" or "show-effective")
        {
            var result = operation == "show"
                ? await service.ShowExactAsync(Global, "Entity", null)
                : await service.ShowEffectiveAsync(new(LexiconScopeKind.Campaign, Guid.NewGuid()), "Entity", null);

            Assert.True(result.IsSuccess, result.Error.Message);

            entry = result.Value.Value.Entry;

            Assert.Equal(AnnalContentHashFormat.LegacyStoreDigest, Assert.Single(result.Value.Value.AnnalHistory).ContentHashFormat);

            Assert.Empty(result.Value.Value.HistoricalFactProvenance);
        }
        else if (operation is "list" or "inspection-search")
        {
            var result = operation == "list" ? await service.ListInspectionAsync(null)
                : await service.SearchInspectionAsync("searchable", 1, null);

            Assert.True(result.IsSuccess, result.Error.Message);

            entry = Assert.Single(result.Value.Value);
        }
        else if (operation == "inspection-count")
        {
            var result = await service.CountInspectionAsync(null);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(new LexiconInspectionCounts(1, 1), result.Value.Value);

            return;
        }
        else if (operation == "delete")
        {
            var result = await service.DeleteByNameAsync("Entity", LexiconScope.Global);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.True(result.Value);

            Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_entries;"));

            Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM annal_claims;"));

            return;
        }
        else
        {
            AnnalsStore annals = new(db);

            AnnalClaimHead head = (await annals.GetClaimAsync(AnnalSubjectStore.Lexicon, EntryId.ToString("N"), CancellationToken.None))!;

            Assert.Empty(await annals.GetLexiconFactProvenanceAsync(head.CurrentVersionId, CancellationToken.None));

            return;
        }

        Assert.Equal(EntryId, entry.Id);

        Assert.Equal(1, entry.CurationGeneration);

        Assert.Null(entry.RetiredAtUtc);

        Assert.Null(entry.PinnedAtUtc);

        Assert.Equal(LexiconRetrievalEligibility.Eligible, entry.Eligibility);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Ordinary_scribe_preserves_legacy_hashes_and_capture_policy(bool existing, bool capture)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await PrepareAsync(file, 10);

        if (existing)
        {
            await SeedAsync(connection);
        }

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        LexiconService service = CreateService(db, capture);

        var result = await service.UpsertAsync("Entity", "Concept", ["new fact"], LexiconScope.Global);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, result.Value.CurationGeneration);

        AnnalsStore annals = new(db);

        AnnalClaimHead? head = await annals.GetClaimAsync(AnnalSubjectStore.Lexicon, result.Value.Id.ToString("N"), CancellationToken.None);

        if (existing || capture)
        {
            Assert.NotNull(head);

            var versions = await annals.GetVersionsAsync(head.ClaimId, CancellationToken.None);

            Assert.Equal(existing ? 2 : 1, versions.Count);

            Assert.All(versions, item => Assert.Equal(AnnalContentHashFormat.LegacyStoreDigest, item.ContentHashFormat));

            Assert.Equal(AnnalContentDigest.ForLexiconEntry(result.Value.Type, string.Join('\n', result.Value.Facts)), versions[^1].ContentHash);
        }
        else
        {
            Assert.Null(head);
        }
    }

    [Theory]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task New_mutations_refuse_with_retryable_unavailability_until_transition_completes(string operation)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await PrepareAsync(file, 10);

        await SeedAsync(connection);

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        LexiconService service = CreateService(db);

        var before = await service.ShowExactAsync(Global, "Entity", null);

        Assert.True(before.IsSuccess, before.Error.Message);

        LexiconCurationTarget target = before.Value.Value.Target;

        Task<Result<LexiconCurationResult>> InvokeAsync() => operation switch
        {
            "correct" => service.CorrectAsync(target, new("Concept", ["replacement"]), null),
            "retire" => service.RetireAsync(target, null),
            "reinstate" => service.ReinstateAsync(target, null),
            "pin" => service.PinAsync(target, null),
            _ => service.UnpinAsync(target, null),
        };

        var result = await InvokeAsync();

        Assert.Equal("Lexicon.CurationUnavailable", result.Error.Code);

        Assert.Equal("Lexicon curation is temporarily unavailable while the Core schema transition completes.", result.Error.Message);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM annal_versions;"));

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionElevenFixture.ChainSet(), 64, CancellationToken.None);

        var retried = await InvokeAsync();

        Assert.True(retried.IsSuccess, retried.Error.Message);
    }

    internal static LexiconService CreateService(ArcanumDbContext db, bool capture = true)
    {
        CovenantSqliteConnectionInitializer.Instance.EnsureAuthorizationFunctions((SqliteConnection)db.Database.GetDbConnection());

        return new(db, NullLogger<LexiconService>.Instance, new TestOptionsMonitor<ArcanumSettings>(
            new ArcanumSettings { Features = new FeatureSettings { Annals = capture } }));
    }

    [Fact]
    public async Task Schema_capability_and_dependent_read_share_a_snapshot_across_a_real_upgrade()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await PrepareAsync(file, 10);

        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;");

        await SeedAsync(connection);

        await using SqliteConnection reader = await file.OpenAsync(CancellationToken.None);

        TaskCompletionSource measured = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<(int Version, object? Count)> reading = GrimoireCoreSchemaVersion.InSnapshotAsync(reader, async () =>
        {
            int version = await GrimoireCoreSchemaVersion.ReadAsync(reader, CancellationToken.None);

            measured.SetResult();

            await resume.Task.WaitAsync(TimeSpan.FromSeconds(20));

            return (version, await ScalarAsync(reader, "SELECT count(*) FROM annal_versions;"));
        }, CancellationToken.None);

        try
        {
            await measured.Task.WaitAsync(TimeSpan.FromSeconds(20));

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionElevenFixture.ChainSet(), 64, CancellationToken.None);

            await using SqliteTransaction transaction = connection.BeginTransaction();

            _ = await AnnalsClaimWriter.AppendCorrectionAsync(connection, transaction, AnnalSubjectStore.Lexicon,
                EntryId.ToString("N"), AnnalOrigin.AgentAsserted, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
                AnnalContentHashFormat.LexiconStructuredSnapshot,
                LexiconSnapshotDigest.Compute(LexiconValueNormalizer.NormalizeCorrection("Entity", "Concept", ["searchable fact"]).Value),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, CancellationToken.None);

            await transaction.CommitAsync();
        }
        finally
        {
            resume.TrySetResult();
        }

        (int version, object? count) = await reading.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(10, version);

        Assert.Equal(1L, count);

        Assert.Equal(11, await GrimoireCoreSchemaVersion.ReadAsync(reader, CancellationToken.None));

        Assert.Equal(2L, await ScalarAsync(reader, "SELECT count(*) FROM annal_versions;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_protected_inspection_never_guesses_a_digest_grammar(bool list)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await PrepareAsync(file, 10);

        await SeedAsync(connection);

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        ArtifactSensitivityLedger ledger = new(new CovenantConnectionSource(db, FixtureOrdinaryConnectionFactory.For(db)));

        var labeled = await ledger.LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.Lexicon, EntryId,
            null, null, null, 1, DerivedArtifactContentDigest.ForText("searchable fact"), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([CovenantOperationGateFixture.DatasetGeneration])), CancellationToken.None);

        Assert.True(labeled.IsSuccess, labeled.Error.Message);

        LexiconService service = CreateService(db);

        Error unauthorized = list ? (await service.ListInspectionAsync(null)).Error
            : (await service.ShowExactAsync(Global, "Entity", null)).Error;

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, unauthorized.Code);

        await using LegacyReadLease lease = new(list);

        Error unverified = list ? (await service.ListInspectionAsync(lease)).Error
            : (await service.ShowExactAsync(Global, "Entity", lease)).Error;

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, unverified.Code);

        Assert.DoesNotContain("searchable fact", unauthorized.Message + unverified.Message, StringComparison.Ordinal);
    }

    private sealed class LegacyReadLease(bool installation) : ICovenantSnapshotReadLease
    {
        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(Guid.NewGuid(), 1,
            installation ? CovenantLeaseKind.InstallationRead : CovenantLeaseKind.Read,
            installation ? CovenantLeaseCoverage.Installation : CovenantLeaseCoverage.Scoped,
            installation ? null : CovenantOperationScope.Global, null, 1, 1, 0, null, null, null, null, null, false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retention_plans_and_prunes_legacy_Lexicon_without_pin_columns(bool pinAfterUpgrade)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await PrepareAsync(file, 10);

        await SeedAsync(connection);

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        string root = Directory.CreateTempSubdirectory("arcanum-legacy-retention-").FullName;

        try
        {
            ArcanumSettings settings = new()
            {
                Retention = new RetentionSettings
                {
                    UploadedFiles = new(), CompletedBatches = new(), WorkspaceIndexes = new(),
                    SessionEntryEmbeddings = new(), AuditLogs = new(), GuardrailLogs = new(),
                    IdempotencyClaims = new(), Accounting = new(), LongRunningOperations = new(),
                    SanctumBreaches = new(), DaemonHistory = new(),
                    LexiconEntries = new() { Enabled = true, Days = 30 },
                },
            };

            DataRetentionService retention = new(db, new TestOptionsMonitor<ArcanumSettings>(settings),
                new LongRunningOperationStore(db, TestOrdinaryConnectionFactory.For(db)), TimeProvider.System,
                NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(db), root, root, root);

            DataRetentionRequest request = new(DataRetentionOperation.Prune);

            DataRetentionPlan plan = await retention.PlanAsync(request);

            Assert.Equal(new DataRetentionLexiconCurationInventory(0, 0), plan.LexiconCuration);

            Assert.Single(plan.CandidateIds);

            if (pinAfterUpgrade)
            {
                _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionElevenFixture.ChainSet(), 64, CancellationToken.None);

                LexiconService lexicon = CreateService(db);

                var shown = await lexicon.ShowExactAsync(Global, "Entity", null);

                Assert.True(shown.IsSuccess, shown.Error.Message);

                Assert.True((await lexicon.PinAsync(shown.Value.Value.Target, null)).IsSuccess);
            }

            var applied = await retention.ApplyAsync(new(request, plan.PlanId));

            if (pinAfterUpgrade)
            {
                Assert.True(applied.IsFailure);

                Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_entries WHERE PinnedAtUtc IS NOT NULL;"));

                Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM annal_claims;"));

                return;
            }

            Assert.True(applied.IsSuccess, applied.Error.Message);

            Assert.True(applied.Value.Reconciled);

            Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_entries;"));

            Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM annal_claims;"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Protected_purge_erases_legacy_Lexicon_and_Annals_without_version_provenance_table(bool staged, bool missingDeclaredTable)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await PrepareAsync(file, 10);

        await SeedAsync(connection);

        if (missingDeclaredTable)
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionElevenFixture.ChainSet(), 64, CancellationToken.None);

            await ExecuteAsync(connection, "DROP TABLE lexicon_annal_fact_provenance;");
        }

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        CovenantConnectionSource connections = new(db, FixtureOrdinaryConnectionFactory.For(db));

        await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
            await connections.GetOpenConnectionAsync(CancellationToken.None), CovenantSqliteConnectionMode.ReadWrite, CancellationToken.None);

        ArtifactSensitivityLedger ledger = new(connections);

        var labeled = await ledger.LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.Lexicon, EntryId,
            null, null, null, 1, DerivedArtifactContentDigest.ForText("searchable fact"),
            ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([CovenantOperationGateFixture.DatasetGeneration])), CancellationToken.None);

        Assert.True(labeled.IsSuccess, labeled.Error.Message);

        ArtifactSensitivityLabel label = (await ledger.TryReadLabelAsync(SensitiveArtifactKind.Lexicon, EntryId, CancellationToken.None)).Value!;

        if (staged)
        {
            SqliteConnection stagedConnection = await connections.GetOpenConnectionAsync(CancellationToken.None);

            await using SqliteTransaction transaction = stagedConnection.BeginTransaction();

            if (missingDeclaredTable)
            {
                _ = await Assert.ThrowsAsync<SqliteException>(() => BackupRestoreProtectedStatePurger.PurgeStagedAsync(
                    stagedConnection, transaction, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System, CancellationToken.None));

                await transaction.RollbackAsync();

                Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_entries;"));
            }
            else
            {
                var purged = await BackupRestoreProtectedStatePurger.PurgeStagedAsync(
                    stagedConnection, transaction, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System, CancellationToken.None);

                Assert.True(purged.IsSuccess, purged.Error.Message);

                await transaction.CommitAsync();

                Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_entries;"));

                await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(connection);
            }

            return;
        }

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize), CancellationToken.None)).Value;

        CovenantArtifactErasureAuthority authority = CovenantArtifactErasureAuthority
            .ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize).Value;

        CovenantProtectedArtifactErasureKernel kernel = new(connections, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System);

        var erased = await kernel.ErasePageAsync(new(CovenantOperationGateFixture.DatasetGeneration,
            [new(label.ArtifactId, label.ArtifactKind, label.SessionId, label.LabelId, label,
                label.ArtifactContentDigest, label.ArtifactRevision)]), authority);

        Assert.True(erased.IsSuccess, erased.Error.Message);

        if (missingDeclaredTable)
        {
            Assert.Equal(CovenantErasureBlocker.IntegrityFailure, erased.Value.Blocker);

            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_entries;"));

            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM artifact_sensitivity;"));

            return;
        }

        Assert.Equal(CovenantErasureBlocker.None, erased.Value.Blocker);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_entries;"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM annal_claims;"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM artifact_sensitivity;"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(connection);
    }

    [Fact]
    public void Saga_erasure_never_names_Lexicon_version_provenance()
    {
        Assert.DoesNotContain(AnnalsErasurePlan.ForStore(AnnalSubjectStore.Saga), step => step.Table == "lexicon_annal_fact_provenance");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_Lexicon_delete_requires_and_erases_declared_provenance_before_versions(bool missingTable)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await PrepareAsync(file, 10);

        await SeedAsync(connection);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionElevenFixture.ChainSet(), 64, CancellationToken.None);

        if (missingTable)
        {
            await ExecuteAsync(connection, "DROP TABLE lexicon_annal_fact_provenance;");
        }
        else
        {
            await ExecuteAsync(connection, """
                INSERT INTO lexicon_annal_fact_provenance
                    (AnnalVersionId, FactOrdinal, SessionId, AttachmentId, LogicalKey, AttachmentVersion,
                     AttachmentContentHash, MaterializedAt, SourceType)
                SELECT VersionId, 0, 'session', 'attachment', 'source', 1, 'digest', RecordedAtUtc, 'text'
                FROM annal_versions;
                CREATE TRIGGER require_provenance_erased_first BEFORE DELETE ON annal_versions
                WHEN EXISTS (SELECT 1 FROM lexicon_annal_fact_provenance WHERE AnnalVersionId = OLD.VersionId)
                BEGIN SELECT RAISE(ABORT, 'provenance must be erased first'); END;
                """);
        }

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        var result = await CreateService(db).DeleteByNameAsync("Entity", LexiconScope.Global);

        Assert.Equal(missingTable, result.IsFailure);

        Assert.Equal(missingTable ? 1L : 0L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_entries;"));

        Assert.Equal(missingTable ? 1L : 0L, await ScalarAsync(connection, "SELECT count(*) FROM annal_claims;"));

        if (!missingTable)
        {
            Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM lexicon_annal_fact_provenance;"));
        }
    }

    internal static async Task<SqliteConnection> PrepareAsync(EvolutionScratchDatabase file, int version)
    {
        SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaVersionChainSet chain = version switch
        {
            2 => CoreSchemaVersionTwoFixture.ChainSet(),
            3 => CoreSchemaVersionThreeFixture.ChainSet(),
            4 => CoreSchemaVersionFourFixture.ChainSet(),
            5 => CoreSchemaVersionFiveFixture.ChainSet(),
            6 => CoreSchemaVersionSixFixture.ChainSet(),
            7 => CoreSchemaVersionSevenFixture.ChainSet(),
            8 => CoreSchemaVersionEightFixture.ChainSet(),
            9 => CoreSchemaVersionNineFixture.ChainSet(),
            10 => CoreSchemaVersionTenFixture.ChainSet(),
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, chain, 64, CancellationToken.None);

        if (version == 2)
        {
            GrimoireSchemaInstallResult pending = await GrimoireSchemaTestInstaller.InstallAsync(
                connection, CoreSchemaVersionElevenFixture.ChainSet(), 64, CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, pending.Core.Health);

            Assert.Equal(2L, await ScalarAsync(connection,
                "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 0 AND TransactionTierCode = 0"));

            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT count(*) FROM grimoire_schema_transitions WHERE FamilyCode = 0 AND TransactionTierCode = 0 AND FromVersion = 2 AND BackfillName IS NOT NULL"));

            Assert.Equal(1L, await ScalarAsync(connection,
                "SELECT count(*) FROM sqlite_master WHERE name = 'annal_versions' AND type = 'table'"));
        }

        return connection;
    }

    internal static async Task SeedAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, """
            INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt, ScopeCampaignId)
            VALUES ('aaaaaaaa111141118111111111111111', 'Entity', 'ENTITY', 'Concept', '["searchable fact"]', 'searchable fact', '2025-01-01T00:00:00.0000000Z', '');
            """);

        await using SqliteTransaction transaction = connection.BeginTransaction();

        _ = await AnnalsClaimWriter.AppendAssertAsync(connection, transaction, AnnalSubjectStore.Lexicon,
            EntryId.ToString("N"), AnnalOrigin.AgentAsserted, SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            AnnalContentHashFormat.LegacyStoreDigest, AnnalContentDigest.ForLexiconEntry("Concept", "searchable fact"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, CancellationToken.None, legacySchema: true);

        await transaction.CommitAsync();
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    internal static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}
