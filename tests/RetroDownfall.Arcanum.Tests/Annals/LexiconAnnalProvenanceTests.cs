using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Annals;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconAnnalProvenanceTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _path = string.Empty;

    private ArcanumDbContext _db = null!;

    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        _path = fixture.CopyDatabase();

        _db = fixture.CreateContext(_path);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        File.Delete(_path);
    }

    [SkippableFact]
    public async Task Structured_append_snapshots_ordinals_and_coordinates_independently_of_current_facts()
    {
        RequireSqlCipher();

        await SeedAsync();

        DbConnection connection = _db.Database.GetDbConnection();

        string? versionId;

        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            versionId = await AppendAsync(transaction);

            Assert.NotNull(versionId);

            await transaction.CommitAsync();
        }

        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM lexicon_annal_fact_provenance;"));

        Assert.Equal("0,2", await ScalarAsync("SELECT group_concat(FactOrdinal) FROM (SELECT FactOrdinal FROM lexicon_annal_fact_provenance ORDER BY FactOrdinal);"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('lexicon_annal_fact_provenance') WHERE name IN ('Fact', 'FactHash');"));

        await ExecuteAsync("UPDATE lexicon_entries SET FactsJson = '[\"replacement\"]', FactsText = 'replacement'; DELETE FROM lexicon_fact_attachment_provenance;");

        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM lexicon_annal_fact_provenance;"));

        Assert.Equal("source-a,source-c", await ScalarAsync("SELECT group_concat(LogicalKey) FROM (SELECT LogicalKey FROM lexicon_annal_fact_provenance ORDER BY FactOrdinal);"));

        Assert.Equal("attachment-a,attachment-c", await ScalarAsync("SELECT group_concat(AttachmentContentHash) FROM (SELECT AttachmentContentHash FROM lexicon_annal_fact_provenance ORDER BY FactOrdinal);"));

        IReadOnlyList<LexiconAnnalFactProvenance> history = await new AnnalsStore(_db)
            .GetLexiconFactProvenanceAsync(versionId!, CancellationToken.None);

        Assert.Equal(
            new LexiconAnnalFactProvenance(versionId!, 0,
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                "source-a", 2, "attachment-a", At, "text"),
            history[0]);

        Assert.Equal(
            new LexiconAnnalFactProvenance(versionId!, 2,
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Guid.Parse("00000000-0000-0000-0000-000000000003"),
                "source-c", 3, "attachment-c", At, "text"),
            history[1]);

        Assert.Empty(await new AnnalsStore(_db).GetLexiconFactProvenanceAsync("absent", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Repeating_a_snapshot_appends_neither_a_version_nor_duplicate_evidence()
    {
        RequireSqlCipher();

        await SeedAsync();

        await using (DbTransaction transaction = await _db.Database.GetDbConnection().BeginTransactionAsync())
        {
            Assert.NotNull(await AppendAsync(transaction));

            Assert.Null(await AppendAsync(transaction));

            await transaction.CommitAsync();
        }

        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM annal_versions;"));

        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM lexicon_annal_fact_provenance;"));
    }

    [SkippableFact]
    public async Task Evidence_insert_failure_rolls_back_the_owning_change_and_claim()
    {
        RequireSqlCipher();

        await SeedAsync();

        await ExecuteAsync("CREATE TRIGGER reject_history BEFORE INSERT ON lexicon_annal_fact_provenance BEGIN SELECT RAISE(ABORT, 'evidence rejected'); END;");

        await using (DbTransaction transaction = await _db.Database.GetDbConnection().BeginTransactionAsync())
        {
            await ExecuteAsync("UPDATE lexicon_entries SET Type = 'corrected';", transaction);

            SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => AppendAsync(transaction));

            Assert.Contains("evidence rejected", failure.Message);

            await transaction.RollbackAsync();
        }

        Assert.Equal("Project", await ScalarAsync("SELECT Type FROM lexicon_entries;"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM annal_claims;"));

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM lexicon_annal_fact_provenance;"));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_evidence_cannot_be_published_without_an_owning_transaction(bool assertClaim)
    {
        RequireSqlCipher();

        await SeedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => AppendAsync(transaction: null, assertClaim));

        Assert.Equal("0,0,0,0", await ReadPublicationCountsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_managed_transaction_cannot_publish_structured_evidence(bool assertClaim)
    {
        RequireSqlCipher();

        await SeedAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await using DbTransaction transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync("COMMIT;", transaction);

        try
        {
            Assert.Same(connection, transaction.Connection);

            Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));

            Exception? failure = await Record.ExceptionAsync(() => AppendAsync(transaction, assertClaim));

            Assert.Equal("0,0,0,0", await ReadPublicationCountsAsync(transaction));

            Assert.IsType<InvalidOperationException>(failure);
        }
        finally
        {
            // Raw COMMIT leaves the provider object live; give its cleanup an empty transaction.
            await ExecuteAsync("BEGIN IMMEDIATE;", transaction);

            await transaction.RollbackAsync();
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_provider_transaction_owns_structured_evidence(bool assertClaim)
    {
        RequireSqlCipher();

        await SeedAsync();

        await using DbTransaction transaction = await _db.Database.GetDbConnection().BeginTransactionAsync();

        Assert.NotNull(await AppendAsync(transaction, assertClaim));

        Assert.Equal("1,1,1,2", await ReadPublicationCountsAsync(transaction));

        await transaction.RollbackAsync();

        Assert.Equal("0,0,0,0", await ReadPublicationCountsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_EF_transaction_owns_structured_evidence(bool assertClaim)
    {
        RequireSqlCipher();

        await SeedAsync();

        await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync();

        Assert.NotNull(await AppendAsync(transaction.GetDbTransaction(), assertClaim));

        Assert.Equal("1,1,1,2", await ReadPublicationCountsAsync(transaction.GetDbTransaction()));

        await transaction.RollbackAsync();

        Assert.Equal("0,0,0,0", await ReadPublicationCountsAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_connections_transaction_cannot_authorize_structured_evidence(bool assertClaim)
    {
        RequireSqlCipher();

        await SeedAsync();

        await using SqliteConnection otherConnection = new("Data Source=:memory:");

        await otherConnection.OpenAsync();

        await using DbTransaction otherTransaction = await otherConnection.BeginTransactionAsync();

        await ExecuteAsync("BEGIN IMMEDIATE;");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => AppendAsync(otherTransaction, assertClaim));

            Assert.Equal("0,0,0,0", await ReadPublicationCountsAsync());
        }
        finally
        {
            await ExecuteAsync("ROLLBACK;");
        }
    }

    private Task<string?> AppendAsync(DbTransaction? transaction, bool assertClaim = false) =>
        assertClaim
        ? AnnalsClaimWriter.AppendAssertAsync(
            _db.Database.GetDbConnection(), transaction, AnnalSubjectStore.Lexicon, "entry", AnnalOrigin.OperatorStated,
            SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            AnnalContentHashFormat.LexiconStructuredSnapshot, new byte[32], At, At, null, CancellationToken.None)
        : AnnalsClaimWriter.AppendCorrectionAsync(
            _db.Database.GetDbConnection(), transaction, AnnalSubjectStore.Lexicon, "entry", AnnalOrigin.OperatorStated,
            SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
            AnnalContentHashFormat.LexiconStructuredSnapshot, new byte[32], At, At, null, CancellationToken.None);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_owning_transaction_keeps_the_version_and_evidence_uncommitted_until_its_owner_finishes(bool assertClaim)
    {
        RequireSqlCipher();

        await SeedAsync();

        await ExecuteAsync("BEGIN IMMEDIATE;");

        try
        {
            Assert.NotNull(await AppendAsync(transaction: null, assertClaim));

            Assert.Equal("1,1,1,2", await ReadPublicationCountsAsync());
        }
        finally
        {
            await ExecuteAsync("ROLLBACK;");
        }

        Assert.Equal("0,0,0,0", await ReadPublicationCountsAsync());
    }

    private Task<object?> ReadPublicationCountsAsync(DbTransaction? transaction = null) =>
        ScalarAsync("""
            SELECT (SELECT COUNT(*) FROM annal_claims) || ','
                || (SELECT COUNT(*) FROM annal_versions) || ','
                || (SELECT COUNT(*) FROM annal_heads) || ','
                || (SELECT COUNT(*) FROM lexicon_annal_fact_provenance);
            """, transaction);

    private async Task SeedAsync()
    {
        await _db.Database.OpenConnectionAsync();

        await ExecuteAsync("""
            INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
            VALUES ('entry', 'name', 'name', 'Project', '["alpha","operator fact","gamma"]', 'alpha operator fact gamma', '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO lexicon_fact_attachment_provenance
                (EntryId, FactHash, Fact, SessionId, AttachmentId, LogicalKey, Version, ContentHash, MaterializedAt, SourceType)
            VALUES
                ('entry', 'fact-gamma-secret-hash', 'gamma', '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000003', 'source-c', 3, 'attachment-c', '2026-01-01T00:00:00.0000000+00:00', 'text'),
                ('entry', 'fact-alpha-secret-hash', 'alpha', '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002', 'source-a', 2, 'attachment-a', '2026-01-01T00:00:00.0000000+00:00', 'text');
            """);
    }

    private async Task ExecuteAsync(string sql, DbTransaction? transaction = null)
    {
        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, DbTransaction? transaction = null)
    {
        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
}
