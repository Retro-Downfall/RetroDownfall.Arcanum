using System.Data.Common;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Annals;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconAnnalFormatTests(GrimoireFixture fixture) : IAsyncLifetime
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
    public async Task Equal_digest_bytes_with_different_formats_append_a_correction()
    {
        RequireSqlCipher();

        await _db.Database.OpenConnectionAsync();

        DbConnection connection = _db.Database.GetDbConnection();

        await using DbTransaction transaction = await connection.BeginTransactionAsync();

        Assert.True(await AppendLegacyAsync(connection, transaction));

        await SetFormatAsync(connection, transaction, 2);

        Assert.True(await AppendLegacyAsync(connection, transaction));
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(4294967297L)]
    public async Task Invalid_persisted_formats_fail_closed_for_reads_and_corrections(long code)
    {
        RequireSqlCipher();

        await _db.Database.OpenConnectionAsync();

        DbConnection connection = _db.Database.GetDbConnection();

        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await AppendLegacyAsync(connection, transaction));

            await SetFormatAsync(connection, transaction, code);

            await transaction.CommitAsync();
        }

        AnnalsStore store = new(_db);

        AnnalClaimHead head = (await store.GetClaimAsync(AnnalSubjectStore.Lexicon, "entry", CancellationToken.None))!;

        await using DbTransaction correction = await connection.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => AppendLegacyAsync(connection, correction));

        await correction.RollbackAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetVersionsAsync(head.ClaimId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Structured_versions_round_trip_the_exact_inserted_identity_format_and_digest()
    {
        RequireSqlCipher();

        await _db.Database.OpenConnectionAsync();

        DbConnection connection = _db.Database.GetDbConnection();

        string? versionId;

        byte[] digest = Convert.FromHexString("1D1CA295A1538D2AEE3794E497EF7766CAA7038BD6D953324A16F0D3B4F422EB");

        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            versionId = await AnnalsClaimWriter.AppendCorrectionAsync(
                connection, transaction, AnnalSubjectStore.Lexicon, "entry", AnnalOrigin.OperatorStated,
                SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
                AnnalContentHashFormat.LexiconStructuredSnapshot, digest, At, At, null, CancellationToken.None);

            Assert.NotNull(versionId);

            Assert.Null(await AnnalsClaimWriter.AppendCorrectionAsync(
                connection, transaction, AnnalSubjectStore.Lexicon, "entry", AnnalOrigin.OperatorStated,
                SagaMemoryScopeKind.Global, null, ContentSensitivity.None,
                AnnalContentHashFormat.LexiconStructuredSnapshot, digest, At, At, null, CancellationToken.None));

            await transaction.CommitAsync();
        }

        AnnalsStore store = new(_db);

        AnnalClaimHead head = (await store.GetClaimAsync(AnnalSubjectStore.Lexicon, "entry", CancellationToken.None))!;

        AnnalClaimVersion version = Assert.Single(await store.GetVersionsAsync(head.ClaimId, CancellationToken.None));

        Assert.Equal(versionId, head.CurrentVersionId);

        Assert.Equal(versionId, version.VersionId);

        Assert.Equal(AnnalContentHashFormat.LexiconStructuredSnapshot, version.ContentHashFormat);

        Assert.Equal(digest, version.ContentHash);
    }

    [SkippableFact]
    public async Task Tombstones_have_no_content_and_remain_idempotent_across_formats()
    {
        RequireSqlCipher();

        await _db.Database.OpenConnectionAsync();

        DbConnection connection = _db.Database.GetDbConnection();

        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await AppendLegacyAsync(connection, transaction));

            Assert.True(await RetireAsync(connection, transaction));

            await SetFormatAsync(connection, transaction, 2);

            Assert.False(await RetireAsync(connection, transaction));

            await transaction.CommitAsync();
        }

        AnnalsStore store = new(_db);

        AnnalClaimHead head = (await store.GetClaimAsync(AnnalSubjectStore.Lexicon, "entry", CancellationToken.None))!;

        IReadOnlyList<AnnalClaimVersion> versions = await store.GetVersionsAsync(head.ClaimId, CancellationToken.None);

        Assert.Equal(2, versions.Count);

        Assert.Equal(AnnalOperation.Retire, versions[1].Operation);

        Assert.Null(versions[1].ContentHash);
    }

    private static Task<bool> RetireAsync(DbConnection connection, DbTransaction transaction) =>
        AnnalsClaimWriter.AppendRetirementAsync(
            connection, transaction, AnnalSubjectStore.Lexicon, "entry", AnnalOrigin.OperatorStated,
            SagaMemoryScopeKind.Global, null, ContentSensitivity.None, At, At, null, CancellationToken.None);

    [SkippableFact]
    public async Task Legacy_rows_without_an_explicit_format_materialize_as_format_one()
    {
        RequireSqlCipher();

        await _db.Database.OpenConnectionAsync();

        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = """
            INSERT INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
            VALUES ('legacy-claim', 2, 'legacy-entry', '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO annal_versions (VersionId, ClaimId, Revision, OperationCode, OriginCode,
                ScopeKindCode, SensitivityCode, ContentHash, ValidFromUtc, RecordedAtUtc)
            VALUES ('legacy-version', 'legacy-claim', 1, 1, 4, 1, 0,
                X'0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20',
                '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
            """;

        await command.ExecuteNonQueryAsync();

        AnnalClaimVersion version = Assert.Single(await new AnnalsStore(_db)
            .GetVersionsAsync("legacy-claim", CancellationToken.None));

        Assert.Equal(AnnalContentHashFormat.LegacyStoreDigest, version.ContentHashFormat);

        Assert.Equal(Convert.FromHexString("0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20"), version.ContentHash);
    }

    [SkippableTheory]
    [InlineData("OriginCode", 0)]
    [InlineData("OriginCode", 4294967297L)]
    [InlineData("ScopeKindCode", 4)]
    [InlineData("SensitivityCode", 256)]
    [InlineData("OperationCode", 4)]
    public async Task Other_persisted_enum_codes_cannot_be_truncated_or_materialized_as_authority(string column, long code)
    {
        RequireSqlCipher();

        await _db.Database.OpenConnectionAsync();

        DbConnection connection = _db.Database.GetDbConnection();

        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await AppendLegacyAsync(connection, transaction));

            await transaction.CommitAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = $"DROP TRIGGER annal_versions_guard_update; PRAGMA foreign_keys = OFF; PRAGMA ignore_check_constraints = ON; UPDATE annal_versions SET {column} = @code;";

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@code";

        parameter.Value = code;

        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync();

        AnnalsStore store = new(_db);

        AnnalClaimHead head = (await store.GetClaimAsync(AnnalSubjectStore.Lexicon, "entry", CancellationToken.None))!;

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetVersionsAsync(head.ClaimId, CancellationToken.None));
    }

    private static Task<bool> AppendLegacyAsync(DbConnection connection, DbTransaction transaction) =>
        AnnalsClaimWriter.AppendCorrectionAsync(
            connection, transaction, AnnalSubjectStore.Lexicon, "entry", AnnalOrigin.AgentAsserted,
            SagaMemoryScopeKind.Global, null, ContentSensitivity.None, new byte[32], At, At, null,
            CancellationToken.None);

    [SkippableFact]
    public async Task Fractional_persisted_format_cannot_be_truncated_to_a_known_code()
    {
        RequireSqlCipher();

        await _db.Database.OpenConnectionAsync();

        DbConnection connection = _db.Database.GetDbConnection();

        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await AppendLegacyAsync(connection, transaction));

            await SetFormatAsync(connection, transaction, 1.5);

            await transaction.CommitAsync();
        }

        AnnalsStore store = new(_db);

        AnnalClaimHead head = (await store.GetClaimAsync(AnnalSubjectStore.Lexicon, "entry", CancellationToken.None))!;

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetVersionsAsync(head.ClaimId, CancellationToken.None));
    }

    private static async Task SetFormatAsync(DbConnection connection, DbTransaction transaction, object code)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "DROP TRIGGER annal_versions_guard_update; PRAGMA ignore_check_constraints = ON; UPDATE annal_versions SET ContentHashFormatCode = @code;";

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@code";

        parameter.Value = code;

        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync();
    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
}
