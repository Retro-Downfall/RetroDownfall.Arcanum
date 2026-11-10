using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remote_physical_spend_is_visible_once_in_existing_external_daily_authority(bool priced)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger budget = new();

        f.UseBudgetParticipant(budget);

        _ = await SeedExternalRemoteSpendAsync(f, budget, priced ? 1.25m : 0m, priced ? 40 : 0, priced);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        A2AExternalSpendLedger external = new(db, TimeProvider.System, NullLogger<A2AExternalSpendLedger>.Instance);

        ExternalSpendSummary summary = await external.GetTodayAsync();

        Assert.Equal(priced ? 1.25m : 0m, summary.KnownCostUsd);

        Assert.Equal(priced ? 40 : 0, summary.KnownTokens);

        Assert.Equal(priced ? 1 : 0, summary.PricedSendings);

        Assert.Equal(priced ? 0 : 1, summary.UnpricedSendings);

        Assert.Equal(1L, await f.CountAsync("guild_budget_effects"));

        Assert.Equal(1L, await f.CountAsync("guild_budget_evidence"));
    }

    [Theory]
    [InlineData(false, "precision", false)]
    [InlineData(false, "range", false)]
    [InlineData(true, "precision", false)]
    [InlineData(true, "precision", true)]
    [InlineData(true, "range", false)]
    [InlineData(true, "range", true)]
    public async Task ExternalSpend_RemoteAndMixedTotalsRefuseUnrepresentableUsd(bool mixed, string boundary, bool largeLegacy)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger budget = new();

        f.UseBudgetParticipant(budget);

        decimal increment = boundary == "precision" ? 0.00000001m : 1m;

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        if (mixed)
        {
            await SeedExternalLegacySpendAsync(db, f, largeLegacy ? decimal.MaxValue : increment, 0);

            _ = await SeedExternalRemoteSpendAsync(f, budget, largeLegacy ? increment : decimal.MaxValue, 0);
        }
        else
        {
            _ = await SeedExternalRemoteSpendAsync(f, budget, decimal.MaxValue, 0);

            _ = await SeedExternalRemoteSpendAsync(f, budget, increment, 0);
        }

        A2AExternalSpendLedger external = new(db, TimeProvider.System, NullLogger<A2AExternalSpendLedger>.Instance);

        await Assert.ThrowsAsync<OverflowException>(() => external.GetTodayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalSpend_RemoteAndMixedTokenOverflowNeverBecomesNoSpend(bool mixed)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger budget = new();

        f.UseBudgetParticipant(budget);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        if (mixed) await SeedExternalLegacySpendAsync(db, f, 0m, long.MaxValue);
        else _ = await SeedExternalRemoteSpendAsync(f, budget, 0m, long.MaxValue);

        _ = await SeedExternalRemoteSpendAsync(f, budget, 0m, 1);

        A2AExternalSpendLedger external = new(db, TimeProvider.System, NullLogger<A2AExternalSpendLedger>.Instance);

        await Assert.ThrowsAsync<OverflowException>(() => external.GetTodayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalSpend_RemoteAndMixedRepresentableSubcentTotalsRemainExact(bool mixed)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger budget = new();

        f.UseBudgetParticipant(budget);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        if (mixed) await SeedExternalLegacySpendAsync(db, f, 0.00000001m, 7);
        else _ = await SeedExternalRemoteSpendAsync(f, budget, 0.00000001m, 7);

        _ = await SeedExternalRemoteSpendAsync(f, budget, 0.00000002m, 8);

        A2AExternalSpendLedger external = new(db, TimeProvider.System, NullLogger<A2AExternalSpendLedger>.Instance);

        Assert.Equal(new ExternalSpendSummary(0.00000003m, 15, 2, 0), await external.GetTodayAsync());
    }

    [Theory]
    [InlineData("not-a-decimal")]
    [InlineData("79228162514264337593543950335.1")]
    public async Task ExternalSpend_MalformedPhysicalValueRemainsUnknownWithoutErasingHealthyRows(string malformed)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger budget = new();

        f.UseBudgetParticipant(budget);

        GuildQuestStored bad = await SeedExternalRemoteSpendAsync(f, budget, 0.5m, 99);

        _ = await SeedExternalRemoteSpendAsync(f, budget, 0.25m, 5);

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            // Stage an installed malformed physical value with a current evidence revision.
            // Legitimate writes use the typed ledger; this tests its existing reader quarantine policy.
            await using SqliteCommand corrupt = connection.CreateCommand();

            corrupt.Transaction = transaction;

            corrupt.CommandText = """
                UPDATE guild_budget_effects SET KnownUsd=$usd,Revision=Revision+1 WHERE EffectId=$effect;
                INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,
                    Complete,NoDispatchProven,AppliedRevision)
                SELECT $receipt,EffectId,Digest,$usd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven,AppliedRevision+1
                FROM guild_budget_evidence WHERE EffectId=$effect ORDER BY AppliedRevision DESC LIMIT 1
                """;

            corrupt.Parameters.AddWithValue("$usd", malformed);

            corrupt.Parameters.AddWithValue("$effect", bad.Quest.Id.ToString("D").ToUpperInvariant());

            corrupt.Parameters.AddWithValue("$receipt", Guid.NewGuid().ToString("D").ToUpperInvariant());

            Assert.Equal(2, await corrupt.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        A2AExternalSpendLedger external = new(db, TimeProvider.System, NullLogger<A2AExternalSpendLedger>.Instance);

        Assert.Equal(new ExternalSpendSummary(0.25m, 5, 1, 1), await external.GetTodayAsync());
    }

    private static async Task SeedExternalLegacySpendAsync(ArcanumDbContext db, Fixture f, decimal usd, long tokens)
    {
        A2ASendingLedger ledger = new(new LongRunningOperationStore(db, f.Connections), TimeProvider.System,
            NullLogger<A2ASendingLedger>.Instance);

        var entry = await ledger.RegisterOutboundAsync(Guid.NewGuid().ToString("N"), "https://peer.example.test/");

        Assert.True(entry.IsRecorded);

        await ledger.SettleOutboundAsync(entry, A2ARemoteCost.Known(tokens, usd));
    }

    private static async Task<GuildQuestStored> SeedExternalRemoteSpendAsync(Fixture f, GuildBudgetLedger budget,
        decimal usd, long tokens, bool priced = true)
    {
        GuildIssueRootRequest request = f.Request with { IdempotencyKey = Guid.NewGuid() };

        var issued = await f.Store.IssueAsync(f.Caller, f.Identity with { LogicalCallId = request.IdempotencyKey,
            IssuingStepId = Guid.NewGuid() }, request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestStored row = issued.Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatchId = Guid.NewGuid();

        var prepared = await f.Store.PrepareDispatchAsync(driver, dispatchId, ReceiptDigest("external-spend"), row.Quest.RunId, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            var effect = await budget.PrepareEffectWithinAsync(connection, transaction,
                new(row.Quest.Id, row.Quest.Id, dispatchId, row.Quest.RunId, GuildBudgetEffectKind.External, row.Quest.Id, null, null, null), default);

            Assert.True(effect.IsSuccess, effect.Error.Message);

            await using SqliteCommand correlate = connection.CreateCommand();

            correlate.Transaction = transaction;

            correlate.CommandText = "INSERT INTO guild_remote_effects(EffectId,QuestId,DispatchId,SettledAtUtc) VALUES($quest,$quest,$dispatch,$now)";

            correlate.Parameters.AddWithValue("$quest", row.Quest.Id.ToString("D").ToUpperInvariant());
            correlate.Parameters.AddWithValue("$dispatch", dispatchId.ToString("D").ToUpperInvariant());
            correlate.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

            _ = await correlate.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }

        var exposed = await f.Store.MarkPossiblyDispatchedAsync(driver, dispatchId, prepared.Value.Revision, default);

        Assert.True(exposed.IsSuccess, exposed.Error.Message);

        GuildBudgetEvidence receipt = new(Guid.NewGuid(), ReceiptDigest("remote receipt"), usd,
            tokens, !priced, !priced, true);

        for (int replay = 0; replay < 2; replay++)
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            var applied = await budget.ApplyEvidenceWithinAsync(connection, transaction, row.Quest.Id, receipt, default);

            Assert.True(applied.IsSuccess, applied.Error.Message);

            await transaction.CommitAsync();
        }

        return row;
    }

}
