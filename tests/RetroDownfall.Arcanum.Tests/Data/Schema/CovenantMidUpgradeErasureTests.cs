using System.Data;
using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// An agent proposal published while Core version 12's sweep is still pending, which is the live
/// upgrade window the Covenant erasure chokepoint has to keep open.
/// </summary>
/// <remarks>
/// <para>Built the way an upgrade produces it, as the Saga and Lexicon cases are: install Core version
/// 11 beside the canonical tier at its head, write one Saga memory so the version-12 sweep has
/// something to work through, then hand the installer the shipped chain once. Version 12 declares a
/// backfill, so that call commits its DDL and stops with 11 recorded, and the fingerprint table does
/// not exist yet.</para>
///
/// <para>Evidence can only be committed at Core 13, so a catalog below it holds none and the kernel
/// needs no key. The case asserts that state before it writes, then proves the write committed, that
/// the gate classified it clear inside the same transaction, and that nothing asked the credential
/// store.</para>
/// </remarks>
public sealed class CovenantMidUpgradeErasureTests
{

    private const int TestDimensions = 64;

    private const string Key = "preference.builds";

    private static readonly Guid CampaignId = new("A0000000-0000-4000-8000-0000000000D7");

    static CovenantMidUpgradeErasureTests() => SqliteNativeRuntime.Instance.Initialize();

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task An_agent_proposal_while_the_version_twelve_sweep_is_pending_needs_no_erasure_key()
    {

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(Token);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionElevenFixture.ChainSet(),
            TestDimensions,
            Token);

        Assert.Equal(11, installed.Core.SchemaVersion);

        await using (ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file))
        {

            _ = await SagaMemoryMidUpgradeWriteTests.WriteAsync(
                SagaMemoryMidUpgradeWriteTests.CreateStore(db),
                Guid.NewGuid(),
                "a conclusion the version twelve sweep has to reach");

        }

        // One call, which leaves version 12's DDL committed and its sweep still pending.
        GrimoireSchemaInstallResult upgraded = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            GrimoireSchemaVersionChains.Default,
            TestDimensions,
            Token);

        Assert.Equal(11, upgraded.Core.SchemaVersion);

        Assert.Equal(
            0L,
            await ScalarLongAsync(
                connection,
                null,
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'memory_erasure_fingerprints';"));

        Assert.Equal(
            1L,
            await ScalarLongAsync(
                connection,
                null,
                "SELECT COUNT(*) FROM pragma_table_info('covenant_mutation_receipts') WHERE name = 'EntryId';"));

        CountingOsCredentialStore credentials = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keyring = MemoryErasureTestKeys.Isolated(credentials);

        CovenantMutationKernel kernel = new(new CovenantQuotaGuard(), keyring);

        using CovenantAgentErasureGate gate = kernel.CaptureErasureGate();

        Assert.Null(gate.Key);

        CovenantMutationBatch batch = new(
            await ScalarGuidBlobAsync(connection, "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;"),
            await ScalarLongAsync(connection, null, "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;"),
            expectedCampaignRegistryEpoch: null,
            CovenantMutationFixture.CommitTime,
            [
                CovenantMutationFixture.AgentPropose(
                    CampaignId,
                    Key,
                    "The model suggests building from the root.",
                    expectedRevision: 0,
                    expectedKeyEpoch: 0),
            ]);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> published = await kernel.ApplyBatchAsync(
            batch,
            new CovenantMutationTransaction(connection, transaction),
            gate,
            Token);

        Assert.True(published.IsSuccess, published.IsFailure ? published.Error.Message : string.Empty);

        CovenantMutationReceipt receipt = Assert.Single(published.Value);

        Assert.Equal(CovenantMutationOutcome.Applied, receipt.Outcome);

        Assert.Equal(
            CovenantAgentErasureState.Clear,
            await gate.ClassifyAsync(connection, transaction, CovenantScope.Campaign, CampaignId, Key, Token));

        await transaction.CommitAsync(Token);

        Assert.Equal(
            1L,
            await ScalarLongAsync(connection, null, "SELECT COUNT(*) FROM covenant_mutation_receipts WHERE OutcomeCode = 1;"));

        Assert.Null(gate.Key);

        Assert.Equal(0, credentials.Calls);

    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);

    }

    private static async Task<Guid> ScalarGuidBlobAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return new Guid((byte[])(await command.ExecuteScalarAsync(Token))!);

    }

}
