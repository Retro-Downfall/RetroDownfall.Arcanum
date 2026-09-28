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
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconCorrectionTests(GrimoireFixture fixture) : IAsyncLifetime
{
    internal static readonly LexiconCurationScope Global = new(LexiconScopeKind.Global, null);

    private CorrectionFixture _test = null!;

    public Task InitializeAsync()
    {
        _test = new CorrectionFixture(fixture);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => _test.DisposeAsync();

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    [Fact]
    public async Task Correction_replaces_type_and_complete_ordered_facts_and_republishes_search()
    {
        LexiconEntryDetail before = await _test.SeedAsync();

        var result = await _test.Service.CorrectAsync(before.Target, new(" Person ", [" gamma ", "beta", "gamma"]), null);

        Assert.True(result.IsSuccess, result.Error.Message);

        LexiconEntryDetail after = result.Value.Entry;

        Assert.Equal(LexiconCurationOutcomeKind.Applied, result.Value.Outcome);

        Assert.Equal(before.Entry.Id, after.Entry.Id);

        Assert.Equal("Person", after.Entry.Type);

        Assert.Equal(["gamma", "beta"], after.Entry.Facts);

        Assert.Equal(2, after.CurationGeneration);

        Assert.True(after.Entry.UpdatedAt > before.Entry.UpdatedAt);

        Assert.Equal("beta", Assert.Single(after.Entry.FactProvenance!).Fact);

        Assert.Equal(before.Entry.FactProvenance![1].Source, after.Entry.FactProvenance![0].Source);

        Assert.Equal(0L, await _test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'alpha'"));

        Assert.Equal(1L, await _test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'gamma'"));
    }

    [Fact]
    public async Task Canonical_equal_replacement_has_no_durable_writes()
    {
        LexiconEntryDetail before = await _test.SeedAsync();

        string[] snapshot = await _test.SnapshotAsync();

        List<string> statements = [];

        raw.sqlite3_trace(_test.Connection.Handle, (object _, string sql) => statements.Add(sql), null);

        var result = await _test.Service.CorrectAsync(before.Target, new(" general ", [" alpha ", "beta", "alpha"]), null);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(LexiconCurationOutcomeKind.Unchanged, result.Value.Outcome);

        Assert.Equal(before.Entry.UpdatedAt, result.Value.Entry.Entry.UpdatedAt);

        Assert.Equal(1, result.Value.Entry.CurationGeneration);

        Assert.Equal(snapshot, await _test.SnapshotAsync());

        Assert.DoesNotContain(statements, sql => sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("name")]
    [InlineData("entry")]
    [InlineData("generation")]
    [InlineData("digest")]
    [InlineData("retired")]
    [InlineData("pinned")]
    [InlineData("head")]
    [InlineData("label")]
    public async Task Every_target_component_is_compared_before_no_op(string field)
    {
        LexiconEntryDetail before = await _test.SeedAsync();

        LexiconCurationTarget target = before.Target;

        target = field switch
        {
            "scope" => target with { Scope = new(LexiconScopeKind.Campaign, Guid.NewGuid()) },
            "name" => target with { NormalizedName = "OTHER" },
            "entry" => target with { EntryId = Guid.NewGuid() },
            "generation" => target with { CurationGeneration = 2 },
            "digest" => target with { SnapshotDigest = new string('A', 64) },
            "retired" => target with { Lifecycle = target.Lifecycle with { RetiredAtUtc = DateTimeOffset.UtcNow } },
            "pinned" => target with { Lifecycle = target.Lifecycle with { PinnedAtUtc = DateTimeOffset.UtcNow } },
            "head" => target with { AnnalHead = new(true, "claim", "version", 1, AnnalOperation.Assert, AnnalContentHashFormat.LexiconStructuredSnapshot, new string('A', 64)) },
            _ => target with { SensitivityLabel = new(true, Guid.NewGuid(), 1, new string('A', 64), GenerationProvenance.CreateExact([Guid.NewGuid()])) },
        };

        string[] snapshot = await _test.SnapshotAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        var result = await _test.Service.CorrectAsync(target, new("general", ["alpha", "beta"]), field == "label" ? lease : null);

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, result.Error.Code);

        Assert.Equal(snapshot, await _test.SnapshotAsync());
    }

    [Fact]
    public async Task Exhausted_generation_is_refused_even_for_equal_content()
    {
        await _test.SeedAsync();

        await _test.ExecuteAsync("UPDATE lexicon_entries SET CurationGeneration = 9223372036854775807");

        LexiconEntryDetail before = await _test.ShowAsync();

        var result = await _test.Service.CorrectAsync(before.Target, new("general", ["alpha", "beta"]), null);

        Assert.Equal(ErrorCodes.Lexicon.CurationGenerationExhausted, result.Error.Code);
    }

    [Fact]
    public async Task Invalid_replacement_is_rejected_before_opening_a_transaction()
    {
        LexiconEntryDetail before = await _test.SeedAsync();

        List<string> statements = [];

        raw.sqlite3_trace(_test.Connection.Handle, (object _, string sql) => statements.Add(sql), null);

        var result = await _test.Service.CorrectAsync(before.Target, new("general", []), null);

        Assert.Equal(ErrorCodes.Lexicon.InvalidReplacement, result.Error.Code);

        Assert.DoesNotContain(statements, sql => sql.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Retired_content_is_refused_after_the_exact_target_is_checked()
    {
        LexiconEntryDetail before = await _test.SeedAsync();

        await using (SqliteTransaction transaction = _test.Connection.BeginTransaction())
        {
            await AnnalsClaimWriter.AppendCorrectionAsync(_test.Connection, transaction, AnnalSubjectStore.Lexicon,
                before.Entry.Id.ToString("N"), AnnalOrigin.AgentAsserted, SagaMemoryScopeKind.Global, null,
                ContentSensitivity.None, AnnalContentHashFormat.LexiconStructuredSnapshot, Convert.FromHexString(before.SnapshotDigest),
                before.Entry.UpdatedAt, before.Entry.UpdatedAt, null, CancellationToken.None);

            await AnnalsClaimWriter.AppendRetirementAsync(_test.Connection, transaction, AnnalSubjectStore.Lexicon,
                before.Entry.Id.ToString("N"), AnnalOrigin.OperatorStated, SagaMemoryScopeKind.Global, null,
                ContentSensitivity.None, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, CancellationToken.None);

            await transaction.CommitAsync();
        }

        await _test.ExecuteAsync("UPDATE lexicon_entries SET RetiredAtUtc = '2026-09-27T00:00:00.0000000Z'");

        LexiconEntryDetail retired = await _test.ShowAsync();

        var result = await _test.Service.CorrectAsync(retired.Target, new("general", ["alpha", "beta"]), null);

        Assert.Equal(ErrorCodes.Lexicon.RetiredMutationRefused, result.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protected_replacement_preserves_ownership_evidence_and_taint_count(bool maintenance)
    {
        await _test.SeedAsync();

        ArtifactSensitivityLabel label = await _test.ProtectAsync(maintenance: maintenance);

        LexiconEntryDetail before = await _test.ShowProtectedAsync();

        SessionSensitivityProjection projection = (await ArtifactSensitivityLedger.ReadProjectionWithinAsync(
            _test.Connection, null, label.SessionId!.Value, CancellationToken.None)).Value;

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        var result = await _test.Service.CorrectAsync(before.Target, new("Person", ["beta", "gamma"]), lease);

        Assert.True(result.IsSuccess, result.Error.Message);

        ArtifactSensitivityLabel after = (await ArtifactSensitivityLedger.ReadLabelWithinAsync(
            _test.Connection, null, SensitiveArtifactKind.Lexicon, label.ArtifactId, CancellationToken.None)).Value!;

        Assert.NotEqual(label.LabelId, after.LabelId);

        Assert.Equal(label.ArtifactId, after.ArtifactId);

        Assert.Equal(2UL, after.ArtifactRevision);

        Assert.Equal(label.Sensitivity, after.Sensitivity);

        Assert.Equal(label.Provenance, after.Provenance);

        Assert.Equal(label.SessionId, after.SessionId);

        Assert.Equal(label.CampaignId, after.CampaignId);

        Assert.Equal(label.TurnId, after.TurnId);

        Assert.Equal(label.ProducingPlanDigest, after.ProducingPlanDigest);

        Assert.Equal(label.ProducingAdmissionDigest, after.ProducingAdmissionDigest);

        Assert.Equal(label.ProducingMaintenanceReceiptDigest, after.ProducingMaintenanceReceiptDigest);

        Assert.NotEqual(label.ArtifactContentDigest, after.ArtifactContentDigest);

        Assert.Equal(result.Value.Entry.Target.SensitivityLabel.ArtifactContentDigest, Convert.ToHexString(after.ArtifactContentDigest.Bytes));

        SessionSensitivityProjection updated = (await ArtifactSensitivityLedger.ReadProjectionWithinAsync(
            _test.Connection, null, label.SessionId.Value, CancellationToken.None)).Value;

        Assert.Equal(1, projection.TaintedArtifactCount);

        Assert.Equal(1, updated.TaintedArtifactCount);

        Assert.Equal(projection.Revision + 1, updated.Revision);

        Assert.Equal(projection.MaximumSensitivity, updated.MaximumSensitivity);

        Assert.Equal(projection.GenerationProvenanceDigest, updated.GenerationProvenanceDigest);

        Assert.Equal(2, registration.Revalidations);

        string[] snapshot = await _test.SnapshotAsync();

        var noOp = await _test.Service.CorrectAsync(result.Value.Entry.Target, new("Person", ["beta", "gamma"]), lease);

        Assert.Equal(LexiconCurationOutcomeKind.Unchanged, noOp.Value.Outcome);

        Assert.Equal(snapshot, await _test.SnapshotAsync());
    }

    [Fact]
    public async Task Protected_revision_overflow_is_a_typed_refusal()
    {
        await _test.SeedAsync();

        await _test.ProtectAsync((ulong)long.MaxValue);

        LexiconEntryDetail before = await _test.ShowProtectedAsync();

        string[] snapshot = await _test.SnapshotAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        var result = await _test.Service.CorrectAsync(before.Target, new("Person", ["beta"]), lease);

        Assert.Equal(ErrorCodes.Lexicon.ArtifactRevisionExhausted, result.Error.Code);

        Assert.Equal(snapshot, await _test.SnapshotAsync());
    }

    [Theory]
    [InlineData("TaintedArtifactCount = 1.5", "TaintedArtifactCount", "real", false)]
    [InlineData("TaintedArtifactCount = 'bad'", "TaintedArtifactCount", "text", false)]
    [InlineData("Revision = 0.5", "Revision", "real", false)]
    [InlineData("Revision = 'bad'", "Revision", "text", false)]
    [InlineData("TaintedArtifactCount = 0", "TaintedArtifactCount", "integer", false)]
    [InlineData("TaintedArtifactCount = -1", "TaintedArtifactCount", "integer", true)]
    [InlineData("Revision = -1", "Revision", "integer", true)]
    [InlineData("Revision = 9223372036854775807", "Revision", "integer", false)]
    [InlineData(null, null, null, false)]
    public async Task Invalid_session_projection_refuses_correction_and_rolls_back_all_surfaces(
        string? assignment, string? column, string? storageClass, bool ignoreChecks)
    {
        await _test.SeedAsync();

        await _test.ProtectAsync();

        LexiconEntryDetail before = await _test.ShowProtectedAsync();

        if (ignoreChecks)
        {
            // Negative counters violate the schema; deliberately seed hostile persisted state.
            // Fractional and TEXT cases above exercise the real CHECK constraints unchanged.
            await _test.ExecuteAsync("PRAGMA ignore_check_constraints = ON");
        }

        await _test.ExecuteAsync(assignment is null
            ? "DELETE FROM session_sensitivity_state"
            : $"UPDATE session_sensitivity_state SET {assignment}");

        if (column is not null)
        {
            Assert.Equal(storageClass, await _test.ScalarAsync($"SELECT typeof({column}) FROM session_sensitivity_state"));
        }

        string[] snapshot = await _test.SnapshotAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        var result = await _test.Service.CorrectAsync(before.Target, new("Person", ["beta", "gamma"]), lease);

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, result.Error.Code);

        Assert.Equal(snapshot, await _test.SnapshotAsync());

        Assert.Equal(1L, await _test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'alpha'"));

        Assert.Equal(0L, await _test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'gamma'"));

        if (column is not null)
        {
            Assert.Equal(storageClass, await _test.ScalarAsync($"SELECT typeof({column}) FROM session_sensitivity_state"));
        }
    }

    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(9223372036854775806L, 9223372036854775807L)]
    public async Task Supported_integer_session_revision_advances_once_without_changing_count(long priorRevision, long nextRevision)
    {
        await _test.SeedAsync();

        ArtifactSensitivityLabel label = await _test.ProtectAsync();

        LexiconEntryDetail before = await _test.ShowProtectedAsync();

        await _test.ExecuteAsync($"UPDATE session_sensitivity_state SET Revision = {priorRevision}");

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        var result = await _test.Service.CorrectAsync(before.Target, new("Person", ["beta", "gamma"]), lease);

        Assert.True(result.IsSuccess, result.Error.Message);

        SessionSensitivityProjection projection = (await ArtifactSensitivityLedger.ReadProjectionWithinAsync(
            _test.Connection, null, label.SessionId!.Value, CancellationToken.None)).Value;

        Assert.Equal(1, projection.TaintedArtifactCount);

        Assert.Equal(nextRevision, projection.Revision);

        Assert.Equal("integer", await _test.ScalarAsync("SELECT typeof(TaintedArtifactCount) FROM session_sensitivity_state"));

        Assert.Equal("integer", await _test.ScalarAsync("SELECT typeof(Revision) FROM session_sensitivity_state"));
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("operation")]
    [InlineData("format")]
    [InlineData("hash")]
    [InlineData("headAbsent")]
    [InlineData("labelId")]
    [InlineData("artifactRevision")]
    [InlineData("artifactDigest")]
    [InlineData("generations")]
    [InlineData("labelAbsent")]
    public async Task Every_present_head_and_label_field_is_compared_before_no_op(string field)
    {
        await _test.SeedAsync();

        await _test.ProtectAsync(withHead: true);

        LexiconEntryDetail before = await _test.ShowProtectedAsync();

        LexiconCurationTarget target = before.Target;

        target = field switch
        {
            "claim" => target with { AnnalHead = target.AnnalHead with { ClaimId = "other" } },
            "version" => target with { AnnalHead = target.AnnalHead with { VersionId = "other" } },
            "revision" => target with { AnnalHead = target.AnnalHead with { Revision = 2 } },
            "operation" => target with { AnnalHead = target.AnnalHead with { Operation = AnnalOperation.Correct } },
            "format" => target with { AnnalHead = target.AnnalHead with { ContentHashFormat = AnnalContentHashFormat.LegacyStoreDigest } },
            "hash" => target with { AnnalHead = target.AnnalHead with { ContentHash = new string('B', 64) } },
            "headAbsent" => target with { AnnalHead = new(false, null, null, null, null, null, null) },
            "labelId" => target with { SensitivityLabel = target.SensitivityLabel with { LabelId = Guid.NewGuid() } },
            "artifactRevision" => target with { SensitivityLabel = target.SensitivityLabel with { ArtifactRevision = 2 } },
            "artifactDigest" => target with { SensitivityLabel = target.SensitivityLabel with { ArtifactContentDigest = new string('B', 64) } },
            "generations" => target with { SensitivityLabel = target.SensitivityLabel with { GenerationProvenance = GenerationProvenance.CreateExact([Guid.NewGuid()]) } },
            _ => target with { SensitivityLabel = new(false, null, null, null, null) },
        };

        string[] snapshot = await _test.SnapshotAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        var result = await _test.Service.CorrectAsync(target, new("general", ["alpha", "beta"]), lease);

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, result.Error.Code);

        Assert.Equal(snapshot, await _test.SnapshotAsync());
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("deleteLabel")]
    [InlineData("insertLabel")]
    [InlineData("provenance")]
    [InlineData("fts")]
    [InlineData("annals")]
    public async Task Failure_at_each_publication_point_rolls_back_every_durable_surface(string point)
    {
        await _test.SeedAsync();

        await _test.ProtectAsync();

        LexiconEntryDetail before = await _test.ShowProtectedAsync();

        string[] snapshot = await _test.SnapshotAsync();

        string trigger = point switch
        {
            "canonical" => "AFTER UPDATE ON lexicon_entries",
            "deleteLabel" => "AFTER DELETE ON artifact_sensitivity",
            "insertLabel" => "AFTER INSERT ON artifact_sensitivity",
            "provenance" => "AFTER INSERT ON lexicon_annal_fact_provenance WHEN (SELECT OriginCode FROM annal_versions WHERE VersionId = NEW.AnnalVersionId) = 1",
            _ => "AFTER UPDATE ON annal_heads WHEN NEW.CurrentRevision = 2",
        };

        if (point == "fts")
        {
            await _test.ExecuteAsync("""
                DROP TRIGGER lexicon_entries_au;
                CREATE TRIGGER lexicon_entries_au AFTER UPDATE ON lexicon_entries BEGIN
                    INSERT INTO lexicon_fts(lexicon_fts, rowid, Name, Type, FactsText)
                    SELECT 'delete', old.rowid, old.Name, old.Type, old.FactsText WHERE old.RetiredAtUtc IS NULL;
                    INSERT INTO lexicon_fts(rowid, Name, Type, FactsText)
                    SELECT new.rowid, new.Name, new.Type, new.FactsText WHERE new.RetiredAtUtc IS NULL;
                    SELECT RAISE(ABORT, 'injected publication failure');
                END;
                """);
        }
        else
        {
            await _test.ExecuteAsync($"CREATE TEMP TRIGGER injected_failure {trigger} BEGIN SELECT RAISE(ABORT, 'injected publication failure'); END;");
        }

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        var result = await _test.Service.CorrectAsync(before.Target, new("Person", ["beta", "gamma"]), lease);

        Assert.Equal(ErrorCodes.Lexicon.WriteFailed, result.Error.Code);

        TestLogEntry failure = Assert.Single(_test.Logger.Entries, entry => entry.Exception is SqliteException);

        Assert.Contains("injected publication failure", failure.Exception!.Message, StringComparison.Ordinal);

        Assert.Equal(snapshot, await _test.SnapshotAsync());

        Assert.Equal(1L, await _test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'alpha'"));

        Assert.Equal(0L, await _test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'gamma'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_revocation_immediately_before_commit_rolls_back_even_an_unprotected_entry(bool protectedEntry)
    {
        await _test.SeedAsync();

        if (protectedEntry)
        {
            await _test.ProtectAsync();
        }

        LexiconEntryDetail before = protectedEntry ? await _test.ShowProtectedAsync() : await _test.ShowAsync();

        string[] snapshot = await _test.SnapshotAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write) { StaleAfter = 1 };

        await using CovenantWriteLease lease = new(registration);

        var result = await _test.Service.CorrectAsync(before.Target, new("Person", ["beta"]), lease);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

        Assert.Equal(2, registration.Revalidations);

        Assert.Equal(snapshot, await _test.SnapshotAsync());
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("provenance")]
    [InlineData("head")]
    [InlineData("labelDigest")]
    public async Task Inconsistent_current_evidence_fails_closed_before_no_op(string evidence)
    {
        await _test.SeedAsync();

        await _test.ProtectAsync(withHead: true);

        LexiconEntryDetail before = await _test.ShowProtectedAsync();

        await _test.ExecuteAsync(evidence switch
        {
            "canonical" => "UPDATE lexicon_entries SET FactsText = 'wrong'",
            "provenance" => "UPDATE lexicon_fact_attachment_provenance SET Fact = 'absent fact' WHERE Fact = 'alpha'",
            "head" => "PRAGMA foreign_keys = OFF; DROP TRIGGER annal_heads_validate_update; UPDATE annal_heads SET CurrentRevision = 99",
            _ => "DROP TRIGGER artifact_sensitivity_guard_update; UPDATE artifact_sensitivity SET ArtifactLabelDigest = zeroblob(32)",
        });

        string[] snapshot = await _test.SnapshotAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        var result = await _test.Service.CorrectAsync(before.Target, new("general", ["alpha", "beta"]), lease);

        Assert.Equal(ErrorCodes.Lexicon.CurationIntegrityFailed, result.Error.Code);

        Assert.Equal(snapshot, await _test.SnapshotAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("read")]
    [InlineData("campaign")]
    public async Task Protected_correction_requires_matching_exact_scope_write_authority(string mismatch)
    {
        await _test.SeedAsync();

        await _test.ProtectAsync();

        LexiconEntryDetail before = await _test.ShowProtectedAsync();

        using LeaseRegistration registration = new(mismatch == "read" ? CovenantLeaseKind.Read : CovenantLeaseKind.Write);

        using LeaseRegistration scoped = new(CovenantLeaseKind.Write)
        {
            Snapshot = registration.Snapshot with { Scope = CovenantOperationScope.ForCampaign(Guid.NewGuid()) },
        };

        await using CovenantWriteLease lease = new(mismatch == "campaign" ? scoped : registration);

        string[] snapshot = await _test.SnapshotAsync();

        var result = await _test.Service.CorrectAsync(before.Target, new("Person", ["beta"]), mismatch == "missing" ? null : lease);

        Assert.Equal(mismatch == "missing" ? ErrorCodes.Lexicon.ProtectedMutationRefused : ErrorCodes.Covenant.ForbiddenAuthority, result.Error.Code);

        Assert.Equal(snapshot, await _test.SnapshotAsync());
    }

    [Fact]
    public async Task Changed_casing_is_a_new_operator_fact_without_an_attachment_source()
    {
        LexiconEntryDetail before = await _test.SeedAsync();

        var result = await _test.Service.CorrectAsync(before.Target, new("general", ["BETA", "beta"]), null);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(["BETA", "beta"], result.Value.Entry.Entry.Facts);

        Assert.Equal("beta", Assert.Single(result.Value.Entry.Entry.FactProvenance!).Fact);

        var currentVersion = result.Value.Entry.AnnalHistory[^1];

        Assert.Equal(1, Assert.Single(result.Value.Entry.HistoricalFactProvenance,
            source => source.AnnalVersionId == currentVersion.VersionId).FactOrdinal);
    }
}

internal sealed class CorrectionFixture : IAsyncDisposable
{
    private readonly ArcanumDbContext _db;

    internal readonly string Path;

    internal LexiconService Concrete { get; }

    internal ArcanumSettings Settings { get; }

    internal TestCapturingLogger<LexiconService> Logger { get; } = new();

    internal ILexiconCurationService Service => Concrete;

    internal SqliteConnection Connection => (SqliteConnection)_db.Database.GetDbConnection();

    internal CorrectionFixture(GrimoireFixture fixture, bool annals = false)
    {
        Path = fixture.CopyDatabase();

        _db = fixture.CreateContext(Path);

        Settings = new ArcanumSettings { Features = new FeatureSettings { Annals = annals } };

        Concrete = new LexiconService(_db, Logger,
            new TestOptionsMonitor<ArcanumSettings>(Settings));
    }

    internal async Task<LexiconEntryDetail> SeedAsync()
    {
        AttachmentMemoryProvenance source = new(Guid.NewGuid(), Guid.NewGuid(), "source", 1,
            "attachment-hash", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "WorkspaceFile", AttachmentSourceAvailability.Available);

        var seed = await Concrete.UpsertAsync("Entity", "general", ["alpha", "beta"], source, LexiconScope.Global);

        Assert.True(seed.IsSuccess, seed.Error.Message);

        return await ShowAsync();
    }

    internal async Task<LexiconEntryDetail> ShowAsync(ICovenantSnapshotReadLease? lease = null)
    {
        var result = await Concrete.ShowExactAsync(LexiconCorrectionTests.Global, "entity", lease);

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value.Value;
    }

    internal async Task<LexiconEntryDetail> ShowProtectedAsync()
    {
        using LeaseRegistration registration = new(CovenantLeaseKind.Read);

        await using CovenantReadLease lease = new(registration);

        return await ShowAsync(lease);
    }

    internal async Task<ArtifactSensitivityLabel> ProtectAsync(ulong revision = 1, bool withHead = false, bool maintenance = false)
    {
        LexiconEntryDetail before = await ShowAsync();

        Guid session = before.Entry.FactProvenance![0].Source.SessionId;

        await ExecuteAsync($"INSERT INTO Sessions(Id, Title, CreatedAt, UpdatedAt) VALUES ('{session.ToString().ToUpperInvariant()}', 'correction', '2026-09-01T00:00:00.0000000Z', '2026-09-01T00:00:00.0000000Z')");

        var canonical = LexiconValueNormalizer.NormalizeCorrection(before.Entry.Name, before.Entry.Type, before.Entry.Facts).Value;

        DerivedArtifactWrite write = new(SensitiveArtifactKind.Lexicon, before.Entry.Id, session, null, Guid.NewGuid(), revision,
            DerivedArtifactContentDigest.ForBytes(LexiconSnapshotDigest.Encode(canonical)), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]), maintenance ? null : new CovenantDigest(Enumerable.Repeat((byte)1, 32).ToArray()),
            maintenance ? null : new CovenantDigest(Enumerable.Repeat((byte)2, 32).ToArray()),
            maintenance ? new CovenantDigest(Enumerable.Repeat((byte)3, 32).ToArray()) : null);

        await using SqliteTransaction transaction = Connection.BeginTransaction();

        var result = await ArtifactSensitivityLedger.WriteWithinAsync(Connection, transaction, write, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        if (withHead)
        {
            await AnnalsClaimWriter.AppendCorrectionAsync(Connection, transaction, AnnalSubjectStore.Lexicon,
                before.Entry.Id.ToString("N"), AnnalOrigin.AgentAsserted, SagaMemoryScopeKind.Global, null,
                ContentSensitivity.CovenantDerived, AnnalContentHashFormat.LexiconStructuredSnapshot,
                Convert.FromHexString(before.SnapshotDigest), before.Entry.UpdatedAt, before.Entry.UpdatedAt, null, CancellationToken.None);
        }

        await transaction.CommitAsync();

        return (await ArtifactSensitivityLedger.ReadLabelWithinAsync(Connection, null, SensitiveArtifactKind.Lexicon,
            before.Entry.Id, CancellationToken.None)).Value!;
    }

    internal async Task ExecuteAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    internal async Task<object?> ScalarAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    internal async Task<string[]> SnapshotAsync()
    {
        List<string> snapshot = [];

        foreach (string table in new[] { "lexicon_entries", "lexicon_fts", "lexicon_fact_attachment_provenance", "lexicon_annal_fact_provenance", "annal_claims", "annal_versions", "annal_heads", "annal_dependencies", "artifact_sensitivity", "session_sensitivity_state" })
        {
            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                snapshot.Add(table + ":" + string.Join("|", Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.GetValue(i) is byte[] bytes ? Convert.ToHexString(bytes) : reader.GetValue(i).ToString())));
            }
        }

        return [.. snapshot];
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();

        File.Delete(Path);
    }
}

internal sealed class LeaseRegistration(CovenantLeaseKind kind) : ICovenantLeaseRegistration, IDisposable
{
    internal int Revalidations { get; private set; }

    internal int StaleAfter { get; init; } = int.MaxValue;

    public CovenantOperationLeaseSnapshot Snapshot { get; init; } = new(Guid.NewGuid(), 1, kind,
        CovenantLeaseCoverage.Scoped, CovenantOperationScope.Global, null, 1, 1, 0, null, null, null, null, null, false);

    public CancellationToken Revocation => CancellationToken.None;

    public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(
        Revalidations++ >= StaleAfter ? Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "Lease revoked.")) : Result.Success());

    public ValueTask ReleaseAsync() => ValueTask.CompletedTask;

    public void Dispose() { }
}
