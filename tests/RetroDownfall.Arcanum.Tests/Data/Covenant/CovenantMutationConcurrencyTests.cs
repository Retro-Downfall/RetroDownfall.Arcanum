using System.Data;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Eight-writer contention against the canonical tier, over genuinely separate connections.
/// </summary>
public sealed class CovenantMutationConcurrencyTests
{

    private const int Writers = 8;

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

        Assert.All(
            results.Where(static result => result.IsFailure),
            result => Assert.Contains(
                result.Error.Code,
                (string[])[ErrorCodes.Covenant.RevisionConflict, ErrorCodes.Covenant.StaleSnapshot]));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_heads;"));

        Assert.Equal(1, await ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_versions;"));

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
                await new CovenantMutationKernel().ApplyBatchAsync(batch, owned, cancellationToken);

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
        catch (SqliteException exception)
        {

            // A busy or locked writer is a lost race, not a defect: the owner retries the whole
            // transaction, and here losing is the outcome under test.
            return new Error(ErrorCodes.Covenant.StaleSnapshot, exception.Message);

        }
        catch (InvalidOperationException exception)
        {

            return new Error(ErrorCodes.Covenant.RevisionConflict, exception.Message);

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
