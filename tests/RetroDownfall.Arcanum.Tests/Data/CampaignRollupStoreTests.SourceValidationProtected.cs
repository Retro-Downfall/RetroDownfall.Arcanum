using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class CampaignRollupStoreTests
{
    private readonly ITestOutputHelper _sourceValidationOutput;

    public CampaignRollupStoreTests(ITestOutputHelper sourceValidationOutput) => _sourceValidationOutput = sourceValidationOutput;

    [Theory]
    [InlineData(false, false, 127)]
    [InlineData(false, true, 127)]
    [InlineData(true, false, 127)]
    [InlineData(true, true, 127)]
    [InlineData(false, false, 128)]
    [InlineData(false, true, 128)]
    [InlineData(true, false, 128)]
    [InlineData(true, true, 128)]
    [InlineData(false, false, 257)]
    [InlineData(false, true, 257)]
    [InlineData(true, false, 257)]
    [InlineData(true, true, 257)]
    public async Task Native_source_validation_preserves_legacy_and_shared_page_boundaries(bool legacy, bool validate, int count)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync(legacy);

        for (int index = 0; index < count; index++)
        {
            Guid session = index == 0 ? SessionA : Guid.Parse($"{index:x8}-ABCD-4000-8000-000000000000");

            if (index > 0)
            {
                await fixture.AddSessionAsync(session, "source decision");
            }

            CampaignContributionInput input = (await fixture.Store.PrepareContributionAsync(session, null, null, CancellationToken.None)).Value!;

            Result<CampaignRollupArtifact> contribution = await fixture.Store.PublishContributionAsync(input, "source decision", null, null, CancellationToken.None);

            Assert.True(contribution.IsSuccess, contribution.Error.Message);
        }

        CampaignRollupArtifact? current = null;

        int folds = 0;

        while ((await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None)).Value is { } input)
        {
            Result<CampaignRollupArtifact> result = await fixture.Store.PublishRollupAsync(input, "Campaign summary", null, null, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);

            current = result.Value;

            folds++;
        }

        Assert.NotNull(current);

        Assert.Equal(count, current.SourceCount);

        using SqliteStatementTrace trace = new(fixture.Connection);

        if (validate)
        {
            Result result = await fixture.Store.ValidateAsync(current, null, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);
        }
        else
        {
            Result<CampaignRollupArtifact?> result = await fixture.Store.ReadCurrentAsync(Campaign, null, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(current, result.Value);
        }

        int pages = (count + 127) / 128;

        Assert.True(trace.Count <= 20 + 10 * pages + 4 * folds, $"total={trace.Count}, pages={pages}, folds={folds}");

        _sourceValidationOutput.WriteLine("clean_total={0}; sources={1}; pages={2}; folds={3}; legacy={4}", trace.Count, count, pages, folds, legacy);
    }

    [Theory]
    [InlineData(false, 127)]
    [InlineData(true, 127)]
    [InlineData(false, 128)]
    [InlineData(true, 128)]
    [InlineData(false, 257)]
    [InlineData(true, 257)]
    public async Task Native_source_validation_preserves_mixed_protected_page_evidence(bool validate, int count)
    {
        await using SourceValidationProtectedFixture fixture = await SourceValidationProtectedFixture.CreateAsync(count);

        Assert.Equal(count, fixture.Current.SourceCount);

        Assert.Equal(ContentSensitivity.CovenantDerived, fixture.Current.Sensitivity);

        Assert.Equal(2, fixture.Current.Provenance.ExactGenerationIds.Length);

        using SqliteStatementTrace trace = new(fixture.Connection);

        Result result = await SourceReadOrValidateAsync(fixture, validate, fixture.ReadLease, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        int pages = (count + 127) / 128;

        int metadata = trace.Statements.Count(static sql => sql.Contains("WITH requested(SessionId, ArtifactId)", StringComparison.Ordinal));

        int labels = trace.Statements.Count(static sql => sql.Contains("FROM artifact_sensitivity label", StringComparison.Ordinal)
            && sql.Contains(" IN (", StringComparison.Ordinal));

        _sourceValidationOutput.WriteLine("protected_total={0}; metadata_batches={1}; label_batches={2}; sources={3}; pages={4}; remaining_authority_root_and_manifest_statements={5}",
            trace.Count, metadata, labels, count, pages, trace.Count - metadata - labels);

        Assert.Equal(pages, metadata);

        Assert.Equal(pages, labels);
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "SensitivityDigest")]
    [InlineData(true, "SensitivityDigest")]
    [InlineData(false, "ArtifactLabelDigest")]
    [InlineData(true, "ArtifactLabelDigest")]
    public async Task Native_source_validation_retains_protected_legacy_manifest_proofs(bool validate, string corruption)
    {
        await using SourceValidationProtectedFixture fixture = await SourceValidationProtectedFixture.CreateAsync(3, legacy: true);

        Assert.Equal(3, fixture.Current.SourceCount);

        Assert.True((await SourceReadOrValidateAsync(fixture, validate, fixture.ReadLease, CancellationToken.None)).IsSuccess);

        if (corruption != "none")
        {
            string field = corruption switch
            {
                "SensitivityDigest" => "SensitivityDigest",
                "ArtifactLabelDigest" => "ArtifactLabelDigest",
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            };

            await SourceSqlAsync(fixture.Connection,
                "DROP TRIGGER artifact_sensitivity_guard_update; UPDATE artifact_sensitivity SET " + field
                + " = zeroblob(32) WHERE ArtifactKindCode = $kind AND ArtifactId = $artifact;",
                ("$kind", (int)SensitiveArtifactKind.CampaignContribution), ("$artifact", fixture.ProtectedArtifacts[0]));

            Result result = await SourceReadOrValidateAsync(fixture, validate, fixture.ReadLease, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);
        }
    }

    [Theory]
    [InlineData(false, "foreign")]
    [InlineData(true, "foreign")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    [InlineData(false, "SensitivityDigest")]
    [InlineData(true, "SensitivityDigest")]
    [InlineData(false, "ArtifactLabelDigest")]
    [InlineData(true, "ArtifactLabelDigest")]
    public async Task Native_source_validation_refuses_corrupt_protected_contribution_label(bool validate, string corruption)
    {
        await using SourceValidationProtectedFixture fixture = await SourceValidationProtectedFixture.CreateAsync(3);

        Assert.True((await SourceReadOrValidateAsync(fixture, validate, fixture.ReadLease, CancellationToken.None)).IsSuccess);

        Guid artifact = fixture.ProtectedArtifacts[0];

        await SourceSqlAsync(fixture.Connection, "DROP TRIGGER artifact_sensitivity_guard_update;");

        if (corruption == "duplicate")
        {
            string columns = ArtifactSensitivityLedger.LabelColumnList + ", SensitivityDigest, ArtifactLabelDigest";

            string values = string.Join(", ", columns.Split(',').Select(static column => column.Trim() switch
            {
                "LabelId" => "$newLabel",
                "ArtifactId" => "lower(ArtifactId)",
                string name => name,
            }));

            await SourceSqlAsync(fixture.Connection,
                $"INSERT INTO artifact_sensitivity ({columns}) SELECT {values} FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND ArtifactId = $artifact;",
                ("$newLabel", Guid.NewGuid()), ("$kind", (int)SensitiveArtifactKind.CampaignContribution), ("$artifact", artifact));

            Assert.Equal(2, await SourceScalarAsync(fixture.Connection,
                "SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND lower(replace(ArtifactId, '-', '')) = $key;",
                ("$kind", (int)SensitiveArtifactKind.CampaignContribution), ("$key", artifact.ToString("N"))));
        }
        else
        {
            string expression = corruption switch
            {
                "foreign" => "ArtifactId = lower(replace(ArtifactId, '-', ''))",
                "malformed" => "ExactGenerationIds = x'01'",
                "SensitivityDigest" => "SensitivityDigest = zeroblob(32)",
                "ArtifactLabelDigest" => "ArtifactLabelDigest = zeroblob(32)",
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            };

            if (corruption == "malformed")
            {
                await SourceSqlAsync(fixture.Connection, "PRAGMA ignore_check_constraints = ON;");
            }

            await SourceSqlAsync(fixture.Connection,
                "UPDATE artifact_sensitivity SET " + expression + " WHERE ArtifactKindCode = $kind AND ArtifactId = $artifact;",
                ("$kind", (int)SensitiveArtifactKind.CampaignContribution), ("$artifact", artifact));
        }

        Result result = await SourceReadOrValidateAsync(fixture, validate, fixture.ReadLease, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.Equal(fixture.PublishedRollups, await SourceScalarAsync(fixture.Connection, "SELECT COUNT(*) FROM campaign_rollup_artifacts;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_source_validation_checks_last_protected_label_after_two_full_pages(bool validate)
    {
        await using SourceValidationProtectedFixture fixture = await SourceValidationProtectedFixture.CreateAsync(257);

        Assert.Equal(129, fixture.ProtectedArtifacts.Count);

        Assert.True((await SourceReadOrValidateAsync(fixture, validate, fixture.ReadLease, CancellationToken.None)).IsSuccess);

        await SourceSqlAsync(fixture.Connection,
            "DROP TRIGGER artifact_sensitivity_guard_update; UPDATE artifact_sensitivity SET ArtifactLabelDigest = zeroblob(32) WHERE ArtifactKindCode = $kind AND ArtifactId = $artifact;",
            ("$kind", (int)SensitiveArtifactKind.CampaignContribution), ("$artifact", fixture.ProtectedArtifacts[^1]));

        Result result = await SourceReadOrValidateAsync(fixture, validate, fixture.ReadLease, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_source_validation_refuses_a_missing_protected_read_capability(bool validate)
    {
        await using SourceValidationProtectedFixture fixture = await SourceValidationProtectedFixture.CreateAsync(3);

        Result result = await SourceReadOrValidateAsync(fixture, validate, null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, result.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_source_validation_revalidates_real_authority_between_protected_sources(bool validate)
    {
        await using SourceValidationProtectedFixture fixture = await SourceValidationProtectedFixture.CreateAsync(3);

        SourceValidationRevokingLease observed = new(fixture.ReadLease, fixture.Authority);

        SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (object? _, string sql) =>
        {
            if (sql.Contains("WITH requested(SessionId, ArtifactId)", StringComparison.Ordinal))
            {
                observed.InSourcePage = true;
            }
        }, null);

        Result result;

        try
        {
            result = await SourceReadOrValidateAsync(fixture, validate, observed, CancellationToken.None);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(observed.Revoked);

        Assert.True(observed.RefusedByRealLease);

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_source_validation_rechecks_authority_before_decoding_a_label_page(bool validate)
    {
        await using SourceValidationProtectedFixture fixture = await SourceValidationProtectedFixture.CreateAsync(3);

        await SourceSqlAsync(fixture.Connection,
            "DROP TRIGGER artifact_sensitivity_guard_update; PRAGMA ignore_check_constraints = ON;"
            + " UPDATE artifact_sensitivity SET ExactGenerationIds = x'01' WHERE ArtifactKindCode = $kind;",
            ("$kind", (int)SensitiveArtifactKind.CampaignContribution));

        bool revokedAtLabels = false;

        SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (object? _, string sql) =>
        {
            if (!revokedAtLabels && sql.Contains("FROM artifact_sensitivity label", StringComparison.Ordinal) && sql.Contains(" IN (", StringComparison.Ordinal))
            {
                revokedAtLabels = true;

                fixture.Authority.Advance();
            }
        }, null);

        Result result;

        try
        {
            result = await SourceReadOrValidateAsync(fixture, validate, fixture.ReadLease, CancellationToken.None);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(revokedAtLabels);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, result.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_source_validation_preserves_caller_cancellation_at_native_label_page(bool validate)
    {
        await using SourceValidationProtectedFixture fixture = await SourceValidationProtectedFixture.CreateAsync(3);

        using CancellationTokenSource cancellation = new();

        bool cancelledAtLabels = false;

        SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (object? _, string sql) =>
        {
            if (sql.Contains("FROM artifact_sensitivity label", StringComparison.Ordinal) && sql.Contains(" IN (", StringComparison.Ordinal))
            {
                cancelledAtLabels = true;

                cancellation.Cancel();
            }
        }, null);

        try
        {
            OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SourceReadOrValidateAsync(fixture, validate, fixture.ReadLease, cancellation.Token));

            Assert.True(cancellation.IsCancellationRequested);

            _sourceValidationOutput.WriteLine("store_cancelled_exception={0}; driver_exception_token_matches_caller={1}", cancelled.GetType().Name, cancelled.CancellationToken == cancellation.Token);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(cancelledAtLabels);

        Assert.Equal(fixture.PublishedRollups, await SourceScalarAsync(fixture.Connection, "SELECT COUNT(*) FROM campaign_rollup_artifacts;"));
    }

    [Fact]
    public async Task Native_source_validation_direct_reader_cancellation_control()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();

        using CancellationTokenSource cancellation = new();

        bool cancelledAtRead = false;

        SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (object? _, string sql) =>
        {
            if (sql.StartsWith("SELECT Content FROM Entries", StringComparison.Ordinal))
            {
                cancelledAtRead = true;

                cancellation.Cancel();
            }
        }, null);

        try
        {
            await using SqliteCommand command = fixture.Connection.CreateCommand();

            command.CommandText = "SELECT Content FROM Entries;";

            OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellation.Token);

                _ = await reader.ReadAsync(cancellation.Token);
            });

            _sourceValidationOutput.WriteLine("direct_cancelled_exception={0}; driver_exception_token_matches_caller={1}", cancelled.GetType().Name, cancelled.CancellationToken == cancellation.Token);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(fixture.Connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.True(cancelledAtRead);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_source_validation_retains_exact_contribution_when_source_generation_advances(bool legacy, bool validate)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync(legacy);

        CampaignRollupArtifact current = await fixture.PublishCampaignAsync("Campaign summary");

        await fixture.ExecuteAsync("UPDATE campaign_contribution_state SET SourceGeneration = SourceGeneration + 1;");

        if (validate)
        {
            Result result = await fixture.Store.ValidateAsync(current, null, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);
        }
        else
        {
            Result<CampaignRollupArtifact?> result = await fixture.Store.ReadCurrentAsync(Campaign, null, CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(current, result.Value);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_source_validation_refuses_duplicate_parsed_source_identities(bool legacy, bool validate)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync(legacy);

        CampaignRollupArtifact current = await fixture.PublishCampaignAsync("Campaign summary");

        string table = legacy ? "campaign_rollup_sources" : "campaign_rollup_manifest_sources";

        string columns = legacy
            ? "RollupArtifactId, SessionId, CampaignId, RollupRevision, ContributionArtifactId, ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence"
            : "ManifestId, LineageId, CampaignId, SessionId, ContributionArtifactId, ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence";

        string values = string.Join(", ", columns.Split(',').Select(static column => column.Trim() == "SessionId" ? "lower(SessionId)" : column.Trim()));

        await fixture.ExecuteAsync("PRAGMA foreign_keys = OFF; DROP TRIGGER " + table + "_guard_insert; INSERT INTO " + table
            + " (" + columns + ") SELECT " + values + " FROM " + table + ";");

        Assert.Equal(2, await fixture.CountAsync("SELECT COUNT(*) FROM " + table + ";"));

        if (validate)
        {
            Assert.True((await fixture.Store.ValidateAsync(current, null, CancellationToken.None)).IsFailure);
        }
        else
        {
            Assert.True((await fixture.Store.ReadCurrentAsync(Campaign, null, CancellationToken.None)).IsFailure);
        }
    }

    private static async Task<Result> SourceReadOrValidateAsync(SourceValidationProtectedFixture fixture, bool validate,
        ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken)
    {
        if (validate)
        {
            return await fixture.Store.ValidateAsync(fixture.Current, authority, cancellationToken);
        }

        Result<CampaignRollupArtifact?> result = await fixture.Store.ReadCurrentAsync(Campaign, authority, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error;
        }

        Assert.Equal(fixture.Current, result.Value);

        return Result.Success();
    }

    private static async Task SourceSqlAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> SourceScalarAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class SourceValidationRevokingLease(ICovenantSnapshotReadLease inner, FakeCovenantAuthorityProvider authority) : ICovenantSnapshotReadLease
    {
        internal bool InSourcePage { get; set; }

        internal bool Revoked { get; private set; }

        internal bool RefusedByRealLease { get; private set; }

        public CovenantOperationLeaseSnapshot Snapshot => inner.Snapshot;

        public CancellationToken Revocation => inner.Revocation;

        public async ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {
            Result result = await inner.RevalidateAsync(cancellationToken);

            if (result.IsFailure)
            {
                RefusedByRealLease = true;
            }
            else if (InSourcePage && !Revoked)
            {
                authority.Advance();

                Revoked = true;
            }

            return result;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SourceValidationProtectedFixture(CovenantCanonicalFixture database, CovenantReadLease readLease,
        FakeCovenantAuthorityProvider authority, SessionTurnClaimLease claim) : IAsyncDisposable
    {
        internal SqliteConnection Connection => database.Connection;

        internal CampaignRollupStore Store { get; } = new(new FixedCovenantConnectionSource(database.Connection), CovenantSqliteConnectionInitializer.Instance);

        internal CovenantReadLease ReadLease => readLease;

        internal FakeCovenantAuthorityProvider Authority => authority;

        internal CampaignRollupArtifact Current { get; private set; } = null!;

        internal int PublishedRollups { get; private set; }

        internal List<Guid> ProtectedArtifacts { get; } = [];

        internal static async Task<SourceValidationProtectedFixture> CreateAsync(int count, bool legacy = false)
        {
            CovenantCanonicalFixture database = await CovenantCanonicalFixture.CreateAsync(CancellationToken.None,
                coreObjects: legacy
                    ? [.. CovenantCapacityFixture.CoreObjects, "Entries", "session_campaign_bindings", "artifact_sensitivity", "session_sensitivity_state",
                        "assistant_entry_finalizations", "session_turn_claims", "artifact_sensitivity_guard_update", .. CovenantSchemaScratchDatabase.ArtifactSensitivityDeleteGuardObjects]
                    : [.. GrimoireSchemaCatalog.CoreObjects.Select(static item => item.Name)]);

            CovenantReadLease? read = null;

            try
            {
                if (legacy)
                {
                    foreach (GrimoireSchemaObject item in CoreSchemaVersionTwentyFourFixture.Objects.Where(static item => item.Name.StartsWith("campaign_", StringComparison.Ordinal)))
                    {
                        await SourceSqlAsync(database.Connection, item.Sql);
                    }

                    foreach (GrimoireSchemaObject item in GrimoireSchemaCatalog.CoreObjects.Where(static item => item.Name.Contains("campaign_contribution", StringComparison.Ordinal)
                        && !item.Name.StartsWith("campaign_", StringComparison.Ordinal)))
                    {
                        await SourceSqlAsync(database.Connection, item.Sql);
                    }
                }

                await database.AddCampaignAsync(Campaign, "source-validation", CancellationToken.None);

                Guid claimSession = Guid.NewGuid();

                await CovenantCapacityFixture.AddSessionAsync(database, claimSession, CancellationToken.None);

                using (CovenantSqliteAuthorizationScope binding = CovenantSqliteConnectionInitializer.Instance.Authorize(database.Connection, CovenantSqliteAuthorizationKind.SessionBindingWrite))
                {
                    await SourceSqlAsync(database.Connection,
                        "INSERT INTO session_campaign_bindings(SessionId, BindingKindCode, CampaignId, BoundAtUtc) VALUES($session, 2, $campaign, $now);",
                        ("$session", claimSession), ("$campaign", Campaign), ("$now", UtcInstantText.Format(DateTimeOffset.UtcNow)));
                }

                await SourceSqlAsync(database.Connection,
                    "INSERT OR IGNORE INTO installation_turn_quota_state(StateKey, ClaimCount, ReservedFinalizationCount, ConsumedFinalizationCount) VALUES(1, 0, 0, 0);");

                SessionTurnRequestIdentity request = new(Guid.NewGuid(), 0, Guid.NewGuid(), claimSession, SessionTurnSurface.Intelligence,
                    CovenantOperationGateFixture.Digest(1), CovenantOperationGateFixture.Digest(2), null, 0, 0);

                Result<SessionTurnClaimLease> acquired = await new SessionTurnClaimStore(new FixedCovenantConnectionSource(database.Connection),
                    new CovenantQuotaGuard(), Guid.NewGuid()).AcquireAsync(request, CancellationToken.None);

                Assert.True(acquired.IsSuccess, acquired.Error.Message);

                FakeCovenantAuthorityProvider authority = new();

                FakeCovenantCampaignScopeProbe scopes = new();

                scopes.Set(Campaign, CovenantCampaignScopeState.Live);

                CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority, campaigns: scopes);

                Result<CovenantReadLease> opened = await gate.AcquireReadAsync(CovenantOperationScope.ForCampaign(Campaign), CancellationToken.None);

                Assert.True(opened.IsSuccess, opened.Error.Message);

                read = opened.Value;

                SourceValidationProtectedFixture fixture = new(database, read, authority, acquired.Value);

                for (int index = 0; index < count; index++)
                {
                    Guid session = Guid.Parse($"{index + 1:x8}-ABCD-4000-8000-000000000000");

                    Guid entry = Guid.NewGuid();

                    await CovenantCapacityFixture.AddSessionAsync(database, session, CancellationToken.None);

                    using (CovenantSqliteAuthorizationScope binding = CovenantSqliteConnectionInitializer.Instance.Authorize(database.Connection, CovenantSqliteAuthorizationKind.SessionBindingWrite))
                    {
                        await SourceSqlAsync(database.Connection,
                            "INSERT INTO session_campaign_bindings(SessionId, BindingKindCode, CampaignId, BoundAtUtc) VALUES($session, 2, $campaign, $now);"
                            + " INSERT INTO Entries(Id, SessionId, Role, Content, ModelUsed, CreatedAt, Sequence) VALUES($entry, $session, 1, 'source decision', '', $now, 1);",
                            ("$session", session), ("$campaign", Campaign), ("$entry", entry), ("$now", UtcInstantText.Format(DateTimeOffset.UtcNow)));
                    }

                    bool protectedSource = index % 2 == 0;

                    if (protectedSource)
                    {
                        Guid generation = index % 4 == 0 ? Guid.Parse("A18D8877-0000-4000-8000-000000000001") : Guid.Parse("A18D8877-0000-4000-8000-000000000002");

                        Result<LabeledArtifactWriteReceipt> labeled = await new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(database.Connection))
                            .LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.AssistantEntry, entry, session, Campaign, null, 0,
                                DerivedArtifactContentDigest.ForText("source decision"), ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([generation])), CancellationToken.None);

                        Assert.True(labeled.IsSuccess, labeled.Error.Message);
                    }

                    Result<CampaignContributionInput?> prepared = await fixture.Store.PrepareContributionAsync(session, null, read, CancellationToken.None);

                    Assert.True(prepared.IsSuccess, prepared.Error.Message);

                    Assert.NotNull(prepared.Value);

                    CampaignContributionInput input = prepared.Value;

                    CampaignMaintenancePublication? publication = protectedSource
                        ? await fixture.AcknowledgeAsync(CovenantMaintenanceStep.CampaignContribution, session, input.SourceManifestDigest, input.SourceGeneration, input.ExpectedRevision)
                        : null;

                    Result<CampaignRollupArtifact> contributed = await fixture.Store.PublishContributionAsync(input, "source decision", publication, read, CancellationToken.None);

                    Assert.True(contributed.IsSuccess, contributed.Error.Message);

                    if (protectedSource)
                    {
                        fixture.ProtectedArtifacts.Add(contributed.Value.ArtifactId);
                    }
                }

                while (true)
                {
                    Result<CampaignRollupInput?> prepared = await fixture.Store.PrepareRollupAsync(Campaign, read, CancellationToken.None);

                    Assert.True(prepared.IsSuccess, prepared.Error.Message);

                    if (prepared.Value is not { } input)
                    {
                        break;
                    }

                    CampaignMaintenancePublication publication = await fixture.AcknowledgeAsync(CovenantMaintenanceStep.CampaignRollup, null,
                        input.SourceManifestDigest, input.SourceGeneration, input.ExpectedRevision);

                    Result<CampaignRollupArtifact> published = await fixture.Store.PublishRollupAsync(input, "Campaign summary", publication, read, CancellationToken.None);

                    Assert.True(published.IsSuccess, published.Error.Message);

                    fixture.Current = published.Value;

                    fixture.PublishedRollups++;
                }

                return fixture;
            }
            catch
            {
                if (read is not null)
                {
                    await read.DisposeAsync();
                }

                await database.DisposeAsync();

                throw;
            }
        }

        private async Task<CampaignMaintenancePublication> AcknowledgeAsync(CovenantMaintenanceStep step, Guid? source,
            CovenantDigest manifest, long generation, long revision)
        {
            long epoch = await SourceScalarAsync(Connection, "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;");

            CampaignMaintenanceIdentity identity = new(claim, CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(Campaign), epoch, 1, null, null),
                step, source, manifest, generation, revision);

            CampaignMaintenanceCheckpointStore store = new(new FixedCovenantConnectionSource(Connection));

            Result<CampaignMaintenanceAttempt> prepared = await store.PrepareAttemptAsync(identity, CovenantOperationGateFixture.Digest(7), CancellationToken.None);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            Result<CampaignMaintenanceAttempt> acknowledged = await store.RecordDisclosureAsync(prepared.Value, CovenantOperationGateFixture.Digest(9), CancellationToken.None);

            Assert.True(acknowledged.IsSuccess, acknowledged.Error.Message);

            return new CampaignMaintenancePublication(acknowledged.Value, CovenantOperationGateFixture.Digest(9));
        }

        public async ValueTask DisposeAsync()
        {
            await readLease.DisposeAsync();

            await database.DisposeAsync();
        }
    }
}
