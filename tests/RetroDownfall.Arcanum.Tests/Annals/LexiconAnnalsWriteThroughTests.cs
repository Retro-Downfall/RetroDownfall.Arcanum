using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Annals;

/// <summary>
/// The Lexicon is the one store in this slice whose rows change, so it is where the difference between
/// asserting a claim and correcting one is observable.
/// </summary>
/// <remarks>
/// The entry point is <see cref="ILexiconService"/>, which is what <c>scribe_lexicon</c> and every
/// endpoint hold. Nothing here writes an <c>annal_*</c> row.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconAnnalsWriteThroughTests : IAsyncLifetime
{

    private static readonly Guid CampaignA = new("A0000000-0000-4000-8000-0000000000AA");

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public LexiconAnnalsWriteThroughTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    /// <summary>
    /// A claim's subject id has to be the <i>exact</i> text its table stores, or nothing can ever join
    /// the two: not an erasure, not a retention sweep, not a reader. This joins them the way production
    /// does rather than reformatting the id to match, which is how the mismatch hid in the first place.
    /// </summary>
    [SkippableFact]
    public async Task A_claim_names_its_entity_by_the_id_the_lexicon_table_actually_stores()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService service = CreateService(annals: true);

        _ = await service.UpsertAsync(
            "config",
            "Project",
            ["ships on Friday"],
            LexiconScope.Global,
            CancellationToken.None);

        Assert.Equal(
            1,
            await CountAsync(
                """
                SELECT COUNT(*) FROM lexicon_entries AS entry
                JOIN annal_claims AS claim
                    ON claim.SubjectStoreCode = 2 AND claim.SubjectId = entry.Id;
                """));

    }

    [SkippableFact]
    public async Task A_first_upsert_asserts_revision_one()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService service = CreateService();

        Result<LexiconEntryDto> written = await service.UpsertAsync(
            "config",
            "Project",
            ["ships on Friday"],
            LexiconScope.Global,
            CancellationToken.None);

        Assert.True(written.IsSuccess);

        IReadOnlyList<VersionRow> versions = await ReadVersionsAsync(written.Value.Id.ToString("N"));

        VersionRow only = Assert.Single(versions);

        Assert.Equal(1, only.Revision);

        Assert.Equal(AnnalOperation.Assert, only.Operation);

        // A Lexicon write is a tool call a model chose to make, not something taken from a transcript
        // behind its back.
        Assert.Equal(AnnalOrigin.AgentAsserted, only.Origin);

        Assert.Null(only.PredecessorVersionId);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM annal_dependencies;"));

    }

    [SkippableFact]
    public async Task A_second_upsert_with_new_facts_appends_a_correction_that_supersedes_revision_one()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService service = CreateService(annals: true);

        Result<LexiconEntryDto> first = await service.UpsertAsync(
            "config",
            "Project",
            ["ships on Friday"],
            LexiconScope.Global,
            CancellationToken.None);

        _ = await service.UpsertAsync(
            "config",
            "Project",
            ["written in C#"],
            LexiconScope.Global,
            CancellationToken.None);

        IReadOnlyList<VersionRow> versions = await ReadVersionsAsync(first.Value.Id.ToString("N"));

        Assert.Equal(2, versions.Count);

        Assert.Equal(AnnalOperation.Assert, versions[0].Operation);

        Assert.Equal(AnnalOperation.Correct, versions[1].Operation);

        Assert.Equal(versions[0].VersionId, versions[1].PredecessorVersionId);

        EdgeRow edge = Assert.Single(await ReadEdgesAsync(versions[1].VersionId));

        Assert.Equal(AnnalDependencyRelation.Supersedes, edge.Relation);

        Assert.Equal(1, edge.Ordinal);

        Assert.Equal(versions[0].VersionId, edge.DependencyVersionId);

        HeadRow head = await ReadHeadAsync(first.Value.Id.ToString("N"));

        Assert.Equal(2, head.CurrentRevision);

        Assert.Equal(versions[1].VersionId, head.CurrentVersionId);

    }

    /// <summary>
    /// The whole promise of an append-only substrate: a correction adds a revision and leaves the record
    /// it corrects exactly as it was.
    /// </summary>
    [SkippableFact]
    public async Task Revision_one_is_unchanged_after_a_correction()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService service = CreateService(annals: true);

        Result<LexiconEntryDto> first = await service.UpsertAsync(
            "config",
            "Project",
            ["ships on Friday"],
            LexiconScope.Global,
            CancellationToken.None);

        VersionRow before = Assert.Single(await ReadVersionsAsync(first.Value.Id.ToString("N")));

        _ = await service.UpsertAsync(
            "config",
            "Project",
            ["written in C#"],
            LexiconScope.Global,
            CancellationToken.None);

        VersionRow after = (await ReadVersionsAsync(first.Value.Id.ToString("N")))[0];

        Assert.Equal(before, after);

    }

    /// <summary>
    /// A merge that adds no fact produces an unchanged fact set. Without the content comparison every
    /// repeated call restating a known fact would append a revision recording no change, and a claim's
    /// history would fill with noise a reader could not tell from real corrections.
    /// </summary>
    [SkippableFact]
    public async Task Re_scribing_an_identical_fact_set_appends_no_revision_and_does_not_move_the_head()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService service = CreateService(annals: true);

        Result<LexiconEntryDto> first = await service.UpsertAsync(
            "config",
            "Project",
            ["ships on Friday"],
            LexiconScope.Global,
            CancellationToken.None);

        HeadRow before = await ReadHeadAsync(first.Value.Id.ToString("N"));

        _ = await service.UpsertAsync(
            "config",
            "Project",
            ["ships on Friday"],
            LexiconScope.Global,
            CancellationToken.None);

        Assert.Single(await ReadVersionsAsync(first.Value.Id.ToString("N")));

        Assert.Equal(before, await ReadHeadAsync(first.Value.Id.ToString("N")));

    }

    [SkippableFact]
    public async Task A_campaign_scoped_entry_is_claimed_to_that_campaign_and_a_global_one_is_claimed_global()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService service = CreateService(annals: true);

        Result<LexiconEntryDto> global = await service.UpsertAsync(
            "config",
            "Project",
            ["installation wide"],
            LexiconScope.Global,
            CancellationToken.None);

        Result<LexiconEntryDto> scoped = await service.UpsertAsync(
            "config",
            "Project",
            ["campaign only"],
            LexiconScope.ForCampaign(CampaignA),
            CancellationToken.None);

        VersionRow globalVersion = Assert.Single(await ReadVersionsAsync(global.Value.Id.ToString("N")));

        Assert.Equal(SagaMemoryScopeKind.Global, globalVersion.ScopeKind);

        Assert.Null(globalVersion.CampaignId);

        VersionRow scopedVersion = Assert.Single(await ReadVersionsAsync(scoped.Value.Id.ToString("N")));

        Assert.Equal(SagaMemoryScopeKind.Campaign, scopedVersion.ScopeKind);

        Assert.Equal(CampaignA.ToString(), scopedVersion.CampaignId);

    }

    [SkippableFact]
    public async Task With_the_gate_off_an_upsert_appends_nothing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService service = CreateService(annals: false);

        Result<LexiconEntryDto> written = await service.UpsertAsync(
            "config",
            "Project",
            ["ships on Friday"],
            LexiconScope.Global,
            CancellationToken.None);

        Assert.True(written.IsSuccess);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM annal_claims;"));

    }

    /// <summary>
    /// No claim outlives the entity it describes.
    /// </summary>
    /// <remarks>
    /// A claim is reached through the row that names it, so one left behind is a record no surface can
    /// read and no reset can clear. It is also what lets a count over this store's own tables answer for
    /// its Annals rows as well, which a reset interrupted before its commit relies on: that inference is
    /// sound only while an entity and the claim explaining it go in one transaction or neither goes.
    ///
    /// <para>The entity that goes is corrected first, so its claim carries a revision beyond the one it
    /// opened with. A removal that released the head alone would leave those versions standing, and
    /// against a single-revision claim that is indistinguishable from taking the whole claim.</para>
    /// </remarks>
    [SkippableFact]
    public async Task No_claim_outlives_the_entity_it_describes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService service = CreateService(annals: true);

        Result<LexiconEntryDto> removed = await service.UpsertAsync(
            "config",
            "Project",
            ["ships on Friday"],
            LexiconScope.Global,
            CancellationToken.None);

        Assert.True(removed.IsSuccess);

        Assert.True(
            (await service.UpsertAsync(
                "config",
                "Project",
                ["ships on Monday"],
                LexiconScope.Global,
                CancellationToken.None)).IsSuccess);

        Assert.True(
            (await service.UpsertAsync(
                "release",
                "Project",
                ["is cut on the first"],
                LexiconScope.Global,
                CancellationToken.None)).IsSuccess);

        Assert.True(
            (await service.DeleteByNameAsync("config", LexiconScope.Global, CancellationToken.None)).Value);

        Assert.Equal(
            0,
            await CountAsync(
                """
                SELECT COUNT(*) FROM annal_claims
                WHERE SubjectStoreCode = 2
                  AND SubjectId NOT IN (SELECT Id FROM lexicon_entries);
                """));

        // The entity left standing is what makes the assertion above bite: its own claim belongs where
        // it is, so what a delete failed to take is the only thing an orphan count can be counting.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = 2;"));

    }

    private ILexiconService CreateService() => CreateService(new FeatureSettings());

    private ILexiconService CreateService(bool annals) =>
        CreateService(new FeatureSettings { Annals = annals });

    private ILexiconService CreateService(FeatureSettings features) =>
        new LexiconService(
            _db!,
            NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings { Features = features }));

    private async Task<IReadOnlyList<VersionRow>> ReadVersionsAsync(string entryId)
    {

        await OpenAsync();

        await using SqliteCommand command = (SqliteCommand)_db!.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            """
            SELECT version.VersionId, version.Revision, version.OperationCode, version.OriginCode,
                   version.ScopeKindCode, version.CampaignId, version.ContentHash,
                   version.RecordedAtUtc, version.PredecessorVersionId
            FROM annal_claims AS claim
            JOIN annal_versions AS version ON version.ClaimId = claim.ClaimId
            WHERE claim.SubjectStoreCode = 2 AND claim.SubjectId = $subjectId
            ORDER BY version.Revision;
            """;

        _ = command.Parameters.AddWithValue("$subjectId", entryId);

        List<VersionRow> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            rows.Add(
                new VersionRow(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    (AnnalOperation)reader.GetInt32(2),
                    (AnnalOrigin)reader.GetInt32(3),
                    (SagaMemoryScopeKind)reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    Convert.ToHexString((byte[])reader.GetValue(6)),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));

        }

        return rows;

    }

    private async Task<IReadOnlyList<EdgeRow>> ReadEdgesAsync(string versionId)
    {

        await OpenAsync();

        await using SqliteCommand command = (SqliteCommand)_db!.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            """
            SELECT DependencyVersionId, RelationCode, Ordinal
            FROM annal_dependencies
            WHERE DependentVersionId = $versionId
            ORDER BY Ordinal;
            """;

        _ = command.Parameters.AddWithValue("$versionId", versionId);

        List<EdgeRow> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {

            rows.Add(
                new EdgeRow(
                    reader.GetString(0),
                    (AnnalDependencyRelation)reader.GetInt32(1),
                    reader.GetInt32(2)));

        }

        return rows;

    }

    private async Task<HeadRow> ReadHeadAsync(string entryId)
    {

        await OpenAsync();

        await using SqliteCommand command = (SqliteCommand)_db!.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            """
            SELECT head.CurrentVersionId, head.CurrentRevision, head.CurrentOperationCode, head.UpdatedAtUtc
            FROM annal_claims AS claim
            JOIN annal_heads AS head ON head.ClaimId = claim.ClaimId
            WHERE claim.SubjectStoreCode = 2 AND claim.SubjectId = $subjectId;
            """;

        _ = command.Parameters.AddWithValue("$subjectId", entryId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None), $"no head for {entryId}");

        return new HeadRow(
            reader.GetString(0),
            reader.GetInt32(1),
            (AnnalOperation)reader.GetInt32(2),
            reader.GetString(3));

    }

    private async Task<int> CountAsync(string sql)
    {

        await OpenAsync();

        await using SqliteCommand command = (SqliteCommand)_db!.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

    }

    private async Task OpenAsync()
    {

        if (_db!.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {

            await _db.Database.OpenConnectionAsync(CancellationToken.None);

        }

    }

    /// <summary>Every column of one version, so an immutability assertion can compare the whole row.</summary>
    private sealed record VersionRow(
        string VersionId,
        int Revision,
        AnnalOperation Operation,
        AnnalOrigin Origin,
        SagaMemoryScopeKind ScopeKind,
        string? CampaignId,
        string ContentHashHex,
        string RecordedAtUtc,
        string? PredecessorVersionId);

    private sealed record EdgeRow(string DependencyVersionId, AnnalDependencyRelation Relation, int Ordinal);

    private sealed record HeadRow(
        string CurrentVersionId,
        int CurrentRevision,
        AnnalOperation CurrentOperation,
        string UpdatedAtUtc);

}
