using System.Data;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Eight-writer contention against the canonical tier, over genuinely separate connections.
/// </summary>
public sealed class CovenantMutationConcurrencyTests
{
    private const int Writers = 8;

    private const string LostLockRaceCode = "Test.WriterLostLockRace";

    private const int SqliteBusy = 5;

    private const int SqliteLocked = 6;

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Eight_writers_on_distinct_keys_all_commit_with_distinct_sequences()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Task<Result<IReadOnlyList<CovenantMutationReceipt>>>[] writers =
        [
            .. Enumerable.Range(0, Writers).Select(index => Task.Run(
                () => WriteAsync(
                    fixture,
                    CovenantMutationFixture.Batch(
                        generation,
                        CovenantMutationFixture.OperatorSet(
                            CovenantOperationScope.Global,
                            $"contended.key{index}",
                            $"Value {index}.",
                            0,
                            0)),
                    Token),
                Token)),
        ];

        Result<IReadOnlyList<CovenantMutationReceipt>>[] results = await Task.WhenAll(writers);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null));

        Assert.Equal(Writers, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_heads;"));

        Assert.Equal(Writers, await ScalarAsync(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"));

        // Every batch got its own sequence and its own projection row: no two writers shared either.
        Assert.Equal(
            Writers,
            await ScalarAsync(fixture, "SELECT COUNT(DISTINCT SearchSequence) FROM covenant_search_outbox;"));

        Assert.Equal(
            Writers,
            await ScalarAsync(fixture, "SELECT COUNT(DISTINCT SearchRowId) FROM covenant_heads;"));
    }

    [Fact]
    public async Task Eight_writers_on_one_key_produce_exactly_one_winner_per_revision()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Task<Result<IReadOnlyList<CovenantMutationReceipt>>>[] writers =
        [
            .. Enumerable.Range(0, Writers).Select(index => Task.Run(
                () => WriteAsync(
                    fixture,
                    CovenantMutationFixture.Batch(
                        generation,
                        CovenantMutationFixture.OperatorSet(
                            CovenantOperationScope.Global,
                            "contended.single",
                            $"Value {index}.",
                            expectedRevision: 0,
                            expectedKeyEpoch: 0)),
                    Token),
                Token)),
        ];

        Result<IReadOnlyList<CovenantMutationReceipt>>[] results = await Task.WhenAll(writers);

        _ = Assert.Single(results, static result => result.IsSuccess);

        // Every loser carries the kernel's own typed refusal and nothing else. Writers queue on the
        // write lock, so a loser meets the winner's committed state, and every head write advances the
        // key's epoch: the epoch guard is the first the kernel applies, and it answers StaleSnapshot.
        // Neither a lost lock race nor an exception from the kernel's defensive fallbacks (a head that
        // changed between its compare and its swap, a search sequence that moved) is an acceptable
        // loser here, because either would mean the guards were bypassed and the writer was stopped
        // only by luck. The revision comparison itself is pinned where the epoch matches and the
        // revision does not, by CovenantMutationKernelTests.A_stale_expected_revision_fails_without_mutating.
        Assert.All(
            results.Where(static result => result.IsFailure),
            result => Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code));

        // The losers wrote nothing: one head, one version, one receipt, one outbox row, one sequence.
        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_heads;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_versions;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_mutation_receipts;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_search_outbox;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"));
    }

    [Fact]
    public async Task A_rolled_back_batch_leaves_no_row_behind()
    {
        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        Guid generation = await fixture.ReadDatasetGenerationAsync(Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await CovenantMutationFixture.ApplyAsync(
            fixture,
            CovenantMutationFixture.Batch(
                generation,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    "rolled.back",
                    "Never committed.",
                    0,
                    0)),
            Token,
            commit: false);

        Assert.True(applied.IsSuccess);

        // The kernel reported success and wrote nothing durable, because the transaction it was
        // handed was the caller's to commit and the caller chose not to.
        Assert.Equal(0, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_entries;"));

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_versions;"));

        Assert.Equal(0, await ScalarAsync(fixture, "SELECT CanonicalSearchSequence FROM covenant_state;"));
    }

    private static async Task<Result<IReadOnlyList<CovenantMutationReceipt>>> WriteAsync(
        CovenantCanonicalFixture fixture,
        CovenantMutationBatch batch,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await fixture.OpenAdditionalConnectionAsync(cancellationToken);

        try
        {
            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            CovenantMutationTransaction owned = new(connection, transaction);

            Result<IReadOnlyList<CovenantMutationReceipt>> receipts =
                await new CovenantMutationKernel(new CovenantQuotaGuard(), MemoryErasureTestKeys.Isolated())
                    .ApplyBatchAsync(batch, owned, CovenantAgentErasureGate.None, cancellationToken);

            if (receipts.IsSuccess)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return receipts;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            // Only a writer that lost the lock itself is reported as a lost race. Anything else the
            // kernel or the database throws, a constraint failure or an InvalidOperationException from
            // a defensive fallback included, escapes and fails the test: mapping it to an expected
            // refusal would let a loser that was stopped by a constraint look like one the kernel's
            // own checks refused.
            return new Error(LostLockRaceCode, exception.Message);
        }
    }

    private static async Task<long> ScalarAsync(CovenantCanonicalFixture fixture, string sql)
    {
        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(Token);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
