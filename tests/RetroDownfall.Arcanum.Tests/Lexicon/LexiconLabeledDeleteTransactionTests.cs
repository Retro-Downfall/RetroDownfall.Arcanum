using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Lexicon;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

/// <summary>
/// A Lexicon delete asks the labelled-artifact guard while it holds its write lock, and refuses when the
/// label table cannot be read.
/// </summary>
/// <remarks>
/// <para>The Lexicon opens its write with <c>BEGIN IMMEDIATE</c> and asks the guard on the same
/// connection, so the guard's read is inside the transaction and no label can be committed between the
/// answer and the delete. The second writer here is the proof: it tries to label the entry right after
/// the guard answers and is blocked by the lock the delete holds.</para>
///
/// <para>A guard that answered "nothing is labelled" whenever the label table could not be read would
/// let the delete through on a damaged Grimoire, so an unreadable table refuses it and the entry stays
/// (§10.20.2).</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconLabeledDeleteTransactionTests(GrimoireFixture fixture) : IAsyncLifetime
{

    private static CancellationToken Token => CancellationToken.None;

    private string _path = string.Empty;

    private ArcanumDbContext _db = null!;

    private ArcanumDbContext _sibling = null!;

    public async Task InitializeAsync()
    {

        _path = fixture.CopyDatabase();

        _db = fixture.CreateContext(_path);

        _sibling = fixture.CreateContext(_path);

        await _db.Database.OpenConnectionAsync(Token);

        await _sibling.Database.OpenConnectionAsync(Token);

    }

    public async Task DisposeAsync()
    {

        await _sibling.DisposeAsync();

        await _db.DisposeAsync();

        File.Delete(_path);

    }

    [SkippableFact]
    public async Task A_delete_that_cannot_read_the_label_table_refuses_and_keeps_the_entry()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LexiconService service = Service(FixtureLabeledArtifactGuard.For(_db));

        _ = await UpsertAsync(service);

        await using (SqliteCommand command = ((SqliteConnection)_db.Database.GetDbConnection()).CreateCommand())
        {

            command.CommandText = "CREATE TEMP TABLE artifact_sensitivity (Unreadable INTEGER);";

            _ = await command.ExecuteNonQueryAsync(Token);

        }

        Result<bool> refused = await service.DeleteByNameAsync("Entity", LexiconScope.Global, Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

        Assert.Equal(1L, await CountAsync("lexicon_entries"));

    }

    [SkippableFact]
    public async Task A_delete_asks_the_guard_while_it_holds_its_write_lock_so_no_label_slips_in_before_the_delete()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LabelIntruder intruder = new(
            _ => Task.FromResult((SqliteConnection)_sibling.Database.GetDbConnection()),
            SensitiveArtifactKind.Lexicon);

        LexiconService service = Service(new LabelIntrusionGuard(FixtureLabeledArtifactGuard.For(_db), intruder));

        Guid id = await UpsertAsync(service);

        intruder.ArtifactId = id;

        Result<bool> deleted = await service.DeleteByNameAsync("Entity", LexiconScope.Global, Token);

        Assert.True(deleted.IsSuccess, deleted.IsFailure ? deleted.Error.Message : null);

        Assert.True(deleted.Value);

        Assert.Equal(1, intruder.Attempts);

        Assert.Equal(1, intruder.Blocked);

        Assert.Equal(0L, await CountAsync("lexicon_entries"));

        // No label survives the entry it names.
        Assert.Equal(0L, await CountAsync("artifact_sensitivity"));

    }

    private LexiconService Service(ICovenantLabeledArtifactGuard guard) =>
        new(
            _db,
            NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { Annals = false } }),
            MemoryErasureTestKeys.Isolated(),
            guard);

    private static async Task<Guid> UpsertAsync(LexiconService service)
    {

        Result<LexiconEntryDto> result = await service.UpsertAsync("Entity", "general", ["alpha"], LexiconScope.Global, Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value.Id;

    }

    private async Task<long> CountAsync(string table)
    {

        await using SqliteCommand command = ((SqliteConnection)_db.Database.GetDbConnection()).CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table};";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);

    }

}
