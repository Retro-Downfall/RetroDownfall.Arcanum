using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class ArtifactSensitivityLedgerTests
{
    [Fact]
    public async Task A_failed_deferred_commit_refuses_the_label_and_rolls_back_its_projection()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await using var setup = fixture.Connection.CreateCommand();

        setup.CommandText = "CREATE TABLE parent_probe(Id INTEGER PRIMARY KEY); CREATE TABLE child_probe(Id INTEGER REFERENCES parent_probe(Id) DEFERRABLE INITIALLY DEFERRED); CREATE TRIGGER deferred_probe AFTER INSERT ON artifact_sensitivity BEGIN INSERT INTO child_probe VALUES(42); END;";

        await setup.ExecuteNonQueryAsync();

        var result = await fixture.Ledger.LabelAsync(Tainted(SensitiveArtifactKind.Lexicon, Guid.NewGuid(), GenerationOne), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.Equal(0, await fixture.CountLabelsAsync());

        var projection = await fixture.Ledger.ReadSessionProjectionAsync(Session, CancellationToken.None);

        Assert.True(projection.IsSuccess);

        Assert.Equal(0L, projection.Value.TaintedArtifactCount);
    }

    [Fact]
    public async Task A_missing_label_table_refuses_a_caller_owned_write_without_spending_its_transaction()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await using var alter = fixture.Connection.CreateCommand();

        alter.CommandText = "ALTER TABLE artifact_sensitivity RENAME TO PRIVATE_label_table;";

        await alter.ExecuteNonQueryAsync();

        await using var transaction = fixture.Connection.BeginTransaction(deferred: false);

        var result = await ArtifactSensitivityLedger.WriteWithinAsync(fixture.Connection, transaction,
            Tainted(SensitiveArtifactKind.Lexicon, Guid.NewGuid(), GenerationOne), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.DoesNotContain("PRIVATE", result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(fixture.Connection.Handle));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task An_engine_rollback_preserves_the_label_refusal_and_never_commits_a_label()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await using var command = fixture.Connection.CreateCommand();

        command.CommandText = "CREATE TRIGGER refuse_label BEFORE INSERT ON artifact_sensitivity BEGIN SELECT RAISE(ROLLBACK, 'PRIVATE-rollback-canary'); END;";

        await command.ExecuteNonQueryAsync();

        var result = await fixture.Ledger.LabelAsync(Tainted(SensitiveArtifactKind.Lexicon, Guid.NewGuid(), GenerationOne), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.DoesNotContain("PRIVATE", result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(0, await fixture.CountLabelsAsync());
    }

    [Fact]
    public async Task A_real_full_database_is_unavailable_and_cannot_commit_an_unlabelled_artifact()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await using var limit = fixture.Connection.CreateCommand();

        limit.CommandText = "PRAGMA page_count;";

        long pages = Convert.ToInt64(await limit.ExecuteScalarAsync());

        limit.CommandText = $"PRAGMA max_page_count={pages};";

        await limit.ExecuteNonQueryAsync();

        for (int index = 0; index < 1000; index++)
        {
            long before = await fixture.CountLabelsAsync();

            var result = await fixture.Ledger.LabelAsync(Tainted(SensitiveArtifactKind.Lexicon, Guid.NewGuid(), GenerationOne), CancellationToken.None);

            if (result.IsFailure)
            {
                Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

                Assert.Contains("SQLite=13", result.Error.Message, StringComparison.Ordinal);

                Assert.Equal(before, await fixture.CountLabelsAsync());

                return;
            }
        }

        Assert.Fail("The fixed native page ceiling did not reach SQLITE_FULL.");
    }

    [Fact]
    public async Task A_native_busy_begin_refuses_labeling_without_persisting_and_can_be_retried_after_unlock()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        fixture.Connection.DefaultTimeout = 1;

        await using Microsoft.Data.Sqlite.SqliteConnection holder = new(fixture.Connection.ConnectionString);

        await holder.OpenAsync();

        await using Microsoft.Data.Sqlite.SqliteTransaction held = holder.BeginTransaction(deferred: false);

        var write = Tainted(SensitiveArtifactKind.Lexicon, Guid.NewGuid(), GenerationOne);

        var refused = await fixture.Ledger.LabelAsync(write, CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.Equal(0, await fixture.CountLabelsAsync());

        await held.RollbackAsync();

        Assert.True((await fixture.Ledger.LabelAsync(write, CancellationToken.None)).IsSuccess);
    }

    [Theory]
    [InlineData(-1, ErrorCodes.Covenant.Unavailable)]
    [InlineData(-2, ErrorCodes.Covenant.Unavailable)]
    [InlineData(5, ErrorCodes.Covenant.Unavailable)]
    [InlineData(13, ErrorCodes.Covenant.Unavailable)]
    [InlineData(1, ErrorCodes.Covenant.IntegrityFailure)]
    public async Task Label_acquisition_failure_is_contextual_and_never_echoes_storage_text(int code, string expected)
    {
        ArtifactSensitivityLedger ledger = new(new FaultingCovenantConnectionSource(code));

        Result<LabeledArtifactWriteReceipt> result = await ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.Lexicon, Guid.NewGuid(), GenerationOne), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(expected, result.Error.Code);

        Assert.DoesNotContain("PRIVATE", result.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("erasure inventory", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
