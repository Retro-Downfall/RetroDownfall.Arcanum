using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconCurationInspectionTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private static readonly LexiconCurationScope Global = new(LexiconScopeKind.Global, null);

    private static readonly Guid Campaign = Guid.Parse("A0000000-0000-4000-8000-0000000000AA");

    private static readonly DateTimeOffset At = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private const string SnapshotHash = "1D1CA295A1538D2AEE3794E497EF7766CAA7038BD6D953324A16F0D3B4F422EB";

    private string _path = string.Empty;

    private ArcanumDbContext _db = null!;

    private LexiconService _service = null!;

    public Task InitializeAsync()
    {
        _path = fixture.CopyDatabase();

        _db = fixture.CreateContext(_path);

        _service = new LexiconService(_db, NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { Annals = false } }));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        File.Delete(_path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claimless_inspection_derives_complete_target_and_default_generation_from_stored_row(bool pinned)
    {
        Guid id = await SeedAsync();

        if (pinned)
        {
            await ExecuteAsync("UPDATE lexicon_entries SET PinnedAtUtc = '2026-09-01T00:00:00.0000000Z';");
        }

        var result = await _service.ShowExactAsync(Global, "  eNtItY  ", null);

        Assert.True(result.IsSuccess, result.Error.Message);

        LexiconEntryDetail detail = result.Value.Value;

        Assert.Equal(id, detail.Target.EntryId);

        Assert.Equal("ENTITY", detail.Target.NormalizedName);

        Assert.Equal(Global, detail.Scope);

        Assert.Equal(1, detail.CurationGeneration);

        Assert.Equal(1, detail.Entry.CurationGeneration);

        Assert.Equal(SnapshotHash, detail.SnapshotDigest);

        Assert.Equal(SnapshotHash, detail.Target.SnapshotDigest);

        Assert.Equal(LexiconRetrievalEligibility.Eligible, detail.Entry.Eligibility);

        Assert.Equal(new LexiconCurationAnnalHead(false, null, null, null, null, null, null), detail.Target.AnnalHead);

        Assert.Equal(new LexiconCurationSensitivityLabel(false, null, null, null, null), detail.Target.SensitivityLabel);

        Assert.True(detail.Target.Validate().IsSuccess);

        Assert.Empty(detail.AnnalHistory);

        Assert.Empty(detail.HistoricalFactProvenance);

        Assert.False(result.Value.ContainsProtectedContent);

        Assert.Equal(pinned ? At : (DateTimeOffset?)null, detail.Entry.PinnedAtUtc);

        var listed = await _service.ListInspectionAsync(null);

        Assert.True(listed.IsSuccess, listed.Error.Message);

        LexiconEntryDto entry = Assert.Single(listed.Value.Value);

        Assert.Equal(id, entry.Id);

        Assert.Equal(LexiconRetrievalEligibility.Eligible, entry.Eligibility);

        Assert.Equal(pinned ? At : (DateTimeOffset?)null, entry.PinnedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claimless_retired_row_is_not_a_legitimate_absent_head(bool list)
    {
        await SeedAsync();

        await ExecuteAsync("UPDATE lexicon_entries SET RetiredAtUtc = '2026-09-02T00:00:00.0000000Z';");

        Error error = list
            ? (await _service.ListInspectionAsync(null)).Error
            : (await _service.ShowExactAsync(Global, "entity", null)).Error;

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, error.Code);
    }

    [Theory]
    [InlineData("1.5", false)]
    [InlineData("1.5", true)]
    [InlineData("2147483648", false)]
    [InlineData("2147483648", true)]
    [InlineData("4294967297", false)]
    [InlineData("4294967297", true)]
    public async Task Current_provenance_version_rejects_non_positive_Int32_storage(string version, bool list)
    {
        await SeedAsync(withProvenance: true);

        await ExecuteAsync("UPDATE lexicon_fact_attachment_provenance SET Version = " + version + ";");

        Error error = list
            ? (await _service.ListInspectionAsync(null)).Error
            : (await _service.ShowExactAsync(Global, "entity", null)).Error;

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, error.Code);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2147483647", int.MaxValue)]
    public async Task Current_provenance_preserves_positive_Int32_versions_in_inspection_and_operational_reads(string storedVersion, int expected)
    {
        await SeedAsync(withProvenance: true);

        await ExecuteAsync("UPDATE lexicon_fact_attachment_provenance SET Version = " + storedVersion + ";");

        var exact = await _service.ShowExactAsync(Global, "entity", null);

        Assert.True(exact.IsSuccess, exact.Error.Message);

        var listed = await _service.ListInspectionAsync(null);

        Assert.True(listed.IsSuccess, listed.Error.Message);

        var operational = await _service.GetByNameAsync("entity", LexiconScope.Global);

        Assert.True(operational.IsSuccess, operational.Error.Message);

        Assert.All(new[] { exact.Value.Value.Entry, Assert.Single(listed.Value.Value), operational.Value! },
            entry => Assert.Equal(expected, Assert.Single(entry.FactProvenance!).Source.Version));
    }

    [Theory]
    [InlineData(AnnalContentHashFormat.LegacyStoreDigest)]
    [InlineData(AnnalContentHashFormat.LexiconStructuredSnapshot)]
    public async Task Inspection_verifies_both_persisted_hash_formats_and_current_provenance(AnnalContentHashFormat format)
    {
        Guid id = await SeedAsync(withProvenance: true);

        await AppendHeadAsync(id, format);

        var result = await _service.ShowExactAsync(Global, "entity", null);

        Assert.True(result.IsSuccess, result.Error.Message);

        LexiconEntryDetail detail = result.Value.Value;

        Assert.Equal(format, detail.Target.AnnalHead.ContentHashFormat);

        Assert.Equal(AnnalOrigin.OperatorStated, detail.CurrentOrigin);

        Assert.Equal(AnnalOperation.Assert, Assert.Single(detail.AnnalHistory).Operation);

        Assert.Equal("source", Assert.Single(detail.Entry.FactProvenance!).Source.LogicalKey);

        Assert.Equal(format == AnnalContentHashFormat.LexiconStructuredSnapshot ? 1 : 0, detail.HistoricalFactProvenance.Length);

        if (format == AnnalContentHashFormat.LexiconStructuredSnapshot)
        {
            Assert.Equal(0, detail.HistoricalFactProvenance[0].FactOrdinal);

            Assert.Equal("source", detail.HistoricalFactProvenance[0].LogicalKey);
        }
    }

    [Fact]
    public async Task Retired_inspection_preserves_content_and_tombstone_but_is_ineligible()
    {
        Guid id = await SeedAsync();

        await AppendHeadAsync(id, AnnalContentHashFormat.LexiconStructuredSnapshot);

        await AnnalsClaimWriter.AppendRetirementAsync(Connection, null, AnnalSubjectStore.Lexicon,
            id.ToString("N"), AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Global, null,
            ContentSensitivity.None, At.AddDays(1), At.AddDays(1), null, CancellationToken.None);

        await ExecuteAsync("UPDATE lexicon_entries SET RetiredAtUtc = '2026-09-02T00:00:00.0000000Z', CurationGeneration = 2;");

        var result = await _service.ShowExactAsync(Global, "entity", null);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(["alpha"], result.Value.Value.Entry.Facts);

        Assert.Equal(LexiconRetrievalEligibility.Retired, result.Value.Value.Eligibility);

        Assert.Equal(At.AddDays(1), result.Value.Value.Entry.RetiredAtUtc);

        Assert.Null(result.Value.Value.Target.AnnalHead.ContentHash);

        Assert.Equal(AnnalOperation.Retire, result.Value.Value.Target.AnnalHead.Operation);

        Assert.Equal(2, result.Value.Value.AnnalHistory.Length);
    }

    [Fact]
    public async Task Labeled_inspection_requires_matching_scoped_lease_and_verifies_exact_snapshot_bytes()
    {
        Guid id = await SeedAsync();

        await AppendHeadAsync(id, AnnalContentHashFormat.LexiconStructuredSnapshot, ContentSensitivity.CovenantDerived);

        ArtifactSensitivityLabel label = await LabelAsync(id);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, (await _service.ShowExactAsync(Global, "entity", null)).Error.Code);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, (await _service.ShowExactAsync(Global, "entity", new Lease(Campaign))).Error.Code);

        Lease lease = new();

        var result = await _service.ShowExactAsync(Global, "entity", lease);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.ContainsProtectedContent);

        Assert.Equal(label.LabelId, result.Value.Value.Target.SensitivityLabel.LabelId);

        Assert.Equal(label.ArtifactContentDigest.ToString(), result.Value.Value.Target.SensitivityLabel.ArtifactContentDigest);

        Assert.Equal(label.Provenance, result.Value.Value.Target.SensitivityLabel.GenerationProvenance);

        Assert.Equal(2, lease.Revalidations);
    }

    [Theory]
    [InlineData("ArtifactContentDigest = zeroblob(32)")]
    [InlineData("ArtifactRevision = 0")]
    [InlineData("ArtifactRevision = 1.5")]
    [InlineData("CampaignId = 'A0000000-0000-4000-8000-0000000000AA'")]
    [InlineData("ArtifactKindCode = 1")]
    [InlineData("ArtifactId = 'A0000000-0000-4000-8000-0000000000AA'")]
    [InlineData("SensitivityCode = 0")]
    [InlineData("ProvenanceModeCode = 9")]
    [InlineData("ExactGenerationIds = zeroblob(16)")]
    [InlineData("GenerationBloom = zeroblob(32)")]
    [InlineData("SensitivityDigest = zeroblob(32)")]
    [InlineData("ArtifactLabelDigest = zeroblob(32)")]
    public async Task Corrupt_label_is_integrity_failure_not_an_unlabeled_target(string change)
    {
        Guid id = await SeedAsync();

        await AppendHeadAsync(id, AnnalContentHashFormat.LexiconStructuredSnapshot, ContentSensitivity.CovenantDerived);

        await LabelAsync(id);

        await DisableGuardsAsync("artifact_sensitivity");

        await ExecuteAsync("UPDATE artifact_sensitivity SET " + change + ";");

        var result = await _service.ShowExactAsync(Global, "entity", new Lease());

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, result.Error.Code);

        var listed = await _service.ListInspectionAsync(new Lease(installation: true));

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, listed.Error.Code);
    }

    [Fact]
    public async Task Self_consistent_label_for_different_snapshot_bytes_fails_integrity()
    {
        Guid id = await SeedAsync();

        await AppendHeadAsync(id, AnnalContentHashFormat.LexiconStructuredSnapshot, ContentSensitivity.CovenantDerived);

        await LabelAsync(id, differentContent: true);

        var result = await _service.ShowExactAsync(Global, "entity", new Lease());

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, result.Error.Code);
    }

    [Fact]
    public async Task Hard_delete_keeps_the_label_guard_for_retired_rows()
    {
        Guid id = await SeedAsync();

        await LabelAsync(id);

        await ExecuteAsync("UPDATE lexicon_entries SET RetiredAtUtc = '2026-09-02T00:00:00.0000000Z';");

        LexiconService guarded = new(_db, NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()), FixtureLabeledArtifactGuard.For(_db));

        var deleted = await guarded.DeleteByNameAsync("Entity", LexiconScope.Global);

        Assert.True(deleted.IsFailure);

        await using DbCommand read = Connection.CreateCommand();

        read.CommandText = "SELECT COUNT(*) FROM lexicon_entries";

        Assert.Equal(1L, await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Concurrent_publication_cannot_mix_the_row_with_an_earlier_head()
    {
        Guid id = await SeedAsync();

        await AppendHeadAsync(id, AnnalContentHashFormat.LexiconStructuredSnapshot);

        await ExecuteAsync("PRAGMA journal_mode = WAL;");

        await using ArcanumDbContext writer = fixture.CreateContext(_path);

        await writer.Database.OpenConnectionAsync();

        bool published = false;

        raw.sqlite3_trace(Connection.Handle, (object _, string sql) =>
        {
            if (!published && sql.Contains("NameNormalized, FactsText FROM lexicon_entries", StringComparison.Ordinal))
            {
                published = true;

                using DbCommand update = writer.Database.GetDbConnection().CreateCommand();

                update.CommandText = "UPDATE lexicon_entries SET Type = 'concurrently replaced';";

                update.ExecuteNonQuery();
            }
        }, null);

        var result = await _service.ShowExactAsync(Global, "entity", null);

        Assert.True(published);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("general", result.Value.Value.Entry.Type);

        Assert.Equal(SnapshotHash, result.Value.Value.Target.SnapshotDigest);

        raw.sqlite3_trace(Connection.Handle, (strdelegate_trace)null!, null);

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, (await _service.ShowExactAsync(Global, "entity", null)).Error.Code);
    }

    [Theory]
    [InlineData("annal_versions", "OperationCode = 9")]
    [InlineData("annal_versions", "ContentHashFormatCode = 9")]
    [InlineData("annal_versions", "ContentHash = zeroblob(32)")]
    [InlineData("annal_versions", "ScopeKindCode = 0")]
    [InlineData("annal_versions", "CampaignId = 'A0000000-0000-4000-8000-0000000000AA'")]
    [InlineData("annal_versions", "SensitivityCode = 1")]
    [InlineData("annal_versions", "Revision = 1.5")]
    [InlineData("annal_heads", "CurrentOperationCode = 3")]
    [InlineData("annal_heads", "CurrentRevision = 2")]
    [InlineData("annal_heads", "CurrentVersionId = 'missing'")]
    [InlineData("lexicon_entries", "NameNormalized = 'ENTITY', Name = 'Different'")]
    [InlineData("lexicon_entries", "FactsText = 'different'")]
    [InlineData("lexicon_entries", "CurationGeneration = 1.5")]
    public async Task Contradictory_head_or_canonical_row_fails_closed(string table, string change)
    {
        Guid id = await SeedAsync();

        await AppendHeadAsync(id, AnnalContentHashFormat.LexiconStructuredSnapshot);

        await DisableGuardsAsync(table);

        await ExecuteAsync("UPDATE " + table + " SET " + change + ";");

        var result = await _service.ShowExactAsync(Global, "entity", null);

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, result.Error.Code);
    }

    [Fact]
    public async Task Claim_without_head_is_not_the_legitimate_no_claim_arm()
    {
        Guid id = await SeedAsync();

        await AppendHeadAsync(id, AnnalContentHashFormat.LexiconStructuredSnapshot);

        await DisableGuardsAsync("annal_heads");

        await ExecuteAsync("DELETE FROM annal_heads;");

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, (await _service.ShowExactAsync(Global, "entity", null)).Error.Code);
    }

    [Fact]
    public async Task Effective_resolution_and_lightweight_list_use_one_deferred_snapshot_each()
    {
        Guid global = await SeedAsync();

        await AppendHeadAsync(global, AnnalContentHashFormat.LexiconStructuredSnapshot, ContentSensitivity.CovenantDerived);

        await LabelAsync(global);

        var campaign = await _service.UpsertAsync("Entity", "general", ["campaign"], LexiconScope.ForCampaign(Campaign));

        Assert.True(campaign.IsSuccess, campaign.Error.Message);

        await using (SqliteTransaction transaction = Connection.BeginTransaction())
        {
            string? baseline = await AnnalsClaimWriter.AppendAssertAsync(Connection, transaction, AnnalSubjectStore.Lexicon,
                campaign.Value.Id.ToString("N"), AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Campaign, Campaign.ToString("D"),
                ContentSensitivity.None, AnnalContentHashFormat.LexiconStructuredSnapshot,
                LexiconSnapshotDigest.Compute(LexiconValueNormalizer.NormalizeCorrection("Entity", "general", ["campaign"]).Value),
                At, At, null, CancellationToken.None);

            Assert.NotNull(baseline);

            Assert.True(await AnnalsClaimWriter.AppendRetirementAsync(Connection, transaction, AnnalSubjectStore.Lexicon,
                campaign.Value.Id.ToString("N"), AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Campaign, Campaign.ToString("D"),
                ContentSensitivity.None, At.AddDays(1), At.AddDays(1), null, CancellationToken.None));

            await transaction.CommitAsync();
        }

        await ExecuteAsync("UPDATE lexicon_entries SET RetiredAtUtc = '2026-09-02T00:00:00.0000000Z', CurationGeneration = 2 WHERE ScopeCampaignId <> ''; ");

        List<string> statements = CaptureStatements();

        var effective = await _service.ShowEffectiveAsync(new(LexiconScopeKind.Campaign, Campaign), "entity", new Lease(installation: true));

        Assert.True(effective.IsSuccess, effective.Error.Message);

        Assert.Equal(global, effective.Value.Value.Entry.Id);

        Assert.Equal(Global, effective.Value.Value.Target.Scope);

        Assert.True(effective.Value.ContainsProtectedContent);

        AssertOneSnapshot(statements);

        statements.Clear();

        var listed = await _service.ListInspectionAsync(new Lease(installation: true));

        Assert.True(listed.IsSuccess, listed.Error.Message);

        Assert.True(listed.Value.ContainsProtectedContent);

        Assert.Equal(2, listed.Value.Value.Count);

        Assert.Contains(listed.Value.Value, entry => entry.Eligibility == LexiconRetrievalEligibility.Retired);

        AssertOneSnapshot(statements);

        Assert.DoesNotContain(statements, sql => sql.Contains("RecordedUntilUtc", StringComparison.Ordinal)
            || sql.Contains("lexicon_annal_fact_provenance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lease_revoked_during_read_refuses_the_complete_projection()
    {
        await SeedAsync();

        Lease lease = new() { StaleAfter = 1 };

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, (await _service.ShowExactAsync(Global, "entity", lease)).Error.Code);
    }

    [Fact]
    public async Task Read_only_implementations_inherit_fail_closed_mutation_defaults()
    {
        ILexiconCurationService service = new FakeLexiconService();

        Result<LexiconCurationResult>[] results =
        [
            await service.CorrectAsync(null!, null!, null),
            await service.RetireAsync(null!, null),
            await service.ReinstateAsync(null!, null),
            await service.PinAsync(null!, null),
            await service.UnpinAsync(null!, null),
        ];

        Assert.All(results, result => Assert.Equal(new Error(ErrorCodes.Lexicon.WriteFailed,
            "Lexicon curation mutation is unavailable."), result.Error));
    }

    private SqliteConnection Connection => (SqliteConnection)_db.Database.GetDbConnection();

    [Theory]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    [InlineData(null, null)]
    public async Task Ordinal_predicate_is_total_for_SQL_nulls(string? value, string? query)
    {
        await SeedAsync();

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = "SELECT arcanum_ordinal_contains($value, $query)";

        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);

        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("[123]")]
    [InlineData("[null]")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task Search_never_treats_malformed_fact_values_as_a_nonmatch(string factsJson)
    {
        await SeedAsync();

        await DisableGuardsAsync("lexicon_entries");

        await ExecuteAsync("UPDATE lexicon_entries SET FactsJson = '" + factsJson + "'");

        var result = await _service.SearchInspectionAsync("absent", 1, null);

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, result.Error.Code);
    }

    [Fact]
    public async Task A_non_Lexicon_label_may_legitimately_reuse_the_same_artifact_guid()
    {
        Guid id = await SeedAsync();

        ArtifactSensitivityLabel original = await LabelAsync(id);

        ArtifactSensitivityLabel saga = new(original.LabelId, SensitiveArtifactKind.Saga, id,
            null, null, null, 1, original.ArtifactContentDigest, original.Sensitivity,
            original.Provenance, null, null, null, At);

        await DisableGuardsAsync("artifact_sensitivity");

        await ExecuteAsync($"UPDATE artifact_sensitivity SET ArtifactKindCode = 6, ArtifactLabelDigest = X'{Convert.ToHexString(saga.LabelDigest.Bytes)}'");

        var show = await _service.ShowExactAsync(Global, "entity", null);

        var list = await _service.ListInspectionAsync(null);

        var counts = await _service.CountInspectionAsync(null);

        Assert.True(show.IsSuccess, show.Error.Message);

        Assert.True(list.IsSuccess, list.Error.Message);

        Assert.True(counts.IsSuccess, counts.Error.Message);

        Assert.Equal(id, show.Value.Value.Entry.Id);

        Assert.Equal(id, Assert.Single(list.Value.Value).Id);

        Assert.Equal(new LexiconInspectionCounts(1, 1), counts.Value.Value);

        Assert.False(counts.Value.ContainsProtectedContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_inspection_preserves_provenance_insertion_order_not_fact_index_order(bool search)
    {
        AttachmentMemoryProvenance source = new(Guid.NewGuid(), Guid.NewGuid(), "source", 1,
            "hash", At, "WorkspaceFile", AttachmentSourceAvailability.Available);

        Assert.True((await _service.UpsertAsync("Entity", "general", ["z-last", "a-first"], source, LexiconScope.Global)).IsSuccess);

        var result = search ? await _service.SearchInspectionAsync("Entity", 1, null)
            : await _service.ListInspectionAsync(null);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(new[] { "z-last", "a-first" }, Assert.Single(result.Value.Value).FactProvenance!.Select(item => item.Fact));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(int.MinValue, -1)]
    public async Task Search_limit_is_explicit_and_negative_values_never_become_unbounded(int? limit, int expected)
    {
        await SeedAsync();

        List<string> statements = CaptureStatements();

        var result = await _service.SearchInspectionAsync("alpha", limit, null);

        if (expected < 0)
        {
            Assert.Equal(ErrorCodes.Validation.InvalidQuery, result.Error.Code);

            Assert.Empty(statements);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(expected, result.Value.Value.Count);
        }
    }

    [Theory]
    [InlineData("PinnedAtUtc = 'bad-time'")]
    [InlineData("CurationGeneration = 0")]
    public async Task Count_projection_refuses_malformed_lifecycle_metadata_without_needing_content(string assignment)
    {
        await SeedAsync();

        await DisableGuardsAsync("lexicon_entries");

        await ExecuteAsync("UPDATE lexicon_entries SET " + assignment);

        var result = await _service.CountInspectionAsync(null);

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, result.Error.Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Counts_materialize_only_protected_content_and_preserve_digest_and_authority_checks(bool authorized, bool corrupt)
    {
        Guid id = await SeedAsync();

        await LabelAsync(id, differentContent: corrupt);

        Assert.True((await _service.UpsertAsync("Ordinary", "general", ["ordinary"], LexiconScope.Global)).IsSuccess);

        HashSet<string> canonical = [];

        Connection.CreateFunction<string, string, string>("observe_count_content", (value, identity) =>
        {
            canonical.Add(identity);

            return value;
        });

        await ExecuteAsync("""
            CREATE TEMP VIEW lexicon_entries AS
            SELECT Id, Name, NameNormalized, Type, FactsJson,
                   observe_count_content(FactsText, Id) AS FactsText, UpdatedAt,
                   ScopeCampaignId, RetiredAtUtc, PinnedAtUtc, CurationGeneration
            FROM main.lexicon_entries;
            """);

        List<string> statements = CaptureStatements();

        var result = await _service.CountInspectionAsync(authorized ? new Lease(installation: true) : null);

        Assert.Equal(authorized ? 1 : 0, canonical.Count);

        Assert.True(statements.Count <= 8, $"Executed {statements.Count} statements.");

        if (!authorized || corrupt)
        {
            Assert.Equal(!authorized ? ErrorCodes.Covenant.ForbiddenAuthority : ErrorCodes.Lexicon.CurationIntegrityFailed, result.Error.Code);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal(new LexiconInspectionCounts(2, 2), result.Value.Value);

            Assert.True(result.Value.ContainsProtectedContent);
        }
    }

    [Theory]
    [InlineData("éCLAIR", "Éclair")]
    [InlineData("雪", "雪")]
    [InlineData("%_", "%_")]
    [InlineData("quote\"", "quote\"")]
    [InlineData("slash\\", "slash\\")]
    [InlineData("line\nfeed", "line\nfeed")]
    public async Task Query_projection_matches_deserialized_facts_with_ordinal_unicode_semantics(string query, string fact)
    {
        Assert.True((await _service.UpsertAsync("First", "general", [fact], LexiconScope.Global)).IsSuccess);

        Assert.True((await _service.UpsertAsync("Second", "general", ["line", "feed", "not a match"], LexiconScope.Global)).IsSuccess);

        var result = await _service.SearchInspectionAsync(query, 1, null);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("First", Assert.Single(result.Value.Value).Name);
    }

    [Theory]
    [InlineData("kind", false)]
    [InlineData("kind", true)]
    [InlineData("identity", false)]
    [InlineData("identity", true)]
    [InlineData("missing-label", false)]
    [InlineData("missing-label", true)]
    public async Task Contradictory_protected_identity_is_refused_before_any_canonical_materialization(string corruption, bool list)
    {
        Guid id = await SeedAsync();

        await LabelAsync(id);

        await AppendHeadAsync(id, AnnalContentHashFormat.LexiconStructuredSnapshot, ContentSensitivity.CovenantDerived);

        await DisableGuardsAsync("artifact_sensitivity");

        await ExecuteAsync(corruption switch
        {
            "kind" => "UPDATE artifact_sensitivity SET ArtifactKindCode = 6",
            "identity" => "UPDATE artifact_sensitivity SET ArtifactId = 'BBBBBBBB-1111-4111-8111-BBBBBBBBBBBB'",
            _ => "DELETE FROM artifact_sensitivity",
        });

        int canonicalReads = 0;

        Connection.CreateFunction<string, string>("observe_canonical", value =>
        {
            canonicalReads++;

            return value;
        });

        await ExecuteAsync("""
            CREATE TEMP VIEW lexicon_entries AS
            SELECT Id, Name, NameNormalized, Type, observe_canonical(FactsJson) AS FactsJson,
                   FactsText, UpdatedAt, ScopeCampaignId, RetiredAtUtc, PinnedAtUtc, CurationGeneration
            FROM main.lexicon_entries;
            """);

        Error error = list ? (await _service.ListInspectionAsync(null)).Error
            : (await _service.ShowExactAsync(Global, "entity", null)).Error;

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, error.Code);

        Assert.Equal(0, canonicalReads);
    }

    private async Task<Guid> SeedAsync(bool withProvenance = false)
    {
        AttachmentMemoryProvenance provenance = new(Guid.NewGuid(), Guid.NewGuid(), "source", 1,
            "attachment-hash", At, "WorkspaceFile", AttachmentSourceAvailability.Available);

        var result = withProvenance
            ? await _service.UpsertAsync("Entity", "general", ["alpha"], provenance, LexiconScope.Global)
            : await _service.UpsertAsync("Entity", "general", ["alpha"], LexiconScope.Global);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, result.Value.CurationGeneration);

        return result.Value.Id;
    }

    private async Task AppendHeadAsync(Guid id, AnnalContentHashFormat format, ContentSensitivity sensitivity = ContentSensitivity.None)
    {
        await using SqliteTransaction transaction = Connection.BeginTransaction();

        await AnnalsClaimWriter.AppendCorrectionAsync(Connection, transaction, AnnalSubjectStore.Lexicon,
            id.ToString("N"), AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Global, null,
            sensitivity, format,
            format == AnnalContentHashFormat.LegacyStoreDigest
                ? AnnalContentDigest.ForLexiconEntry("general", "alpha")
                : Convert.FromHexString(SnapshotHash),
            At, At, null, CancellationToken.None);

        await transaction.CommitAsync();
    }

    private async Task<ArtifactSensitivityLabel> LabelAsync(Guid id, bool differentContent = false)
    {
        byte[] snapshotBytes = [.. "Arcanum.Lexicon.Snapshot.v2\0"u8, 2, 0, 0, 0, 7, .. "general"u8, 0, 0, 0, 1, 0, 0, 0, 5, .. "alpha"u8];

        ArtifactSensitivityLabel label = new(Guid.NewGuid(), SensitiveArtifactKind.Lexicon, id,
            null, null, null, 1, DerivedArtifactContentDigest.ForBytes(differentContent ? "different"u8.ToArray() : snapshotBytes),
            ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()]), null, null, null, At);

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity(LabelId, ArtifactKindCode, ArtifactId, SensitivityCode,
                ProvenanceModeCode, ExactGenerationIds, ArtifactRevision, ArtifactContentDigest,
                SensitivityDigest, ArtifactLabelDigest, CreatedAtUtc)
            VALUES ($label, $kind, $id, 1, 1, $generations, 1, $content, $sensitivity, $digest, $at);
            """;

        command.Parameters.AddWithValue("$label", label.LabelId.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$kind", (int)SensitiveArtifactKind.Lexicon);

        command.Parameters.AddWithValue("$id", id.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$generations", label.Provenance.ToCanonicalExactBytes());

        command.Parameters.AddWithValue("$content", label.ArtifactContentDigest.Bytes.ToArray());

        command.Parameters.AddWithValue("$sensitivity", label.SensitivityDigest.Bytes.ToArray());

        command.Parameters.AddWithValue("$digest", label.LabelDigest.Bytes.ToArray());

        command.Parameters.AddWithValue("$at", "2026-09-01T00:00:00.0000000Z");

        await command.ExecuteNonQueryAsync();

        return label;
    }

    private async Task DisableGuardsAsync(string table)
    {
        List<string> names = [];

        await using (SqliteCommand read = Connection.CreateCommand())
        {
            read.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'trigger' AND tbl_name = $table;";

            read.Parameters.AddWithValue("$table", table);

            await using var reader = await read.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (string name in names)
        {
            await ExecuteAsync("DROP TRIGGER \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";");
        }

        await ExecuteAsync("PRAGMA foreign_keys = OFF; PRAGMA ignore_check_constraints = ON;");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    private List<string> CaptureStatements()
    {
        List<string> statements = [];

        raw.sqlite3_trace(Connection.Handle, (object _, string sql) => statements.Add(sql), null);

        return statements;
    }

    private static void AssertOneSnapshot(List<string> statements)
    {
        Assert.Single(statements, sql => sql.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(statements, sql => sql.StartsWith("BEGIN DEFERRED", StringComparison.OrdinalIgnoreCase));

        Assert.Single(statements, sql => sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Lease(Guid? campaign = null, bool installation = false) : ICovenantSnapshotReadLease
    {
        public int Revalidations { get; private set; }

        public int StaleAfter { get; init; } = int.MaxValue;

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(Guid.NewGuid(), 1,
            installation ? CovenantLeaseKind.InstallationRead : CovenantLeaseKind.Read,
            installation ? CovenantLeaseCoverage.Installation : CovenantLeaseCoverage.Scoped,
            installation ? null : campaign is { } id ? CovenantOperationScope.ForCampaign(id) : CovenantOperationScope.Global,
            null, 1, 1, 0, null, null, null, null, null, false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Revalidations++ >= StaleAfter
                ? Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "Lease revoked."))
                : Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
