using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class MemoryRecallLexiconTests(GrimoireFixture fixture)
{
    [Theory]
    [InlineData("exact")]
    [InlineData("fts")]
    [InlineData("fallback")]
    public async Task Clean_match_excludes_labeled_rows_before_limit(string mode)
    {
        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        LexiconService service = Service(db);

        var clean = await service.UpsertAsync("public entity", "General", ["festival public"], LexiconScope.Global);

        var secret = await service.UpsertAsync("festival", "General", ["festival festival festival secret"], LexiconScope.Global);

        Assert.True(clean.IsSuccess);

        Assert.True(secret.IsSuccess);

        await ExecuteAsync(db, """
            INSERT INTO artifact_sensitivity(LabelId, ArtifactKindCode, ArtifactId, SensitivityCode,
                ProvenanceModeCode, ExactGenerationIds, ArtifactRevision, ArtifactContentDigest,
                SensitivityDigest, ArtifactLabelDigest, CreatedAtUtc)
            VALUES ('recall-label', 7, $id, 1, 1, zeroblob(16), 1, zeroblob(32), zeroblob(32), zeroblob(32), '2026-10-20T00:00:00Z');
            """, secret.Value.Id.ToString("D").ToLowerInvariant());

        if (mode != "exact")
        {
            await ExecuteAsync(db, "UPDATE lexicon_entries SET Name = 'private entity', NameNormalized = 'PRIVATE ENTITY' WHERE Id = $id;", secret.Value.Id.ToString("N"));
        }

        if (mode == "fallback")
        {
            await ExecuteAsync(db, "DROP TABLE lexicon_fts;");
        }

        const string query = "festival";

        var result = await service.MatchCleanEntitiesAsync([query], 1, LexiconScope.Global);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(clean.Value.Id, Assert.Single(result.Value).Id);
    }

    [Theory]
    [InlineData("head-sensitivity")]
    [InlineData("deleted-source")]
    [InlineData("deleted-campaign")]
    public async Task Clean_match_excludes_protected_heads_and_deleted_owners(string mode)
    {
        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        LexiconService service = Service(db);

        Guid campaign = new("a0000000-0000-4000-8000-0000000000aa");

        LexiconScope scope = mode == "deleted-campaign" ? LexiconScope.ForCampaign(campaign) : LexiconScope.Global;

        var stored = await service.UpsertAsync("festival", "General", ["private festival"], scope);

        Assert.True(stored.IsSuccess);

        if (mode == "deleted-campaign")
        {
            await ExecuteAsync(db, "INSERT INTO owner_deletion_events(OwnerKindCode, OwnerId, DeletedAtUtc) VALUES (1, $id, '2026-10-20T00:00:00Z');", campaign.ToString("D").ToUpperInvariant());
        }
        else
        {
            await ExecuteAsync(db, """
                INSERT INTO annal_claims(ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
                VALUES ('recall-claim', 2, $id, '2026-10-20T00:00:00Z');
                INSERT INTO annal_versions(VersionId, ClaimId, Revision, OperationCode, OriginCode,
                    ScopeKindCode, SensitivityCode, ContentHash, ValidFromUtc, RecordedAtUtc, SourceSessionId)
                VALUES ('recall-version', 'recall-claim', 1, 1, 2, 1,
                """ + (mode == "head-sensitivity" ? "1" : "0") + """
                    , zeroblob(32), '2026-10-20T00:00:00Z', '2026-10-20T00:00:00Z', 'B0000000-0000-4000-8000-0000000000BB');
                INSERT INTO annal_heads(ClaimId, SubjectStoreCode, CurrentVersionId, CurrentRevision, CurrentOperationCode, UpdatedAtUtc)
                VALUES ('recall-claim', 2, 'recall-version', 1, 1, '2026-10-20T00:00:00Z');
                """, stored.Value.Id.ToString("N"));

            if (mode == "deleted-source")
            {
                await ExecuteAsync(db, "INSERT INTO owner_deletion_events(OwnerKindCode, OwnerId, DeletedAtUtc) VALUES (2, 'b0000000-0000-4000-8000-0000000000bb', '2026-10-20T00:00:00Z');");
            }
        }

        var result = await service.MatchCleanEntitiesAsync(["festival"], 1, scope);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task Clean_match_refuses_oversized_candidate_instead_of_skipping_it()
    {
        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        LexiconService service = Service(db);

        var stored = await service.UpsertAsync("festival", "General", ["festival"], LexiconScope.Global);

        Assert.True(stored.IsSuccess);

        await ExecuteAsync(db, "UPDATE lexicon_entries SET FactsJson = '[\"' || replace(hex(zeroblob(35000)), '0', 'x') || '\"]' WHERE Id = $id;", stored.Value.Id.ToString("N"));

        var result = await service.MatchCleanEntitiesAsync(["festival"], 1, LexiconScope.Global);

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData("malformed-row")]
    [InlineData("fts-fallback")]
    public async Task Clean_match_failure_logs_static_diagnostics_without_exception_objects(string mode)
    {
        await using ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        TestCapturingLogger<LexiconService> logger = new();

        LexiconService service = new(db, logger,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { Lexicon = true, Annals = false } }),
            MemoryErasureTestKeys.Isolated());

        var stored = await service.UpsertAsync("festival", "General", ["private festival"], LexiconScope.Global);

        Assert.True(stored.IsSuccess);

        if (mode == "malformed-row")
        {
            await ExecuteAsync(db, "UPDATE lexicon_entries SET UpdatedAt = 'private-source-marker' WHERE Id = $id;", stored.Value.Id.ToString("N"));
        }
        else
        {
            await ExecuteAsync(db, "DROP TABLE lexicon_fts;");
        }

        var result = await service.MatchCleanEntitiesAsync([mode == "malformed-row" ? "festival" : "private"], 1, LexiconScope.Global);

        Assert.Equal(mode == "malformed-row", result.IsFailure);

        Assert.NotEmpty(logger.Entries);

        Assert.All(logger.Entries, entry =>
        {
            Assert.Null(entry.Exception);

            Assert.DoesNotContain("private", entry.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static LexiconService Service(ArcanumDbContext db) => new(db, NullLogger<LexiconService>.Instance,
        new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { Lexicon = true, Annals = false } }),
        MemoryErasureTestKeys.Isolated());

    private static async Task ExecuteAsync(ArcanumDbContext db, string sql, string? id = null)
    {
        await db.Database.OpenConnectionAsync();

        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        if (id is not null)
        {
            var parameter = command.CreateParameter();

            parameter.ParameterName = "$id";

            parameter.Value = id;

            command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync();
    }
}
