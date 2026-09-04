using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The one connection a closed period keeps working, and the policy it may never be opened without.
/// </summary>
/// <remarks>
/// A Covenant erasure closes ordinary admission and then opens the operation store's own connection
/// itself, under a scoped permit from the gate. Entity Framework's connection interceptors fire for
/// connections Entity Framework opens, so that route runs none of them — and the two pragmas it would
/// silently drop are the ones an erasure's own deletions depend on. Without <c>secure_delete</c> the
/// erased bytes stay readable in the freed pages; without <c>foreign_keys</c> a factory erasure leaves
/// every cascade-only child row behind. Both while reporting a proven erasure.
///
/// <para>Asserted here against a real database rather than only through the coordinator, because the
/// coordinator's own suite can prove it reaches this component and not that this component does
/// anything when it gets there.</para>
/// </remarks>
public sealed class CovenantClosedPeriodLedgerConnectionTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Opening_the_ledger_applies_and_proves_the_pragmas_an_erasure_depends_on()
    {

        await using CovenantSchemaScratchDatabase scratch =
            await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await using ArcanumDbContext context = CreateContext(scratch);

        CovenantClosedPeriodLedgerConnection ledger = new(
            context,
            CovenantSqliteConnectionInitializer.Instance);

        await ledger.OpenAsync(Token);

        try
        {

            Assert.Equal(System.Data.ConnectionState.Open, ledger.Connection.State);

            Assert.Equal(1L, await ScalarLongAsync(ledger, "PRAGMA foreign_keys;"));

            Assert.Equal(1L, await ScalarLongAsync(ledger, "PRAGMA secure_delete;"));

        }
        finally
        {

            await ledger.Connection.CloseAsync();

        }

    }

    /// <summary>
    /// A second open of an already-open ledger is a no-op rather than a second initialization.
    /// </summary>
    /// <remarks>
    /// The window is opened and closed around each durable step, and a step that found the connection
    /// already open would otherwise re-run the policy over a handle that is mid-transaction. What
    /// matters is that the connection is still usable and still carries the policy afterwards.
    /// </remarks>
    [Fact]
    public async Task Opening_a_ledger_that_is_already_open_leaves_it_open_and_configured()
    {

        await using CovenantSchemaScratchDatabase scratch =
            await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await using ArcanumDbContext context = CreateContext(scratch);

        CovenantClosedPeriodLedgerConnection ledger = new(
            context,
            CovenantSqliteConnectionInitializer.Instance);

        await ledger.OpenAsync(Token);

        try
        {

            await ledger.OpenAsync(Token);

            Assert.Equal(System.Data.ConnectionState.Open, ledger.Connection.State);

            Assert.Equal(1L, await ScalarLongAsync(ledger, "PRAGMA secure_delete;"));

        }
        finally
        {

            await ledger.Connection.CloseAsync();

        }

    }

    private static async Task<long> ScalarLongAsync(
        CovenantClosedPeriodLedgerConnection ledger,
        string sql)
    {

        await using SqliteCommand command = ((SqliteConnection)ledger.Connection).CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(Token),
            System.Globalization.CultureInfo.InvariantCulture);

    }

    private static ArcanumDbContext CreateContext(CovenantSchemaScratchDatabase scratch)
    {

        DbContextOptions<ArcanumDbContext> options = new DbContextOptionsBuilder<ArcanumDbContext>()
            .UseSqlite(
                new SqliteConnectionStringBuilder
                {
                    DataSource = scratch.DatabasePath,

                    Password = CovenantSchemaScratchDatabase.ScratchPassphrase,

                    Pooling = false,
                }.ToString())
            .UseModel(RetroDownfall.Arcanum.Infrastructure.Generated.ArcanumDbContextModel.Instance)
            .Options;

        return new ArcanumDbContext(options, new UnusedSecretStore(), new ScratchPassphrase());

    }

    /// <summary>The context takes one and this suite never reaches a path that reads it.</summary>
    private sealed class UnusedSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class ScratchPassphrase : IGrimoireDbPassphraseSource
    {

        public string Passphrase => CovenantSchemaScratchDatabase.ScratchPassphrase;

        public void SetPassphrase(string passphrase) =>
            throw new NotSupportedException(
                "This suite's passphrase is the scratch database's own and is never replaced.");

    }

}
