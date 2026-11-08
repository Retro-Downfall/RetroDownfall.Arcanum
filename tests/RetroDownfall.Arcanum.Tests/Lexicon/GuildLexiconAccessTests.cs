using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

public sealed class GuildLexiconAccessTests
{
    [Theory]
    [InlineData("exact")]
    [InlineData("fts")]
    [InlineData("like")]
    [InlineData("clean")]
    [InlineData("named")]
    [InlineData("scoped")]
    public async Task Global_reader_does_not_materialize_or_shadow_from_ungranted_campaign(string path)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        Guid campaign = Guid.NewGuid();

        Assert.True((await service.UpsertAsync("Beacon", "General", ["permitted anchor"], LexiconScope.Global)).IsSuccess);

        Assert.True((await service.UpsertAsync("Beacon", "General", ["forbidden anchor"], LexiconScope.ForCampaign(campaign))).IsSuccess);

        if (path == "like")
        {
            // Shadow only this reader's FTS name; the durable catalog remains healthy for Signet admission.
            await using SqliteCommand unavailableFts = connection.CreateCommand();

            unavailableFts.CommandText = "CREATE TEMP VIEW lexicon_fts AS SELECT rowid, Name, Type, FactsText FROM lexicon_entries";

            _ = await unavailableFts.ExecuteNonQueryAsync();
        }

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Read, campaign);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        LexiconScope requested = LexiconScope.ForCampaign(campaign);

        if (path is "named" or "scoped")
        {
            Result<LexiconEntryDto?> named = path == "named"
                ? await service.GetByNameAsync("Beacon", requested)
                : await service.GetByNameInScopeAsync("Beacon", requested);

            Assert.True(named.IsSuccess, named.Error.Message);

            if (path == "scoped")
            {
                Assert.Null(named.Value);
            }
            else
            {
                Assert.Equal(["permitted anchor"], named.Value!.Facts);
            }

            return;
        }

        Result<IReadOnlyList<LexiconEntryDto>> matched = path == "clean"
            ? await service.MatchCleanEntitiesAsync(["Beacon"], 1, requested)
            : await service.MatchEntitiesAsync([path == "exact" ? "Beacon" : "anchor"], 1, requested);

        Assert.True(matched.IsSuccess, matched.Error.Message);

        Assert.Equal(["permitted anchor"], Assert.Single(matched.Value).Facts);
    }

    [Fact]
    public async Task Eligibility_does_not_probe_ungranted_campaign()
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        LexiconScope campaign = LexiconScope.ForCampaign(Guid.NewGuid());

        Assert.True((await service.UpsertAsync("Hidden", null, ["private"], campaign)).IsSuccess);

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Read, campaign.CampaignId);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        Result<bool> eligible = await service.HasEligibleEntriesAsync(campaign);

        Assert.True(eligible.IsSuccess, eligible.Error.Message);

        Assert.False(eligible.Value);
    }

    [Theory]
    [InlineData("append")]
    [InlineData("delete")]
    [InlineData("deletion-target")]
    [InlineData("deletion-identity")]
    public async Task Read_grant_cannot_mutate_or_probe_delete_authority(string path)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        Assert.True((await service.UpsertAsync("Beacon", null, ["original"], LexiconScope.Global)).IsSuccess);

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Read);

        using (GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution)))
        {
            Error error = path switch
            {
                "append" => (await service.UpsertAsync("Beacon", null, ["forbidden"], LexiconScope.Global)).Error,
                "delete" => (await service.DeleteByNameAsync("Beacon", LexiconScope.Global, LexiconDeletionOrigin.Agent)).Error,
                "deletion-target" => (await service.FindAgentDeletionTargetAsync("Beacon", LexiconScope.Global)).Error,
                _ => (await service.FindAllLifecycleIdentityForDeletionAsync("Beacon", LexiconScope.Global)).Error,
            };

            Assert.Equal(ErrorCodes.Guild.MemoryNotGranted, error.Code);
        }

        Assert.Equal(["original"], (await service.GetByNameAsync("Beacon", LexiconScope.Global)).Value!.Facts);
    }

    [Fact]
    public async Task Append_to_ungranted_campaign_leaves_both_tiers_unchanged()
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        LexiconScope campaign = LexiconScope.ForCampaign(Guid.NewGuid());

        Assert.True((await service.UpsertAsync("Beacon", null, ["global"], LexiconScope.Global)).IsSuccess);

        Assert.True((await service.UpsertAsync("Beacon", null, ["campaign"], campaign)).IsSuccess);

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Append);

        using (GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution)))
        {
            Result<LexiconEntryDto> result = await service.UpsertAsync("Beacon", null, ["forbidden"], campaign);

            Assert.Equal(ErrorCodes.Guild.MemoryNotGranted, result.Error.Code);
        }

        Assert.Equal(["global"], (await service.GetByNameInScopeAsync("Beacon", LexiconScope.Global)).Value!.Facts);

        Assert.Equal(["campaign"], (await service.GetByNameInScopeAsync("Beacon", campaign)).Value!.Facts);
    }

    [Fact]
    public async Task Guild_append_captures_immutable_version_when_annals_capture_is_disabled()
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Append);

        using (GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution)))
        {
            Assert.True((await service.UpsertAsync("Beacon", null, ["new fact"], LexiconScope.Global)).IsSuccess);

            Assert.True((await service.UpsertAsync("Beacon", null, ["new fact"], LexiconScope.Global)).IsSuccess);
        }

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM annal_versions"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));

        Assert.Equal(execution.AgentId, Guid.Parse(Assert.IsType<string>(await f.ScalarAsync("SELECT AgentId FROM guild_memory_attribution"))));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_memory_attribution a JOIN annal_versions v ON v.VersionId=a.ImmutableVersionId JOIN annal_claims c ON c.ClaimId=v.ClaimId AND c.SubjectId=a.SubjectId WHERE a.StoreCode=2"));
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("pin")]
    [InlineData("retire")]
    public async Task Memory_grants_do_not_enable_operator_curation(string operation)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        Assert.True((await service.UpsertAsync("Beacon", null, ["original"], LexiconScope.Global)).IsSuccess);

        var detail = await service.ShowExactAsync(new(LexiconScopeKind.Global, null), "Beacon", null);

        Assert.True(detail.IsSuccess, detail.Error.Message);

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Read);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        Error error = operation switch
        {
            "inspect" => (await service.ListInspectionAsync(null)).Error,
            "pin" => (await service.PinAsync(detail.Value.Value.Target, null)).Error,
            _ => (await service.RetireAsync(detail.Value.Value.Target, null)).Error,
        };

        Assert.StartsWith("Guild.", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_delete_cannot_borrow_operator_origin_to_remove_pinned_or_retired_entry(bool retired)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        Assert.True((await service.UpsertAsync("Beacon", null, ["original"], LexiconScope.Global)).IsSuccess);

        var detail = await service.ShowExactAsync(new(LexiconScopeKind.Global, null), "Beacon", null);

        Assert.True(detail.IsSuccess, detail.Error.Message);

        Assert.True((retired
            ? await service.RetireAsync(detail.Value.Value.Target, null)
            : await service.PinAsync(detail.Value.Value.Target, null)).IsSuccess);

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Delete);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        Result<bool> result = await service.DeleteByNameAsync("Beacon", LexiconScope.Global);

        Assert.Equal(retired ? ErrorCodes.Lexicon.RetiredMutationRefused : ErrorCodes.Lexicon.PinnedMutationRefused, result.Error.Code);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM lexicon_entries"));
    }

    [Fact]
    public async Task Attribution_insert_failure_rolls_back_canonical_fact_and_immutable_version()
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        Assert.True((await service.UpsertAsync("Beacon", null, ["original"], LexiconScope.Global)).IsSuccess);

        object? originalVersionCount = await f.ScalarAsync("SELECT count(*) FROM annal_versions");

        object? originalHead = await f.ScalarAsync("SELECT CurrentVersionId FROM annal_heads");

        int attributionAttempts = 0;

        connection.CreateFunction("observe_guild_attribution", () => ++attributionAttempts);

        await using (SqliteCommand fault = connection.CreateCommand())
        {
            fault.CommandText = "CREATE TEMP TRIGGER reject_guild_attribution BEFORE INSERT ON main.guild_memory_attribution BEGIN SELECT observe_guild_attribution(); SELECT RAISE(ABORT, 'attribution blocked'); END";

            _ = await fault.ExecuteNonQueryAsync();
        }

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Append);

        using (GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution)))
        {
            Result<LexiconEntryDto> changed = await service.UpsertAsync("Beacon", null, ["must roll back"], LexiconScope.Global);

            Assert.True(changed.IsFailure);
        }

        Assert.Equal(1, attributionAttempts);

        Assert.Equal(["original"], (await service.GetByNameAsync("Beacon", LexiconScope.Global)).Value!.Facts);

        Assert.Equal(originalVersionCount, await f.ScalarAsync("SELECT count(*) FROM annal_versions"));

        Assert.Equal(originalHead, await f.ScalarAsync("SELECT CurrentVersionId FROM annal_heads"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));
    }

    [Fact]
    public async Task Revoked_execution_cannot_reuse_service_reader()
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        Assert.True((await service.UpsertAsync("Beacon", null, ["original"], LexiconScope.Global)).IsSuccess);

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Read);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        Assert.NotNull((await service.GetByNameAsync("Beacon", LexiconScope.Global)).Value);

        Guid key = Guid.NewGuid();

        Assert.True((await f.Authority.RevokeAsync(f.Operator, new(key, new string('A', 64)), execution.SignetId,
            new(key, 1, GuildCancellationReason.OperatorRequested), default)).IsSuccess);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, (await service.GetByNameAsync("Beacon", LexiconScope.Global)).Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Campaign_grant_does_not_add_global_fallback_or_enable_campaign_tier_when_existing_scope_is_global(bool globalOnlyPath)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        f.Policy.NextOwners = new(session, campaign, null);

        Assert.True((await service.UpsertAsync("CampaignFact", null, ["anchor"], LexiconScope.ForCampaign(campaign))).IsSuccess);

        Assert.True((await service.UpsertAsync("GlobalFact", null, ["anchor"], LexiconScope.Global)).IsSuccess);

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Campaign, campaign))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        LexiconScope scope = globalOnlyPath ? LexiconScope.Global : LexiconScope.ForCampaign(campaign);

        var matched = await service.MatchEntitiesAsync(["anchor"], 10, scope);

        Assert.True(matched.IsSuccess, matched.Error.Message);

        if (globalOnlyPath)
        {
            Assert.Empty(matched.Value);
        }
        else
        {
            Assert.Equal("CampaignFact", Assert.Single(matched.Value).Name);
        }

        Assert.Null((await service.GetByNameAsync("GlobalFact", scope)).Value);
    }

    [Theory]
    [InlineData("unmaterialized")]
    [InlineData("omitted")]
    [InlineData("changed-version")]
    [InlineData("ended-turn")]
    public async Task Guild_append_requires_exact_live_attachment_materialization_provenance(string proof)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        AttachmentMemoryProvenance provenance = new(Guid.NewGuid(), Guid.NewGuid(), "notes.txt", 1,
            new string('A', 64), DateTimeOffset.UtcNow, "text/plain", AttachmentSourceAvailability.Available);

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Append);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        using AttachmentMemoryGateAmbient.TurnScope turn = AttachmentMemoryGateAmbient.BeginTurn();

        if (proof != "unmaterialized")
        {
            turn.RegisterMaterialized(provenance);
        }

        if (proof == "ended-turn")
        {
            turn.Dispose();
        }

        Result<LexiconEntryDto> result = proof == "omitted"
            ? await service.UpsertAsync("Beacon", null, ["unauthorized promotion"], LexiconScope.Global)
            : await service.UpsertAsync("Beacon", null, ["unauthorized promotion"],
                proof == "changed-version" ? provenance with { Version = 2 } : provenance, LexiconScope.Global);

        Assert.True(result.IsFailure, "A direct store call must not bypass the tool's attachment promotion boundary.");

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM lexicon_entries"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM annal_versions"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_memory_attribution"));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("fts")]
    [InlineData("like")]
    [InlineData("named")]
    [InlineData("eligible")]
    public async Task Guild_regular_readers_exclude_protected_content_before_top_k_and_eligibility(string path)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        if (path is "exact" or "fts" or "like")
        {
            Assert.True((await service.UpsertAsync("Public", null, ["festival public"], LexiconScope.Global)).IsSuccess);
        }

        var secret = await service.UpsertAsync(path is "fts" or "like" ? "Private" : "festival", null,
            ["festival festival festival secret"], LexiconScope.Global);

        Assert.True(secret.IsSuccess);

        await using (SqliteCommand label = connection.CreateCommand())
        {
            label.CommandText = """
                INSERT INTO artifact_sensitivity(LabelId,ArtifactKindCode,ArtifactId,SensitivityCode,
                    ProvenanceModeCode,ExactGenerationIds,ArtifactRevision,ArtifactContentDigest,
                    SensitivityDigest,ArtifactLabelDigest,CreatedAtUtc)
                VALUES('guild-private-lexicon',7,$id,1,1,zeroblob(16),1,zeroblob(32),zeroblob(32),zeroblob(32),'2026-10-20T00:00:00.0000000Z');
                """;

            label.Parameters.AddWithValue("$id", secret.Value.Id.ToString("D"));

            _ = await label.ExecuteNonQueryAsync();
        }

        if (path == "like")
        {
            await using SqliteCommand unavailableFts = connection.CreateCommand();

            unavailableFts.CommandText = "CREATE TEMP VIEW lexicon_fts AS SELECT rowid, Name, Type, FactsText FROM lexicon_entries";

            _ = await unavailableFts.ExecuteNonQueryAsync();
        }

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Read);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        if (path == "eligible")
        {
            Assert.False((await service.HasEligibleEntriesAsync(LexiconScope.Global)).Value);
        }
        else if (path == "named")
        {
            Assert.Null((await service.GetByNameAsync("festival", LexiconScope.Global)).Value);

            Assert.Null((await service.GetByNameInScopeAsync("festival", LexiconScope.Global)).Value);
        }
        else
        {
            var matched = await service.MatchEntitiesAsync(["festival"], 1, LexiconScope.Global);

            Assert.True(matched.IsSuccess, matched.Error.Message);

            Assert.Equal("Public", Assert.Single(matched.Value).Name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Append_without_read_acknowledges_only_submitted_content(bool noOp)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        var original = await service.UpsertAsync("Beacon", "Private type", ["existing secret"], LexiconScope.Global);

        Assert.True(original.IsSuccess);

        GuildExecutionHandle execution = await MintGlobalAsync(f, GuildMemoryOperation.Append);

        using (GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution)))
        {
            string[] submitted = noOp ? ["existing secret"] : ["submitted fact"];

            var appended = await service.UpsertAsync("beacon", null, submitted, LexiconScope.Global);

            Assert.True(appended.IsSuccess, appended.Error.Message);

            Assert.Equal(original.Value.Id, appended.Value.Id);

            Assert.Equal(submitted, appended.Value.Facts);

            Assert.Equal("beacon", appended.Value.Name);

            Assert.Equal("General", appended.Value.Type);

            Assert.Null(appended.Value.FactProvenance);

            Assert.Null(appended.Value.PinnedAtUtc);
        }

        var stored = await service.GetByNameAsync("Beacon", LexiconScope.Global);

        Assert.Contains("existing secret", stored.Value!.Facts);
    }

    [Theory]
    [InlineData(GuildMemoryOperation.Append)]
    [InlineData(GuildMemoryOperation.Delete)]
    public async Task Guild_mutation_refuses_protected_content_without_an_optional_operator_guard(GuildMemoryOperation operation)
    {
        await using var f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService service = CreateService(db);

        var original = await service.UpsertAsync("Beacon", "General", ["protected content"], LexiconScope.Global);

        Assert.True(original.IsSuccess);

        var canonical = LexiconValueNormalizer.NormalizeCorrection(original.Value.Name, original.Value.Type, original.Value.Facts).Value;

        DerivedArtifactWrite write = new(SensitiveArtifactKind.Lexicon, original.Value.Id, null, null, Guid.NewGuid(), 1,
            DerivedArtifactContentDigest.ForBytes(LexiconSnapshotDigest.Encode(canonical)), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]), new CovenantDigest(Enumerable.Repeat((byte)1, 32).ToArray()),
            new CovenantDigest(Enumerable.Repeat((byte)2, 32).ToArray()), null);

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            var labeled = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, write, CancellationToken.None);

            Assert.True(labeled.IsSuccess, labeled.Error.Message);

            await transaction.CommitAsync();
        }

        Assert.True((await service.UpsertAsync("Beacon", "General", ["protected content"], LexiconScope.Global)).IsSuccess);

        GuildExecutionHandle execution = await MintGlobalAsync(f, operation);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        Error error = operation == GuildMemoryOperation.Append
            ? (await service.UpsertAsync("Beacon", "General", ["protected content"], LexiconScope.Global)).Error
            : (await service.DeleteByNameAsync("Beacon", LexiconScope.Global)).Error;

        Assert.Equal(ErrorCodes.Lexicon.ProtectedMutationRefused, error.Code);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM lexicon_entries"));
    }

    private static LexiconService CreateService(ArcanumDbContext db) => new(db,
        NullLogger<LexiconService>.Instance, new TestOptionsMonitor<ArcanumSettings>(new() { Features = new() { Annals = false } }), MemoryErasureTestKeys.Isolated());

    private static async Task<GuildExecutionHandle> MintGlobalAsync(GuildSignetAuthorityTests.Fixture f, GuildMemoryOperation operation, Guid? campaign = null)
    {
        if (campaign is { } campaignId)
        {
            Guid session = Guid.NewGuid();

            await f.SeedOwnersAsync(campaignId, session, Guid.NewGuid());

            f.Policy.NextOwners = new(session, campaignId, null);
        }

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Lexicon, operation, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        return await f.RootAsync(grant);
    }
}
