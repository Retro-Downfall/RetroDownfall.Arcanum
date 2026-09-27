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

    [Fact]
    public async Task Claimless_inspection_derives_complete_target_and_default_generation_from_stored_row()
    {
        Guid id = await SeedAsync();

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

        await _service.UpsertAsync("Entity", "general", ["campaign"], LexiconScope.ForCampaign(Campaign));

        await ExecuteAsync("UPDATE lexicon_entries SET RetiredAtUtc = '2026-09-02T00:00:00.0000000Z' WHERE ScopeCampaignId <> ''; ");

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
