using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// The Entry delete asks the labelled-artifact guard inside its own write transaction, and refuses when
/// the label table cannot be read.
/// </summary>
/// <remarks>
/// <para>A check made before the transaction opens and a delete made inside it are two moments, and a
/// label written between them is removed with its Entry and leaves nothing behind. The second writer
/// here is the proof: it tries to label the Entry on its own connection right after the guard answers,
/// which is where a label written between the check and the delete would land, and it is blocked by the
/// write lock the delete's transaction holds. An Entry delete that asked outside its transaction would
/// let that label commit and then delete the Entry under it.</para>
///
/// <para>A guard that answered "nothing is labelled" whenever the label table could not be read would let
/// the delete through on a damaged Grimoire, so an unreadable table refuses it, rolls the transaction
/// back, and the Entry stays (§10.20.2).</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class EntryLabeledDeleteTransactionTests(GrimoireFixture fixture) : IAsyncLifetime
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
    public async Task A_delete_asks_the_guard_inside_its_transaction_so_no_label_slips_in_before_the_delete()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LabelIntruder intruder = new(
            _ => Task.FromResult((SqliteConnection)_sibling.Database.GetDbConnection()),
            SensitiveArtifactKind.AssistantEntry);

        GrimoireRepository repository = Repository(new LabelIntrusionGuard(FixtureLabeledArtifactGuard.For(_db), intruder));

        (Guid sessionId, Guid entryId) = await CreateEntryAsync(repository);

        intruder.ArtifactId = entryId;

        Assert.True(await repository.DeleteEntryAsync(sessionId, entryId, Token));

        Assert.Equal(1, intruder.Attempts);

        Assert.Equal(1, intruder.Blocked);

        Assert.Equal(0L, await CountEntryAsync(entryId));

        // No label survives the Entry it names.
        Assert.Equal(0L, await CountAsync("artifact_sensitivity"));

        Assert.Equal(1, intruder.AskedInsideTransaction);

        Assert.Equal(0, intruder.AskedOutsideTransaction);

    }

    [SkippableFact]
    public async Task A_delete_that_cannot_read_the_label_table_refuses_and_keeps_the_entry()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = Repository(FixtureLabeledArtifactGuard.For(_db));

        (Guid sessionId, Guid entryId) = await CreateEntryAsync(repository);

        await using (SqliteCommand command = ((SqliteConnection)_db.Database.GetDbConnection()).CreateCommand())
        {

            command.CommandText = "CREATE TEMP TABLE artifact_sensitivity (Unreadable INTEGER);";

            _ = await command.ExecuteNonQueryAsync(Token);

        }

        LabeledArtifactRefusalException refused = await Assert.ThrowsAsync<LabeledArtifactRefusalException>(
            () => repository.DeleteEntryAsync(sessionId, entryId, Token));

        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);

        Assert.Equal(1L, await CountEntryAsync(entryId));

    }

    private GrimoireRepository Repository(ICovenantLabeledArtifactTransactionGuard guard) =>
        new(
            _db,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            attachmentIndex: null,
            covenantKernel: null,
            availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(_db),
            guard);

    private static async Task<(Guid SessionId, Guid EntryId)> CreateEntryAsync(GrimoireRepository repository)
    {

        (Guid sessionId, Guid entryId) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "delete this entry under a label race",
            model: "test-model",
            cancellationToken: Token);

        await repository.FinalizeAssistantEntryAsync(entryId, "the reply that is deleted", Token);

        return (sessionId, entryId);

    }

    private async Task<long> CountEntryAsync(Guid entryId)
    {

        await using SqliteCommand command = ((SqliteConnection)_db.Database.GetDbConnection()).CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM Entries WHERE lower(replace(Id, '-', '')) = $id;";

        _ = command.Parameters.AddWithValue("$id", entryId.ToString("N"));

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);

    }

    private async Task<long> CountAsync(string table)
    {

        await using SqliteCommand command = ((SqliteConnection)_db.Database.GetDbConnection()).CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table};";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);

    }

}
